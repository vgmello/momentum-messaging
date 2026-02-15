using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Momentum.Messaging.Abstractions;

namespace Momentum.Messaging.Outbox;

/// <summary>
/// Background service that polls the outbox store for pending messages
/// and dispatches them to the actual transport publisher.
/// </summary>
public sealed class OutboxProcessor : BackgroundService
{
    private readonly IOutboxStore _store;
    private readonly IMessagePublisher _publisher;
    private readonly IMessageTypeRegistry _typeRegistry;
    private readonly ILogger<OutboxProcessor> _logger;
    private readonly OutboxProcessorOptions _options;

    public OutboxProcessor(
        IOutboxStore store,
        IMessagePublisher publisher,
        IMessageTypeRegistry typeRegistry,
        ILogger<OutboxProcessor> logger,
        OutboxProcessorOptions options)
    {
        _store = store;
        _publisher = publisher;
        _typeRegistry = typeRegistry;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var messages = await _store.FetchPendingAsync(
                    _options.BatchSize,
                    _options.LockDuration,
                    ct);

                if (messages.Count == 0)
                {
                    await Task.Delay(_options.PollingInterval, ct);
                    continue;
                }

                _logger.LogDebug("Outbox: dispatching {Count} messages", messages.Count);

                var dispatched = new List<Guid>(messages.Count);

                foreach (var msg in messages)
                {
                    try
                    {
                        var envelope = ToEnvelope(msg);
                        await _publisher.PublishEnvelopeAsync(envelope, ct);
                        dispatched.Add(msg.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Outbox: failed to dispatch message {MessageId}", msg.MessageId);
                        await _store.MarkFailedAsync(msg.Id, ex.Message, ct);
                    }
                }

                if (dispatched.Count > 0)
                {
                    await _store.MarkDispatchedAsync(dispatched, ct);
                    _logger.LogInformation("Outbox: dispatched {Count} messages", dispatched.Count);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbox: processor error, retrying after delay");
                await Task.Delay(_options.ErrorDelay, ct);
            }
        }
    }

    private static MessageEnvelope ToEnvelope(OutboxMessage msg)
    {
        var headers = MessageHeaders.Empty;
        if (msg.Headers is not null)
        {
            var dict = System.Text.Json.JsonSerializer.Deserialize(msg.Headers, OutboxJsonContext.Default.DictionaryStringString);
            if (dict is not null)
                headers = new MessageHeaders(dict);
        }

        return new MessageEnvelope
        {
            MessageId = msg.MessageId,
            MessageType = msg.MessageType,
            Destination = msg.Destination,
            PartitionKey = msg.PartitionKey,
            Payload = msg.Payload,
            Headers = headers,
            CorrelationId = msg.CorrelationId,
            CausationId = msg.CausationId,
            Timestamp = msg.CreatedAt,
        };
    }
}

public sealed class OutboxProcessorOptions
{
    /// <summary>How often to poll for pending messages. Default: 1 second.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Max messages per batch. Default: 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>How long to lock messages during dispatch. Default: 30 seconds.</summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay after an unhandled error. Default: 5 seconds.</summary>
    public TimeSpan ErrorDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to keep dispatched messages before purging. Default: 7 days.</summary>
    public TimeSpan RetentionPeriod { get; set; } = TimeSpan.FromDays(7);
}
