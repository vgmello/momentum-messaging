using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Momentum.Messaging.Outbox;

public static class OutboxServiceCollectionExtensions
{
    public static IServiceCollection AddMomentumOutbox(
        this IServiceCollection services,
        Action<OutboxOptions>? configure = null)
    {
        var options = new OutboxOptions();
        configure?.Invoke(options);

        // Core services (scoped — one per request)
        services.TryAddScoped<IOutboxCollector, OutboxCollector>();
        services.TryAddScoped<OutboxTransactionAccessor>();
        services.TryAddScoped<IOutboxTransactionAccessor>(sp => sp.GetRequiredService<OutboxTransactionAccessor>());

        // Transaction mode + registry
        services.TryAddSingleton<IHandlerTransactionRegistry>(
            new DefaultHandlerTransactionRegistry(options.TransactionMode));

        // Processor options
        services.AddSingleton(options.ProcessorOptions);

        // Background processor
        if (options.EnableProcessor)
            services.AddHostedService<OutboxProcessor>();

        return services;
    }

    public static IServiceCollection AddMomentumInbox(this IServiceCollection services)
    {
        services.TryAddSingleton<InboxMessageProcessor>();
        return services;
    }
}

public sealed class OutboxOptions
{
    /// <summary>
    /// Global transaction mode. Default: TransactionalByDefault.
    ///
    /// TransactionalByDefault: all handlers join the ambient transaction
    ///   unless marked [NonTransactional].
    ///
    /// NonTransactionalByDefault: all handlers run independently
    ///   unless marked [Transactional].
    /// </summary>
    public TransactionMode TransactionMode { get; set; } = TransactionMode.TransactionalByDefault;

    public bool EnableProcessor { get; set; } = true;
    public OutboxProcessorOptions ProcessorOptions { get; set; } = new();
}
