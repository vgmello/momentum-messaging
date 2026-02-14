using System.Diagnostics.CodeAnalysis;

namespace Momentum.Messaging.Abstractions;

/// <summary>
/// Pluggable message serializer. No default is provided — you must register one.
/// Implementations: Momentum.Messaging.Json, Momentum.Messaging.MessagePack, etc.
/// </summary>
public interface IMessageSerializer
{
    /// <summary>Serialize a message to bytes.</summary>
    ReadOnlyMemory<byte> Serialize<TMessage>(TMessage message) where TMessage : class;

    /// <summary>Serialize a message to bytes using the runtime type.</summary>
    ReadOnlyMemory<byte> Serialize(object message, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type messageType);

    /// <summary>Deserialize bytes to a typed message.</summary>
    TMessage Deserialize<TMessage>(ReadOnlyMemory<byte> payload) where TMessage : class;

    /// <summary>Deserialize bytes to an object given the type.</summary>
    object Deserialize(ReadOnlyMemory<byte> payload, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type messageType);

    /// <summary>Content type for transport headers (e.g., "application/json").</summary>
    string ContentType { get; }
}
