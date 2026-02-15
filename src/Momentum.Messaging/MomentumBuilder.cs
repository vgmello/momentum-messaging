using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Momentum.Messaging;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMomentum(
        this IServiceCollection services,
        Action<MomentumBuilder>? configure = null)
    {
        var builder = new MomentumBuilder(services);
        configure?.Invoke(builder);
        builder.Build();
        return services;
    }
}

public sealed class MomentumBuilder
{
    private readonly IServiceCollection _services;
    private readonly List<Type> _behaviorTypes = [];
    private ServiceLifetime _handlerLifetime = ServiceLifetime.Transient;
    private bool _scopedDispatch;
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    private Type _publishStrategyType = typeof(SequentialStrategy);

    internal MomentumBuilder(IServiceCollection services) => _services = services;

    public IServiceCollection Services => _services;

    // -- Behaviors --

    /// <summary>
    /// Register an open-generic pipeline behavior.
    /// The source generator emits closed generic registrations for AOT compatibility.
    /// At runtime, open generic registration is used as a fallback for DI containers
    /// that support it natively.
    /// </summary>
    public MomentumBuilder AddBehavior(Type openGenericBehaviorType)
    {
        if (!openGenericBehaviorType.IsGenericTypeDefinition)
            throw new ArgumentException(
                $"{openGenericBehaviorType.Name} must be an open generic (e.g., typeof(MyBehavior<,>)).");
        _behaviorTypes.Add(openGenericBehaviorType);
        return this;
    }

    internal IReadOnlyList<Type> BehaviorTypes => _behaviorTypes;

    // -- Notification strategy --

    public MomentumBuilder UseParallelNotifications()
    {
        _publishStrategyType = typeof(ParallelStrategy);
        return this;
    }

    public MomentumBuilder UseNotificationStrategy<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>() where T : class, INotificationPublishStrategy
    {
        _publishStrategyType = typeof(T);
        return this;
    }

    // -- Scoped dispatch --

    /// <summary>
    /// Enable DI scope creation per dispatch. Required when using scoped services
    /// (e.g., outbox/transactional patterns). Off by default for maximum throughput.
    /// </summary>
    public MomentumBuilder UseScopedDispatch()
    {
        _scopedDispatch = true;
        return this;
    }

    // -- Lifetime --

    public MomentumBuilder WithHandlerLifetime(ServiceLifetime lifetime)
    {
        _handlerLifetime = lifetime;
        return this;
    }

    // -- Build --

    internal void Build()
    {
        _services.TryAddSingleton(typeof(INotificationPublishStrategy), _publishStrategyType);

        if (MomentumGeneratedHook.RegistrationAction is null)
            throw new InvalidOperationException(
                "Momentum source generator has not run. " +
                "Ensure Momentum.Messaging.Generators is referenced and [assembly: MomentumMediator] is present.");

        // The generated code registers handlers, message bus, AND closed generic
        // behavior registrations (AOT-safe). We pass the open generic types
        // so the generated code can emit closed versions for each request type.
        var options = new MomentumOptions { ScopedDispatch = _scopedDispatch };
        MomentumGeneratedHook.RegistrationAction(_services, _handlerLifetime, _behaviorTypes, options);
    }
}

/// <summary>
/// Options passed from <see cref="MomentumBuilder"/> to the generated registration code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class MomentumOptions
{
    /// <summary>Whether to create a DI scope per dispatch.</summary>
    public bool ScopedDispatch { get; set; }
}
