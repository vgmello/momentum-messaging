using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Momentum.Messaging;

namespace Momentum.Messaging.Benchmarks;

/// <summary>
/// Measures the overhead of IMessageContext injection in Momentum handlers.
/// Compares handlers with and without the context parameter.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SendWithContextBenchmark
{
    [Params(1_000, 10_000, 100_000)]
    public int BatchSize { get; set; }

    private IMessageBus _bus = null!;
    private ServiceProvider _provider = null!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddMomentum();
        _provider = services.BuildServiceProvider();
        _bus = _provider.GetRequiredService<IMessageBus>();
    }

    [GlobalCleanup]
    public void Cleanup() => _provider.Dispose();

    [Benchmark(Baseline = true)]
    public async Task<int> Send_WithoutContext()
    {
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var result = await _bus.SendAsync(new PingNoCtx(i)).ConfigureAwait(false);
            sum += result.Value;
        }
        return sum;
    }

    [Benchmark]
    public async Task<int> Send_WithContext()
    {
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var result = await _bus.SendAsync(new PingWithCtx(i)).ConfigureAwait(false);
            sum += result.Value;
        }
        return sum;
    }
}

// ── Without context ──

public sealed record PingNoCtx(int Id) : IRequest<PongNoCtx>;
public sealed record PongNoCtx(int Value);

public sealed class PingNoCtxHandler
{
    public Task<PongNoCtx> HandleAsync(PingNoCtx request, CancellationToken ct)
        => Task.FromResult(new PongNoCtx(request.Id));
}

// ── With context ──

public sealed record PingWithCtx(int Id) : IRequest<PongWithCtx>;
public sealed record PongWithCtx(int Value);

public sealed class PingWithCtxHandler
{
    public Task<PongWithCtx> HandleAsync(PingWithCtx request, IMessageContext ctx, CancellationToken ct)
        => Task.FromResult(new PongWithCtx(request.Id));
}
