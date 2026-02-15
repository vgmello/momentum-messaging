using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Momentum.Messaging;

namespace Momentum.Messaging.Benchmarks;

/// <summary>
/// Measures raw handler invocation overhead against framework dispatch.
/// Establishes the theoretical floor for each dispatch pattern.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class RawBaselineBenchmark
{
    [Params(10_000, 100_000)]
    public int BatchSize { get; set; }

    private ServiceProvider _provider = null!;
    private IMessageBus _bus = null!;
    private MediatR.IMediator _mediator = null!;
    private ServiceProvider _mediatrProvider = null!;

    [GlobalSetup]
    public void Setup()
    {
        var s = new ServiceCollection();
        s.AddMomentum();
        _provider = s.BuildServiceProvider();
        _bus = _provider.GetRequiredService<IMessageBus>();

        var m = new ServiceCollection();
        m.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<RawBaselineBenchmark>());
        _mediatrProvider = m.BuildServiceProvider();
        _mediator = _mediatrProvider.GetRequiredService<MediatR.IMediator>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _provider.Dispose();
        _mediatrProvider.Dispose();
    }

    // ── Raw baselines (theoretical floor) ──

    [Benchmark(Baseline = true)]
    public async Task<int> Raw_Static_NoServices()
    {
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var result = await RawStaticHandler.HandleAsync(new RawPing(i), default).ConfigureAwait(false);
            sum += result.Value;
        }
        return sum;
    }

    [Benchmark]
    public async Task<int> Raw_Instance_NewDirect()
    {
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var handler = new RawInstanceHandler();
            var result = await handler.HandleAsync(new RawPing(i), default).ConfigureAwait(false);
            sum += result.Value;
        }
        return sum;
    }

    [Benchmark]
    public async Task<int> Raw_Instance_Scoped()
    {
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            using var scope = _provider.CreateScope();
            var handler = new RawInstanceHandler();
            var result = await handler.HandleAsync(new RawPing(i), default).ConfigureAwait(false);
            sum += result.Value;
        }
        return sum;
    }

    [Benchmark]
    public async Task<int> Raw_WithContext()
    {
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var ctx = new MessageContextScope(_bus);
            var previous = MessageContextScope.SetCurrent(ctx);
            try
            {
                var handler = new RawInstanceHandler();
                var result = await handler.HandleAsync(new RawPing(i), default).ConfigureAwait(false);
                sum += result.Value;
            }
            finally { MessageContextScope.RestoreCurrent(previous); }
        }
        return sum;
    }

    // ── Framework dispatch ──

    [Benchmark]
    public async Task<int> Momentum_Send()
    {
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var result = await _bus.SendAsync(new RawPing(i)).ConfigureAwait(false);
            sum += result.Value;
        }
        return sum;
    }

    [Benchmark]
    public async Task<int> MediatR_Send()
    {
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var result = await _mediator.Send(new MediatRRawPing(i)).ConfigureAwait(false);
            sum += result.Value;
        }
        return sum;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Messages and handlers for raw baselines
// ═══════════════════════════════════════════════════════════════════

public sealed record RawPing(int Id) : IRequest<RawPong>;
public sealed record RawPong(int Value);

[IgnoreHandler]
public static class RawStaticHandler
{
    public static Task<RawPong> HandleAsync(RawPing request, CancellationToken ct)
        => Task.FromResult(new RawPong(request.Id));
}

[IgnoreHandler]
public sealed class RawInstanceHandler
{
    public Task<RawPong> HandleAsync(RawPing request, CancellationToken ct)
        => Task.FromResult(new RawPong(request.Id));
}

// Momentum handler for RawPing (discovered by generator)
public sealed class RawPingHandler
{
    public Task<RawPong> HandleAsync(RawPing request, CancellationToken ct)
        => Task.FromResult(new RawPong(request.Id));
}

// ═══════════════════════════════════════════════════════════════════
// MediatR comparison
// ═══════════════════════════════════════════════════════════════════

public sealed record MediatRRawPing(int Id) : MediatR.IRequest<MediatRRawPong>;
public sealed record MediatRRawPong(int Value);

[IgnoreHandler]
public sealed class MediatRRawPingHandler : MediatR.IRequestHandler<MediatRRawPing, MediatRRawPong>
{
    public Task<MediatRRawPong> Handle(MediatRRawPing request, CancellationToken ct)
        => Task.FromResult(new MediatRRawPong(request.Id));
}
