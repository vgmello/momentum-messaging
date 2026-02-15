using System.Diagnostics.CodeAnalysis;
using Momentum.Messaging.Abstractions;

namespace Momentum.Messaging.Outbox;

/// <summary>
/// Collects messages during handler execution for outbox publishing.
/// Supports isolated collections: transactional handlers share one collection,
/// independent handlers get their own that flushes immediately.
/// </summary>
public interface IOutboxCollector
{
    /// <summary>
    /// Stage a message for outbox publishing.
    /// </summary>
    void Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TMessage>(
        TMessage message,
        string destination,
        Action<PublishOptions>? configure = null)
        where TMessage : class;

    /// <summary>Get all pending messages in the ambient (transactional) collection.</summary>
    IReadOnlyList<CollectedMessage> GetAmbientPending();

    /// <summary>Get all pending messages in the independent collection.</summary>
    IReadOnlyList<CollectedMessage> GetIndependentPending();

    /// <summary>Clear the ambient (transactional) collection.</summary>
    void ClearAmbient();

    /// <summary>Clear the independent collection.</summary>
    void ClearIndependent();

    /// <summary>
    /// Set the active target. Messages added via Add() go to the active target.
    /// </summary>
    CollectionTarget ActiveTarget { get; set; }
}

public enum CollectionTarget
{
    Ambient,
    Independent,
}

public sealed class CollectedMessage
{
    public required object Message { get; init; }
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    public required Type MessageType { get; init; }
    public required string Destination { get; init; }
    public PublishOptions Options { get; init; } = new();
}

internal sealed class OutboxCollector : IOutboxCollector
{
    private readonly List<CollectedMessage> _ambient = [];
    private readonly List<CollectedMessage> _independent = [];

    public CollectionTarget ActiveTarget { get; set; } = CollectionTarget.Ambient;

    public void Add<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TMessage>(
        TMessage message,
        string destination,
        Action<PublishOptions>? configure = null)
        where TMessage : class
    {
        var options = new PublishOptions();
        configure?.Invoke(options);

        var collected = new CollectedMessage
        {
            Message = message,
            MessageType = typeof(TMessage),
            Destination = destination,
            Options = options,
        };

        var target = ActiveTarget == CollectionTarget.Independent ? _independent : _ambient;
        target.Add(collected);
    }

    public IReadOnlyList<CollectedMessage> GetAmbientPending() => _ambient;
    public IReadOnlyList<CollectedMessage> GetIndependentPending() => _independent;
    public void ClearAmbient() => _ambient.Clear();
    public void ClearIndependent() => _independent.Clear();
}
