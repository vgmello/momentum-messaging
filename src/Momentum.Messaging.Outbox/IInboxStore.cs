namespace Momentum.Messaging.Outbox;

/// <summary>
/// Persistence contract for the inbox. Provides idempotent message
/// processing by tracking which message IDs have been handled.
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// Try to claim a message for processing. Returns true if this is the
    /// first time the message ID has been seen (i.e., not a duplicate).
    /// Uses the same transaction as your business data for atomicity.
    /// </summary>
    Task<bool> TryClaimAsync(
        string messageId,
        System.Data.IDbTransaction? transaction = null,
        CancellationToken ct = default);

    /// <summary>
    /// Mark a claimed message as successfully processed.
    /// </summary>
    Task MarkProcessedAsync(
        string messageId,
        CancellationToken ct = default);

    /// <summary>
    /// Mark a claimed message as failed.
    /// </summary>
    Task MarkFailedAsync(
        string messageId,
        string error,
        CancellationToken ct = default);

    /// <summary>
    /// Clean up old processed inbox records beyond the retention period.
    /// </summary>
    Task PurgeAsync(TimeSpan retentionPeriod, CancellationToken ct = default);
}

/// <summary>
/// An inbox record. Maps to the inbox table.
/// </summary>
public sealed class InboxRecord
{
    public required string MessageId { get; init; }
    public required string MessageType { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public InboxStatus Status { get; set; } = InboxStatus.Claimed;
    public string? Error { get; set; }
}

public enum InboxStatus
{
    Claimed = 0,
    Processed = 1,
    Failed = 2,
}
