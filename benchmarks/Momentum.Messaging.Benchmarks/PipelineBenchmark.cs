using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Momentum.Messaging;

namespace Momentum.Messaging.Benchmarks;

/// <summary>
/// Compares pipeline behavior overhead between Momentum and MediatR.
/// Tests: no pipeline, 1 behavior, 3 behaviors.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class PipelineBenchmark
{
    [Params(10_000, 100_000)]
    public int BatchSize { get; set; }

    // ── Momentum providers ──
    private ServiceProvider _momentumNoPipeline = null!;
    private ServiceProvider _momentumOneBehavior = null!;
    private ServiceProvider _momentumThreeBehaviors = null!;

    // ── MediatR providers ──
    private ServiceProvider _mediatrNoPipeline = null!;
    private ServiceProvider _mediatrOneBehavior = null!;
    private ServiceProvider _mediatrThreeBehaviors = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Momentum — no pipeline
        var s1 = new ServiceCollection();
        s1.AddMomentum();
        _momentumNoPipeline = s1.BuildServiceProvider();

        // Momentum — 1 behavior
        var s2 = new ServiceCollection();
        s2.AddMomentum(m => m.AddBehavior(typeof(MomentumPassthroughBehavior<,>)));
        _momentumOneBehavior = s2.BuildServiceProvider();

        // Momentum — 3 behaviors
        var s3 = new ServiceCollection();
        s3.AddMomentum(m =>
        {
            m.AddBehavior(typeof(MomentumPassthroughBehavior<,>));
            m.AddBehavior(typeof(MomentumPassthroughBehavior2<,>));
            m.AddBehavior(typeof(MomentumPassthroughBehavior3<,>));
        });
        _momentumThreeBehaviors = s3.BuildServiceProvider();

        // MediatR — no pipeline
        var m1 = new ServiceCollection();
        m1.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<PipelineBenchmark>());
        _mediatrNoPipeline = m1.BuildServiceProvider();

        // MediatR — 1 behavior
        var m2 = new ServiceCollection();
        m2.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<PipelineBenchmark>());
        m2.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(MediatRPassthroughBehavior<,>));
        _mediatrOneBehavior = m2.BuildServiceProvider();

        // MediatR — 3 behaviors
        var m3 = new ServiceCollection();
        m3.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<PipelineBenchmark>());
        m3.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(MediatRPassthroughBehavior<,>));
        m3.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(MediatRPassthroughBehavior2<,>));
        m3.AddTransient(typeof(MediatR.IPipelineBehavior<,>), typeof(MediatRPassthroughBehavior3<,>));
        _mediatrThreeBehaviors = m3.BuildServiceProvider();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _momentumNoPipeline.Dispose();
        _momentumOneBehavior.Dispose();
        _momentumThreeBehaviors.Dispose();
        _mediatrNoPipeline.Dispose();
        _mediatrOneBehavior.Dispose();
        _mediatrThreeBehaviors.Dispose();
    }

    // ── Momentum benchmarks ──

    [Benchmark(Baseline = true)]
    public Task<int> Momentum_NoPipeline()
        => RunMomentum(_momentumNoPipeline);

    [Benchmark]
    public Task<int> Momentum_1Behavior()
        => RunMomentum(_momentumOneBehavior);

    [Benchmark]
    public Task<int> Momentum_3Behaviors()
        => RunMomentum(_momentumThreeBehaviors);

    // ── MediatR benchmarks ──

    [Benchmark]
    public Task<int> MediatR_NoPipeline()
        => RunMediatR(_mediatrNoPipeline);

    [Benchmark]
    public Task<int> MediatR_1Behavior()
        => RunMediatR(_mediatrOneBehavior);

    [Benchmark]
    public Task<int> MediatR_3Behaviors()
        => RunMediatR(_mediatrThreeBehaviors);

    // ── Helpers ──

    private async Task<int> RunMomentum(ServiceProvider sp)
    {
        var bus = sp.GetRequiredService<IMessageBus>();
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var result = await bus.SendAsync(new PipelinePing(i)).ConfigureAwait(false);
            sum += result.Value;
        }
        return sum;
    }

    private async Task<int> RunMediatR(ServiceProvider sp)
    {
        var mediator = sp.GetRequiredService<MediatR.IMediator>();
        var sum = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var result = await mediator.Send(new MediatRPipelinePing(i)).ConfigureAwait(false);
            sum += result.Value;
        }
        return sum;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Momentum messages + handlers + behaviors
// ═══════════════════════════════════════════════════════════════════

public sealed record PipelinePing(int Id) : IRequest<PipelinePong>;
public sealed record PipelinePong(int Value);

public sealed class PipelinePingHandler
{
    public Task<PipelinePong> HandleAsync(PipelinePing request, CancellationToken ct)
        => Task.FromResult(new PipelinePong(request.Id));
}

public sealed class MomentumPassthroughBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> HandleAsync(TRequest request, NextDelegate<TResponse> next, CancellationToken ct)
        => next();
}

public sealed class MomentumPassthroughBehavior2<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> HandleAsync(TRequest request, NextDelegate<TResponse> next, CancellationToken ct)
        => next();
}

public sealed class MomentumPassthroughBehavior3<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> HandleAsync(TRequest request, NextDelegate<TResponse> next, CancellationToken ct)
        => next();
}

// ═══════════════════════════════════════════════════════════════════
// MediatR messages + handlers + behaviors
// ═══════════════════════════════════════════════════════════════════

public sealed record MediatRPipelinePing(int Id) : MediatR.IRequest<MediatRPipelinePong>;
public sealed record MediatRPipelinePong(int Value);

[IgnoreHandler]
public sealed class MediatRPipelinePingHandler : MediatR.IRequestHandler<MediatRPipelinePing, MediatRPipelinePong>
{
    public Task<MediatRPipelinePong> Handle(MediatRPipelinePing request, CancellationToken ct)
        => Task.FromResult(new MediatRPipelinePong(request.Id));
}

[IgnoreHandler]
public sealed class MediatRPassthroughBehavior<TRequest, TResponse> : MediatR.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken ct)
        => next();
}

[IgnoreHandler]
public sealed class MediatRPassthroughBehavior2<TRequest, TResponse> : MediatR.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken ct)
        => next();
}

[IgnoreHandler]
public sealed class MediatRPassthroughBehavior3<TRequest, TResponse> : MediatR.IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, MediatR.RequestHandlerDelegate<TResponse> next, CancellationToken ct)
        => next();
}
