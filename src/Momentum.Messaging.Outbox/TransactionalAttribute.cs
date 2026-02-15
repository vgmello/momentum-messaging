namespace Momentum.Messaging.Outbox;

/// <summary>
/// Marks a handler as transactional. Used when the global default is NonTransactional.
/// The handler joins/creates the ambient transaction.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TransactionalAttribute : Attribute;

/// <summary>
/// Marks a handler as non-transactional. Used when the global default is Transactional.
/// The handler gets its own independent transaction that commits immediately.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NonTransactionalAttribute : Attribute;
