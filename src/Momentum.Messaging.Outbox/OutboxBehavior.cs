using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using Momentum.Messaging.Abstractions;

namespace Momentum.Messaging.Outbox;

/// <summary>
/// Pipeline behavior that manages outbox transactions.
///
/// Detects whether the handler is [Transactional] or not and behaves accordingly:
///
///   [Transactional] handler:
///     - Begins or joins the ambient DB transaction
///     - Outbox messages are collected into the ambient collection
///     - Only the outermost transactional handler commits
///     - Failure rolls back ALL transactional work + outbox messages
///
///   Non-transactional handler:
///     - Gets its own independent transaction
///     - Outbox messages flush and commit immediately when the handler completes
///     - NOT rolled back if a parent transactional handler fails later
///
/// Nesting example:
///   Handler A [Transactional] → Handler B (non-tx) → Handler C [Transactional]
///
///   - A starts ambient transaction (depth=1)
///   - B runs independently, commits its own work + outbox messages immediately
///   - C joins A's ambient transaction (depth=2)
///   - If C fails: A + C rollback, but B's work is already committed
///
/// Register as: momentum.AddBehavior(typeof(OutboxBehavior&lt;,&gt;));
/// </summary>
public sealed class OutboxBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly IOutboxCollector _collector;
    private readonly IOutboxStore _outboxStore;
    private readonly IMessageSerializer _serializer;
    private readonly IMessageTypeRegistry _typeRegistry;
    private readonly IOutboxTransactionAccessor _transactionAccessor;
    private readonly IDbConnection _connection;
    private readonly IHandlerTransactionRegistry _txRegistry;

    // Static cache: request type → is handler non-transactional
    private static readonly ConcurrentDictionary<Type, bool> NonTransactionalCache = new();

    public OutboxBehavior(
        IOutboxCollector collector,
        IOutboxStore outboxStore,
        IMessageSerializer serializer,
        IMessageTypeRegistry typeRegistry,
        IOutboxTransactionAccessor transactionAccessor,
        IDbConnection connection,
        IHandlerTransactionRegistry txRegistry)
    {
        _collector = collector;
        _outboxStore = outboxStore;
        _serializer = serializer;
        _typeRegistry = typeRegistry;
        _transactionAccessor = transactionAccessor;
        _connection = connection;
        _txRegistry = txRegistry;
    }

    public async Task<TResponse> HandleAsync(
        TRequest request,
        NextDelegate<TResponse> next,
        CancellationToken ct = default)
    {
        // Determine if the target handler is transactional.
        // The source generator knows the handler type for each request,
        // but at this level we check the attribute on TRequest's handler.
        // The generated mediator resolves the handler — we inspect its type.
        var isTransactional = !IsNonTransactional(request);

        return isTransactional
            ? await HandleTransactionalAsync(request, next, ct)
            : await HandleIndependentAsync(request, next, ct);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Transactional path — joins/creates the ambient transaction
    // ─────────────────────────────────────────────────────────────────────

    private async Task<TResponse> HandleTransactionalAsync(
        TRequest request,
        NextDelegate<TResponse> next,
        CancellationToken ct)
    {
        var previousTarget = _collector.ActiveTarget;
        _collector.ActiveTarget = CollectionTarget.Ambient;

        using var scope = _transactionAccessor.BeginTransactional(_connection);

        TResponse response;
        try
        {
            response = await next();
        }
        catch
        {
            if (scope.IsAmbientOwner)
            {
                scope.Transaction.Transaction.Rollback();
                await scope.Transaction.DisposeAsync();
                ((OutboxTransactionAccessor)_transactionAccessor).ResetAmbient();
                _collector.ClearAmbient();
            }
            _collector.ActiveTarget = previousTarget;
            throw;
        }

        if (scope.IsAmbientOwner)
        {
            try
            {
                await FlushAsync(_collector.GetAmbientPending(), scope.Transaction, ct);
                scope.Transaction.Transaction.Commit();
            }
            catch
            {
                scope.Transaction.Transaction.Rollback();
                throw;
            }
            finally
            {
                await scope.Transaction.DisposeAsync();
                ((OutboxTransactionAccessor)_transactionAccessor).ResetAmbient();
                _collector.ClearAmbient();
                _collector.ActiveTarget = previousTarget;
            }
        }
        else
        {
            _collector.ActiveTarget = previousTarget;
        }

        return response;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Independent path — own transaction, commits immediately
    // ─────────────────────────────────────────────────────────────────────

    private async Task<TResponse> HandleIndependentAsync(
        TRequest request,
        NextDelegate<TResponse> next,
        CancellationToken ct)
    {
        var previousTarget = _collector.ActiveTarget;
        _collector.ActiveTarget = CollectionTarget.Independent;

        using var scope = _transactionAccessor.BeginIndependent(_connection);

        TResponse response;
        try
        {
            response = await next();
        }
        catch
        {
            scope.Transaction.Transaction.Rollback();
            await scope.Transaction.DisposeAsync();
            _collector.ClearIndependent();
            _collector.ActiveTarget = previousTarget;
            throw;
        }

        try
        {
            await FlushAsync(_collector.GetIndependentPending(), scope.Transaction, ct);
            scope.Transaction.Transaction.Commit();
        }
        catch
        {
            scope.Transaction.Transaction.Rollback();
            throw;
        }
        finally
        {
            await scope.Transaction.DisposeAsync();
            _collector.ClearIndependent();
            _collector.ActiveTarget = previousTarget;
        }

        return response;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Shared
    // ─────────────────────────────────────────────────────────────────────

    private async Task FlushAsync(
        IReadOnlyList<CollectedMessage> pending,
        OutboxTransaction transaction,
        CancellationToken ct)
    {
        if (pending.Count == 0)
            return;

        var outboxMessages = new List<OutboxMessage>(pending.Count);

        foreach (var collected in pending)
        {
            var payload = _serializer.Serialize(collected.Message, collected.MessageType);

            outboxMessages.Add(new OutboxMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                MessageType = _typeRegistry.GetName(collected.MessageType),
                Destination = collected.Destination,
                PartitionKey = collected.Options.PartitionKey,
                Payload = payload.ToArray(),
                Headers = collected.Options.Headers.Count > 0
                    ? JsonSerializer.Serialize(collected.Options.Headers)
                    : null,
                CorrelationId = collected.Options.CorrelationId,
                CausationId = collected.Options.CausationId,
            });
        }

        await _outboxStore.StoreAsync(outboxMessages, transaction, ct);
    }

    /// <summary>
    /// Check if the handler should run independently.
    /// Respects global mode + per-handler [Transactional]/[NonTransactional] overrides.
    /// </summary>
    private bool IsNonTransactional(TRequest request)
    {
        return NonTransactionalCache.GetOrAdd(typeof(TRequest), _ => _txRegistry.IsNonTransactional<TRequest>());
    }
}
