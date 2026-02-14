using System.Data;

namespace Momentum.Messaging.Outbox;

/// <summary>
/// Manages the ambient outbox transaction for a request scope.
/// Supports mixed nesting of transactional and non-transactional handlers.
/// </summary>
public interface IOutboxTransactionAccessor
{
    /// <summary>The current active transaction, or null if none active.</summary>
    OutboxTransaction? Current { get; }

    /// <summary>Current transactional nesting depth. 0 = no transaction active.</summary>
    int Depth { get; }

    /// <summary>
    /// Begin or join a transaction. Returns a scope handle.
    /// Only called by the OutboxBehavior for [Transactional] handlers.
    /// </summary>
    OutboxTransactionScope BeginTransactional(IDbConnection connection);

    /// <summary>
    /// Create an independent (non-transactional) scope.
    /// Gets its own connection + transaction that commits/rolls back independently.
    /// </summary>
    OutboxTransactionScope BeginIndependent(IDbConnection connection);
}

/// <summary>
/// Represents a handler's participation in an outbox transaction.
/// </summary>
public readonly struct OutboxTransactionScope : IDisposable
{
    private readonly OutboxTransactionAccessor _accessor;
    private readonly OutboxTransactionScopeKind _kind;

    internal OutboxTransactionScope(OutboxTransactionAccessor accessor, OutboxTransactionScopeKind kind)
    {
        _accessor = accessor;
        _kind = kind;
    }

    /// <summary>True if this scope owns the outermost ambient transaction.</summary>
    public bool IsAmbientOwner => _kind == OutboxTransactionScopeKind.AmbientOwner;

    /// <summary>True if this is an independent (non-transactional) scope.</summary>
    public bool IsIndependent => _kind == OutboxTransactionScopeKind.Independent;

    /// <summary>True if this just joined an existing ambient transaction.</summary>
    public bool IsNested => _kind == OutboxTransactionScopeKind.Nested;

    /// <summary>The transaction for this scope.</summary>
    public OutboxTransaction Transaction => _kind switch
    {
        OutboxTransactionScopeKind.Independent => _accessor.IndependentTransaction
            ?? throw new InvalidOperationException("Independent transaction not available."),
        _ => _accessor.Current
            ?? throw new InvalidOperationException("No active outbox transaction."),
    };

    public void Dispose() => _accessor.Release(_kind);
}

internal enum OutboxTransactionScopeKind
{
    AmbientOwner,   // Created the ambient transaction
    Nested,         // Joined existing ambient transaction
    Independent,    // Runs independently, own transaction
}

internal sealed class OutboxTransactionAccessor : IOutboxTransactionAccessor
{
    private int _depth;

    public OutboxTransaction? Current { get; private set; }
    public OutboxTransaction? IndependentTransaction { get; private set; }
    public int Depth => _depth;

    public OutboxTransactionScope BeginTransactional(IDbConnection connection)
    {
        if (_depth == 0)
        {
            // Outermost transactional handler — create the ambient transaction
            if (connection.State != ConnectionState.Open)
                connection.Open();

            Current = new OutboxTransaction
            {
                Connection = connection,
                Transaction = connection.BeginTransaction(),
            };

            _depth = 1;
            return new OutboxTransactionScope(this, OutboxTransactionScopeKind.AmbientOwner);
        }

        // Nested transactional handler — join ambient
        _depth++;
        return new OutboxTransactionScope(this, OutboxTransactionScopeKind.Nested);
    }

    public OutboxTransactionScope BeginIndependent(IDbConnection connection)
    {
        // Independent handler gets its own transaction regardless of ambient state
        if (connection.State != ConnectionState.Open)
            connection.Open();

        IndependentTransaction = new OutboxTransaction
        {
            Connection = connection,
            Transaction = connection.BeginTransaction(),
        };

        return new OutboxTransactionScope(this, OutboxTransactionScopeKind.Independent);
    }

    internal void Release(OutboxTransactionScopeKind kind)
    {
        switch (kind)
        {
            case OutboxTransactionScopeKind.AmbientOwner:
                // Reset handled by behavior after commit/rollback
                break;
            case OutboxTransactionScopeKind.Nested:
                _depth--;
                break;
            case OutboxTransactionScopeKind.Independent:
                IndependentTransaction = null;
                break;
        }
    }

    internal void ResetAmbient()
    {
        Current = null;
        _depth = 0;
    }
}
