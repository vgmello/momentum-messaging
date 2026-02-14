namespace Momentum.Messaging;

/// <summary>
/// Triggers source generation for this assembly.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class MomentumMediatorAttribute : Attribute;

/// <summary>
/// Explicitly marks a class as a handler, overriding convention-based discovery.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class MomentumHandlerAttribute : Attribute;

/// <summary>
/// Excludes a class from handler discovery even if it matches conventions.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class IgnoreHandlerAttribute : Attribute;

/// <summary>
/// Adds a handler class suffix to the discovery conventions.
/// Multiple attributes can be applied. Default is "Handler" if none specified.
/// </summary>
/// <example>
/// <code>
/// [assembly: MomentumMediator]
/// [assembly: MomentumHandlerSuffix("Handler")]
/// [assembly: MomentumHandlerSuffix("Processor")]
/// [assembly: MomentumHandlerSuffix("Consumer")]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class MomentumHandlerSuffixAttribute : Attribute
{
    public string Suffix { get; }
    public MomentumHandlerSuffixAttribute(string suffix) => Suffix = suffix;
}

/// <summary>
/// Adds a handler method name to the discovery conventions.
/// Multiple attributes can be applied. Default is "HandleAsync" if none specified.
/// </summary>
/// <example>
/// <code>
/// [assembly: MomentumMediator]
/// [assembly: MomentumMethodName("HandleAsync")]
/// [assembly: MomentumMethodName("ExecuteAsync")]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class MomentumMethodNameAttribute : Attribute
{
    public string Name { get; }
    public MomentumMethodNameAttribute(string name) => Name = name;
}

/// <summary>
/// Replaces the default suffix-based discovery with a fully custom strategy.
/// The specified type must implement IHandlerDiscoveryStrategy and have a
/// parameterless constructor. The generator will instantiate it at compile time.
/// </summary>
/// <example>
/// <code>
/// [assembly: MomentumMediator]
/// [assembly: MomentumDiscoveryStrategy(typeof(MyCustomDiscoveryStrategy))]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class MomentumDiscoveryStrategyAttribute : Attribute
{
    public Type StrategyType { get; }
    public MomentumDiscoveryStrategyAttribute(Type strategyType) => StrategyType = strategyType;
}
