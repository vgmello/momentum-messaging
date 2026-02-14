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
    private Type _publishStrategyType = typeof(SequentialStrategy);

    internal MomentumBuilder(IServiceCollection services) => _services = services;

    public IServiceCollection Services => _services;

    // -- Behaviors --

    public MomentumBuilder AddBehavior(Type openGenericBehaviorType)
    {
        if (!openGenericBehaviorType.IsGenericTypeDefinition)
            throw new ArgumentException(
                $"{openGenericBehaviorType.Name} must be an open generic (e.g., typeof(MyBehavior<,>)).");
        _behaviorTypes.Add(openGenericBehaviorType);
        return this;
    }

    // -- Notification strategy --

    public MomentumBuilder UseParallelNotifications()
    {
        _publishStrategyType = typeof(ParallelStrategy);
        return this;
    }

    public MomentumBuilder UseNotificationStrategy<T>() where T : class, INotificationPublishStrategy
    {
        _publishStrategyType = typeof(T);
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

        foreach (var behaviorType in _behaviorTypes)
        {
            _services.Add(new ServiceDescriptor(
                typeof(IPipelineBehavior<,>), behaviorType, _handlerLifetime));
        }

        if (MomentumGeneratedHook.RegistrationAction is null)
            throw new InvalidOperationException(
                "Momentum source generator has not run. " +
                "Ensure Momentum.Messaging.Generators is referenced and [assembly: MomentumMediator] is present.");

        MomentumGeneratedHook.RegistrationAction(_services, _handlerLifetime);
    }
}
