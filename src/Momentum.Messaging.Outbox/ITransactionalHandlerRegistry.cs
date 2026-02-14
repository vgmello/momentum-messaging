namespace Momentum.Messaging.Outbox;

/// <summary>
/// The global default transaction mode for handlers.
/// </summary>
public enum TransactionMode
{
    /// <summary>All handlers are transactional unless marked [NonTransactional].</summary>
    TransactionalByDefault,

    /// <summary>All handlers are non-transactional unless marked [Transactional].</summary>
    NonTransactionalByDefault,
}

/// <summary>
/// Determines whether a handler runs transactionally or independently.
/// The source generator emits a concrete implementation that knows which
/// handlers have [Transactional] or [NonTransactional] attributes.
/// The global mode determines the default for unmarked handlers.
/// </summary>
public interface IHandlerTransactionRegistry
{
    /// <summary>Returns true if the handler for this request type should run independently.</summary>
    bool IsNonTransactional<TRequest>();
    bool IsNonTransactional(Type requestType);
}

/// <summary>
/// Default implementation. Uses the configured global mode.
/// The source generator replaces this with one that knows about attributed handlers.
/// </summary>
internal sealed class DefaultHandlerTransactionRegistry : IHandlerTransactionRegistry
{
    private readonly TransactionMode _mode;

    public DefaultHandlerTransactionRegistry(TransactionMode mode) => _mode = mode;

    // No attributes known — everything follows the global default
    public bool IsNonTransactional<TRequest>() =>
        _mode == TransactionMode.NonTransactionalByDefault;

    public bool IsNonTransactional(Type requestType) =>
        _mode == TransactionMode.NonTransactionalByDefault;
}
