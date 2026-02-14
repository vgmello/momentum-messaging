namespace Momentum.Messaging.Abstractions;

/// <summary>
/// Subscribes to a transport and receives messages. Each transport plugin
/// provides an implementation. The consumer runs as a hosted service
/// and dispatches received messages to the mediator (through the inbox
/// if enabled).
/// </summary>
public interface IMessageConsumer
{
    /// <summary>
    /// Start consuming messages from the configured source.
    /// Called by the hosted service on application startup.
    /// </summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stop consuming. Allows in-flight messages to complete.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// Processes a received message envelope. The default implementation
/// deserializes the envelope and dispatches to the mediator.
/// When inbox is enabled, it wraps this with deduplication.
/// </summary>
public interface IMessageProcessor
{
    Task ProcessAsync(MessageEnvelope envelope, IMessageContext context, CancellationToken ct = default);
}

/// <summary>
/// Context for a message being processed. Provides acknowledge/reject
/// semantics that the transport adapter translates to its native protocol
/// (Kafka offset commit, RabbitMQ ack/nack, etc.).
/// </summary>
public interface IMessageContext
{
    /// <summary>The raw envelope.</summary>
    MessageEnvelope Envelope { get; }

    /// <summary>
    /// Acknowledge successful processing. Transport-specific:
    /// Kafka commits offset, RabbitMQ sends ack, etc.
    /// </summary>
    Task AcknowledgeAsync(CancellationToken ct = default);

    /// <summary>
    /// Reject the message. Transport-specific:
    /// RabbitMQ nack+requeue, Kafka does not commit, etc.
    /// </summary>
    Task RejectAsync(bool requeue = true, CancellationToken ct = default);

    /// <summary>
    /// Number of times this message has been delivered (if the transport tracks it).
    /// </summary>
    int DeliveryAttempt { get; }
}
