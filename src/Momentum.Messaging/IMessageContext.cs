using System.ComponentModel;
using Momentum.Messaging.Abstractions;

namespace Momentum.Messaging;

/// <summary>
/// Ambient message context available during dispatch. Extends <see cref="IMessageBus"/>
/// so handlers can send/publish with automatic correlation/causation propagation.
/// </summary>
public interface IMessageContext : IMessageBus
{
    /// <summary>Unique identifier for this message.</summary>
    string MessageId { get; }

    /// <summary>Correlation ID tracing the originating request chain.</summary>
    string? CorrelationId { get; }

    /// <summary>MessageId of the message that caused this one.</summary>
    string? CausationId { get; }

    /// <summary>Source topic/queue (null for in-memory origin).</summary>
    string? Source { get; }

    /// <summary>Partition/routing key (null if not partitioned).</summary>
    string? PartitionKey { get; }

    /// <summary>Message headers.</summary>
    MessageHeaders Headers { get; }

    /// <summary>When the message was created.</summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>Raw inbound envelope when consumed from a transport; null for in-memory.</summary>
    MessageEnvelope? Envelope { get; }
}

/// <summary>
/// Scoped implementation of <see cref="IMessageContext"/>.
/// Created per dispatch by the generated message bus.
/// </summary>
/// <remarks>Infrastructure type — use <see cref="IMessageContext"/> in handler code.</remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class MessageContextScope : IMessageContext
{
    private static readonly AsyncLocal<MessageContextScope?> CurrentScope = new();

    /// <summary>Get the current ambient context, or null if none.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static MessageContextScope? Current => CurrentScope.Value;

    private readonly IMessageBus _bus;
    private readonly MessageContextScope? _parent;
    private string? _messageId;
    private string? _correlationId;
    private bool _correlationIdSet;
    private string? _causationId;
    private bool _causationIdSet;
    private DateTimeOffset? _timestamp;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public MessageContextScope(IMessageBus bus, MessageContextScope? parent = null)
    {
        _bus = bus;
        _parent = parent;
    }

    public string MessageId
    {
        get => _messageId ??= Guid.NewGuid().ToString("N");
        set => _messageId = value;
    }

    public string? CorrelationId
    {
        get
        {
            if (!_correlationIdSet)
            {
                _correlationId = _parent?.CorrelationId ?? Guid.NewGuid().ToString("N");
                _correlationIdSet = true;
            }
            return _correlationId;
        }
        set
        {
            _correlationId = value;
            _correlationIdSet = true;
        }
    }

    public string? CausationId
    {
        get
        {
            if (!_causationIdSet)
            {
                _causationId = _parent?.MessageId;
                _causationIdSet = true;
            }
            return _causationId;
        }
        set
        {
            _causationId = value;
            _causationIdSet = true;
        }
    }

    public string? Source { get; set; }
    public string? PartitionKey { get; set; }
    public MessageHeaders Headers { get; set; } = MessageHeaders.Empty;

    public DateTimeOffset Timestamp
    {
        get
        {
            _timestamp ??= DateTimeOffset.UtcNow;
            return _timestamp.Value;
        }
        set => _timestamp = value;
    }

    public MessageEnvelope? Envelope { get; set; }

    public Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        => _bus.SendAsync(request, ct);

    public Task PublishAsync<TNotification>(TNotification notification, CancellationToken ct = default)
        where TNotification : INotification
        => _bus.PublishAsync(notification, ct);

    /// <summary>
    /// Set this scope as the ambient context, returning the previous value
    /// so it can be restored after dispatch.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static MessageContextScope? SetCurrent(MessageContextScope scope)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = scope;
        return previous;
    }

    /// <summary>Restore the previous ambient context.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RestoreCurrent(MessageContextScope? previous)
    {
        CurrentScope.Value = previous;
    }
}
