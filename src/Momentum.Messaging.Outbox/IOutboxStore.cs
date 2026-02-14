namespace Momentum.Messaging.Outbox;

/// <summary>
/// Persistence contract for the outbox. Dapper-based implementations
/// are provided as separate packages (Momentum.Messaging.SqlServer,
/// Momentum.Messaging.PostgreSql, etc.).
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Write messages to the outbox within the given transaction.
    /// The transaction is the SAME one your handler uses for business data,
    /// guaranteeing atomicity.
    /// </summary>
    Task StoreAsync(
        IReadOnlyList<OutboxMessage> messages,
        OutboxTransaction transaction,
        CancellationToken ct = default);

    /// <summary>
    /// Fetch the next batch of unsent messages for dispatching.
    /// Marks them as locked to prevent concurrent processors from picking them up.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> FetchPendingAsync(
        int batchSize,
        TimeSpan lockDuration,
        CancellationToken ct = default);

    /// <summary>
    /// Mark messages as successfully dispatched.
    /// </summary>
    Task MarkDispatchedAsync(
        IReadOnlyList<Guid> messageIds,
        CancellationToken ct = default);

    /// <summary>
    /// Mark a message as failed. Increments retry count.
    /// </summary>
    Task MarkFailedAsync(
        Guid messageId,
        string error,
        CancellationToken ct = default);

    /// <summary>
    /// Clean up old dispatched messages beyond the retention period.
    /// </summary>
    Task PurgeAsync(TimeSpan retentionPeriod, CancellationToken ct = default);
}

/// <summary>
/// An outbox message row. Maps to the outbox table.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string MessageId { get; init; }
    public required string MessageType { get; init; }
    public required string Destination { get; init; }
    public string? PartitionKey { get; init; }
    public required byte[] Payload { get; init; }
    public string? Headers { get; init; }
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.Pending;
}

public enum OutboxMessageStatus
{
    Pending = 0,
    Dispatched = 1,
    Failed = 2,
}

/// <summary>
/// Wraps the database transaction so the outbox store can participate
/// in the same transaction as your business data writes.
/// </summary>
public sealed class OutboxTransaction : IAsyncDisposable
{
    /// <summary>The underlying DbConnection.</summary>
    public required System.Data.IDbConnection Connection { get; init; }

    /// <summary>The underlying DbTransaction.</summary>
    public required System.Data.IDbTransaction Transaction { get; init; }

    public async ValueTask DisposeAsync()
    {
        Transaction.Dispose();
        if (Connection is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else
            Connection.Dispose();
    }
}
