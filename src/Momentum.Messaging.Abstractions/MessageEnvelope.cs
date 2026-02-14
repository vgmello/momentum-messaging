namespace Momentum.Messaging.Abstractions;

/// <summary>
/// The envelope wraps any message for transport. It carries the payload,
/// metadata, headers, and routing information. This is what flows through
/// serializers, transports, outbox, and inbox.
/// </summary>
public sealed class MessageEnvelope
{
    /// <summary>Unique message identifier for deduplication (inbox).</summary>
    public required string MessageId { get; init; }

    /// <summary>
    /// Correlation ID for tracing a chain of messages back to the originating request.
    /// Propagated automatically through the pipeline.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Causation ID — the MessageId of the message that caused this one.
    /// </summary>
    public string? CausationId { get; init; }

    /// <summary>
    /// Assembly-qualified type name of the payload. Used for deserialization routing.
    /// </summary>
    public required string MessageType { get; init; }

    /// <summary>
    /// The destination topic/queue/exchange/event hub name.
    /// </summary>
    public required string Destination { get; init; }

    /// <summary>
    /// Optional partition/routing key for ordered delivery within a partition.
    /// </summary>
    public string? PartitionKey { get; init; }

    /// <summary>
    /// Serialized payload bytes. The serializer is pluggable.
    /// </summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>
    /// Extensible headers. Transports may add their own (e.g., Kafka headers).
    /// </summary>
    public MessageHeaders Headers { get; init; } = MessageHeaders.Empty;

    /// <summary>
    /// When the message was created (UTC).
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional delay before the message becomes visible to consumers.
    /// Not all transports support this.
    /// </summary>
    public TimeSpan? ScheduledDelay { get; init; }
}

/// <summary>
/// Typed wrapper for reading — the deserialized payload is available as <see cref="Message"/>.
/// </summary>
public sealed class MessageEnvelope<TMessage> where TMessage : class
{
    public required MessageEnvelope Envelope { get; init; }
    public required TMessage Message { get; init; }
}

/// <summary>
/// Immutable header bag. Keys are case-insensitive.
/// </summary>
public sealed class MessageHeaders
{
    public static readonly MessageHeaders Empty = new(new Dictionary<string, string>());

    private readonly Dictionary<string, string> _headers;

    public MessageHeaders(IDictionary<string, string> headers)
        => _headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => _headers.GetValueOrDefault(key);

    public MessageHeaders With(string key, string value)
    {
        var copy = new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase)
        {
            [key] = value
        };
        return new MessageHeaders(copy);
    }

    public IReadOnlyDictionary<string, string> All => _headers;
}
