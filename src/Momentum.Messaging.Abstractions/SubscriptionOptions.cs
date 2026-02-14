namespace Momentum.Messaging.Abstractions;

/// <summary>
/// Describes a subscription to a message source. Transport plugins
/// translate this into their native subscription model.
/// </summary>
public sealed class SubscriptionOptions
{
    /// <summary>The topic/queue/event hub to consume from.</summary>
    public required string Source { get; init; }

    /// <summary>
    /// Consumer group ID. Multiple instances with the same group share the load.
    /// Different groups each get all messages (fan-out).
    /// </summary>
    public required string GroupId { get; init; }

    /// <summary>
    /// Max messages to process concurrently per consumer instance.
    /// </summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>
    /// Auto-acknowledge after successful processing (default: true).
    /// Set to false for manual ack via IMessageContext.
    /// </summary>
    public bool AutoAcknowledge { get; init; } = true;

    /// <summary>
    /// Transport-specific configuration. Kafka consumer config, RabbitMQ
    /// queue arguments, etc. Avoids polluting the shared model.
    /// </summary>
    public Dictionary<string, object> Properties { get; init; } = [];
}
