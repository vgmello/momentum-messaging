using Momentum.Messaging;
using Microsoft.Extensions.DependencyInjection;

// ══════════════════════════════════════════════════════════════════════════════
// Assembly-level configuration — this is the ONLY place conventions are defined
// ══════════════════════════════════════════════════════════════════════════════

[assembly: MomentumMediator]

// Multiple suffixes — all of these class patterns are discovered
[assembly: MomentumHandlerSuffix("Handler")]
[assembly: MomentumHandlerSuffix("Processor")]
[assembly: MomentumHandlerSuffix("Consumer")]

// Multiple method names — all of these methods are discovered
[assembly: MomentumMethodName("HandleAsync")]
[assembly: MomentumMethodName("ExecuteAsync")]

// OR: replace discovery entirely with a custom strategy
// [assembly: MomentumDiscoveryStrategy(typeof(MyCompany.CustomDiscovery))]


// ── Messages ─────────────────────────────────────────────────────────────────

public sealed record CreateOrder(string ProductId, int Quantity) : IRequest<OrderResult>;
public sealed record OrderResult(Guid OrderId, DateTime CreatedAt);

public sealed record CancelOrder(Guid OrderId) : IRequest;

public sealed record OrderCreated(Guid OrderId, string ProductId) : INotification;


// ── "Handler" suffix ─────────────────────────────────────────────────────────

public sealed class CreateOrderHandler                               // ✅ matches "Handler" suffix
{
    public async Task<OrderResult> HandleAsync(CreateOrder req, CancellationToken ct)   // ✅ matches "HandleAsync"
    {
        return new OrderResult(Guid.NewGuid(), DateTime.UtcNow);
    }
}


// ── "Processor" suffix ───────────────────────────────────────────────────────

public sealed class CancelOrderProcessor                             // ✅ matches "Processor" suffix
{
    public Task ExecuteAsync(CancelOrder req, CancellationToken ct)  // ✅ matches "ExecuteAsync"
    {
        Console.WriteLine($"Processing cancellation for {req.OrderId}");
        return Task.CompletedTask;
    }
}


// ── "Consumer" suffix ────────────────────────────────────────────────────────

public sealed class OrderCreatedEmailConsumer                        // ✅ matches "Consumer" suffix
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct)
    {
        Console.WriteLine($"📧 Email for order {notification.OrderId}");
        return Task.CompletedTask;
    }
}

public sealed class OrderCreatedInventoryConsumer                    // ✅ matches "Consumer" suffix
{
    public Task ExecuteAsync(OrderCreated notification, CancellationToken ct)  // ✅ "ExecuteAsync" also works
    {
        Console.WriteLine($"📦 Inventory for {notification.ProductId}");
        return Task.CompletedTask;
    }
}


// ── Explicit opt-in (no suffix match needed) ─────────────────────────────────

[MomentumHandler]
public sealed class SpecialOrderLogic                                // ✅ [MomentumHandler] overrides suffix
{
    public Task HandleAsync(OrderCreated notification, CancellationToken ct)
    {
        Console.WriteLine($"📊 Analytics for {notification.OrderId}");
        return Task.CompletedTask;
    }
}


// ── Explicit opt-out ─────────────────────────────────────────────────────────

[IgnoreHandler]
public sealed class DeprecatedOrderHandler                           // ❌ excluded despite "Handler" suffix
{
    public Task HandleAsync(CreateOrder req, CancellationToken ct) => Task.CompletedTask;
}


// ── Behavior ─────────────────────────────────────────────────────────────────

public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request, NextDelegate<TResponse> next, CancellationToken ct)
    {
        Console.WriteLine($"→ {typeof(TRequest).Name}");
        var response = await next();
        Console.WriteLine($"← {typeof(TRequest).Name}");
        return response;
    }
}


// ── DI Setup ─────────────────────────────────────────────────────────────────

public static class Program
{
    public static async Task Main()
    {
        var services = new ServiceCollection();

        services.AddMomentum(momentum =>
        {
            momentum.AddBehavior(typeof(LoggingBehavior<,>));
            momentum.UseParallelNotifications();
        });

        await using var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.SendAsync(new CreateOrder("SKU-42", 3));
        Console.WriteLine($"✅ Order: {result.OrderId}");

        await mediator.SendAsync(new CancelOrder(result.OrderId));

        await mediator.PublishAsync(new OrderCreated(result.OrderId, "SKU-42"));
    }
}
