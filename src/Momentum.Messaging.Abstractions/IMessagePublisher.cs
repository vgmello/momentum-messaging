namespace Momentum.Messaging.Abstractions;

/// <summary>
/// Publishes messages to a transport (Kafka, RabbitMQ, EventHubs, etc.).
/// Each transport plugin provides an implementation.
///
/// In normal flow, user code calls this directly or through the mediator.
/// When the outbox is enabled, the outbox pipeline behavior captures
/// publish calls and writes to the outbox table instead. The outbox
/// background processor then calls the real transport publisher.
/// </summary>
public interface IMessagePublisher
{
    /// <summary>
    /// Publish a message to the specified destination.
    /// </summary>
    Task PublishAsync<TMessage>(
        TMessage message,
        string destination,
        Action<PublishOptions>? configure = null,
        CancellationToken ct = default)
        where TMessage : class;

    /// <summary>
    /// Publish a batch of messages to the specified destination.
    /// </summary>
    Task PublishBatchAsync<TMessage>(
        IReadOnlyList<TMessage> messages,
        string destination,
        Action<PublishOptions>? configure = null,
        CancellationToken ct = default)
        where TMessage : class;

    /// <summary>
    /// Publish a pre-built envelope directly. Used by the outbox processor
    /// to replay messages that were already serialized.
    /// </summary>
    Task PublishEnvelopeAsync(MessageEnvelope envelope, CancellationToken ct = default);
}

/// <summary>
/// Options for a single publish operation.
/// </summary>
public sealed class PublishOptions
{
    /// <summary>Partition/routing key for ordered delivery.</summary>
    public string? PartitionKey { get; set; }

    /// <summary>Additional headers to include.</summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    /// <summary>Correlation ID to propagate.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Causation ID (message that caused this one).</summary>
    public string? CausationId { get; set; }

    /// <summary>Delay before the message becomes visible.</summary>
    public TimeSpan? ScheduledDelay { get; set; }
}
