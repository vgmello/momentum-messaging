using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Momentum.Messaging;

namespace Momentum.Messaging.Benchmarks;

/// <summary>
/// Compares SendAsync throughput and allocation between Momentum and MediatR.
/// Each iteration sends <see cref="BatchSize"/> request/response messages.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[GroupBenchmarksBy(BenchmarkDotNet.Configs.BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class SendBenchmark
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
        mediatrServices.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<SendBenchmark>());
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
    [BenchmarkCategory("Send")]
    public async Task<int> Momentum_Send()
    {
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var result = await _momentumBus.SendAsync(new MomentumPing(i)).ConfigureAwait(false);
            sum += result.Value;
        }
        return sum;
    }

    [Benchmark]
    [BenchmarkCategory("Send")]
    public async Task<int> MediatR_Send()
    {
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var result = await _mediatrMediator.Send(new MediatRPing(i)).ConfigureAwait(false);
            sum += result.Value;
        }
        return sum;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Momentum messages + handler
// ═══════════════════════════════════════════════════════════════════

public sealed record MomentumPing(int Id) : IRequest<MomentumPong>;
public sealed record MomentumPong(int Value);

public sealed class MomentumPingHandler
{
    public Task<MomentumPong> HandleAsync(MomentumPing request, CancellationToken ct)
        => Task.FromResult(new MomentumPong(request.Id));
}

// ═══════════════════════════════════════════════════════════════════
// MediatR messages + handler
// ═══════════════════════════════════════════════════════════════════

public sealed record MediatRPing(int Id) : MediatR.IRequest<MediatRPong>;
public sealed record MediatRPong(int Value);

[IgnoreHandler] // Exclude from Momentum's source generator
public sealed class MediatRPingHandler : MediatR.IRequestHandler<MediatRPing, MediatRPong>
{
    public Task<MediatRPong> Handle(MediatRPing request, CancellationToken ct)
        => Task.FromResult(new MediatRPong(request.Id));
}
