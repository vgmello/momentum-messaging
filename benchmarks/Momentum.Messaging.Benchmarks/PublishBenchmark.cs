using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Momentum.Messaging;

namespace Momentum.Messaging.Benchmarks;

/// <summary>
/// Compares PublishAsync (notifications) throughput between Momentum and MediatR.
/// Each notification fans out to 3 handlers.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PublishBenchmark
{
    [Params(1_000, 10_000, 100_000)]
    public int BatchSize { get; set; }

    private IMessageBus _momentumBus = null!;
    private MediatR.IMediator _mediatrMediator = null!;
    private ServiceProvider _momentumProvider = null!;
    private ServiceProvider _mediatrProvider = null!;

    [GlobalSetup]
    public void Setup()
    {
        // ── Momentum ──
        var momentumServices = new ServiceCollection();
        momentumServices.AddMomentum();
        _momentumProvider = momentumServices.BuildServiceProvider();
        _momentumBus = _momentumProvider.GetRequiredService<IMessageBus>();

        // ── MediatR ──
        var mediatrServices = new ServiceCollection();
        mediatrServices.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<PublishBenchmark>());
        _mediatrProvider = mediatrServices.BuildServiceProvider();
        _mediatrMediator = _mediatrProvider.GetRequiredService<MediatR.IMediator>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _momentumProvider.Dispose();
        _mediatrProvider.Dispose();
    }

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Publish")]
    public async Task Momentum_Publish()
    {
        for (var i = 0; i < BatchSize; i++)
            await _momentumBus.PublishAsync(new MomentumOrderPlaced(i)).ConfigureAwait(false);
    }

    [Benchmark]
    [BenchmarkCategory("Publish")]
    public async Task MediatR_Publish()
    {
        for (var i = 0; i < BatchSize; i++)
            await _mediatrMediator.Publish(new MediatROrderPlaced(i)).ConfigureAwait(false);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Momentum notifications + handlers (3 handlers per notification)
// ═══════════════════════════════════════════════════════════════════

public sealed record MomentumOrderPlaced(int OrderId) : INotification;

public sealed class MomentumOrderPlacedLogHandler
{
    public Task HandleAsync(MomentumOrderPlaced notification, CancellationToken ct)
        => Task.CompletedTask;
}

public sealed class MomentumOrderPlacedEmailHandler
{
    public Task HandleAsync(MomentumOrderPlaced notification, CancellationToken ct)
        => Task.CompletedTask;
}

public sealed class MomentumOrderPlacedMetricsHandler
{
    public Task HandleAsync(MomentumOrderPlaced notification, CancellationToken ct)
        => Task.CompletedTask;
}

// ═══════════════════════════════════════════════════════════════════
// MediatR notifications + handlers (3 handlers per notification)
// ═══════════════════════════════════════════════════════════════════

public sealed record MediatROrderPlaced(int OrderId) : MediatR.INotification;

[IgnoreHandler]
public sealed class MediatROrderPlacedLogHandler : MediatR.INotificationHandler<MediatROrderPlaced>
{
    public Task Handle(MediatROrderPlaced notification, CancellationToken ct)
        => Task.CompletedTask;
}

[IgnoreHandler]
public sealed class MediatROrderPlacedEmailHandler : MediatR.INotificationHandler<MediatROrderPlaced>
{
    public Task Handle(MediatROrderPlaced notification, CancellationToken ct)
        => Task.CompletedTask;
}

[IgnoreHandler]
public sealed class MediatROrderPlacedMetricsHandler : MediatR.INotificationHandler<MediatROrderPlaced>
{
    public Task Handle(MediatROrderPlaced notification, CancellationToken ct)
        => Task.CompletedTask;
}
