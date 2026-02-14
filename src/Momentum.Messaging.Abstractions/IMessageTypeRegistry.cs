namespace Momentum.Messaging.Abstractions;

/// <summary>
/// Maps message type names (stored in envelopes) to CLR types for deserialization.
/// The source generator can emit a concrete implementation, or you can register manually.
/// </summary>
public interface IMessageTypeRegistry
{
    /// <summary>Get the CLR type for a message type name.</summary>
    Type? Resolve(string messageTypeName);

    /// <summary>Get the message type name for a CLR type.</summary>
    string GetName(Type messageType);

    /// <summary>Get the message type name for a CLR type.</summary>
    string GetName<TMessage>() where TMessage : class;
}

/// <summary>
/// Default implementation: uses assembly-qualified type names.
/// Override for shorter/stable names (recommended for production).
/// </summary>
public class DefaultMessageTypeRegistry : IMessageTypeRegistry
{
    private readonly Dictionary<string, Type> _nameToType = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Type, string> _typeToName = [];

    /// <summary>
    /// Register a message type with a stable name.
    /// </summary>
    public DefaultMessageTypeRegistry Register<TMessage>(string? name = null) where TMessage : class
    {
        var type = typeof(TMessage);
        var typeName = name ?? type.FullName ?? type.Name;
        _nameToType[typeName] = type;
        _typeToName[type] = typeName;
        return this;
    }

    public Type? Resolve(string messageTypeName)
        => _nameToType.GetValueOrDefault(messageTypeName)
           ?? Type.GetType(messageTypeName); // Fallback to assembly-qualified

    public string GetName(Type messageType)
        => _typeToName.GetValueOrDefault(messageType)
           ?? messageType.FullName
           ?? messageType.Name;

    public string GetName<TMessage>() where TMessage : class
        => GetName(typeof(TMessage));
}
