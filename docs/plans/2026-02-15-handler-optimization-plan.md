# Handler Optimization Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Eliminate DI resolution overhead with manual handler construction, support static handlers with method injection, remove boxing via typed dispatch methods + `Unsafe.As`, and add per-handler lifetime attributes.

**Architecture:** The source generator inspects handler constructors and method parameters at compile time, emitting direct `new Handler(svc1, svc2)` calls instead of `GetRequiredService<Handler>()`. Each request type gets its own strongly-typed dispatch method (`DispatchXxxAsync`), and `SendAsync<TResponse>` calls these via `Unsafe.As` to avoid `(TResponse)(object)` boxing. Per-handler `[Scoped]`/`[Singleton]`/`[Transient]` attributes control scope creation and caching.

**Tech Stack:** Roslyn IIncrementalGenerator (netstandard2.0/C#11), `System.Runtime.CompilerServices.Unsafe`, BenchmarkDotNet

---

### Task 1: Add Lifetime Attributes

**Files:**
- Modify: `src/Momentum.Messaging/Attributes.cs`

**Step 1: Add three new attributes**

Append to `src/Momentum.Messaging/Attributes.cs`:

```csharp
/// <summary>
/// Handler creates a DI scope per dispatch. This is the default lifetime.
/// Services injected via constructor or method parameters are resolved from the scope.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ScopedHandlerAttribute : Attribute;

/// <summary>
/// Handler instance is cached as a singleton. Constructor services are resolved once.
/// Method-injected services are resolved from the root provider per call.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class SingletonHandlerAttribute : Attribute;

/// <summary>
/// Handler is constructed per dispatch without creating a DI scope.
/// Services are resolved from the root provider.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class TransientHandlerAttribute : Attribute;
```

**Step 2: Build to verify**

Run: `dotnet build src/Momentum.Messaging/Momentum.Messaging.csproj`
Expected: Build succeeded, 0 warnings, 0 errors

**Step 3: Commit**

```bash
git add src/Momentum.Messaging/Attributes.cs
git commit -m "feat: add ScopedHandler, SingletonHandler, TransientHandler attributes"
```

---

### Task 2: Extend HandlerInfo and Discovery

**Files:**
- Modify: `src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs` (lines 539-620, HandlerInfo + DiscoverHandlers)

**Step 1: Add constants for new attributes**

In the constants block at the top of `MomentumSourceGenerator` (after line 21), add:

```csharp
private const string ScopedHandlerAttr = "Momentum.Messaging.ScopedHandlerAttribute";
private const string SingletonHandlerAttr = "Momentum.Messaging.SingletonHandlerAttribute";
private const string TransientHandlerAttr = "Momentum.Messaging.TransientHandlerAttribute";
```

**Step 2: Add ServiceParam and HandlerLifetime to models**

Replace the `HandlerInfo` class (line 601) and add new types:

```csharp
internal enum HandlerLifetime { Scoped, Singleton, Transient }

internal sealed class ServiceParam
{
    public string TypeFullName { get; set; } = null!;
    public string ParamName { get; set; } = null!;
}

internal sealed class HandlerInfo
{
    public string HandlerTypeFullName { get; set; } = null!;
    public string HandlerTypeName { get; set; } = null!;
    public string MessageTypeFullName { get; set; } = null!;
    public string MessageTypeName { get; set; } = null!;
    public string ResponseTypeFullName { get; set; } = null!;
    public string MethodName { get; set; } = null!;
    public bool IsNotification { get; set; }
    public bool HasContextParam { get; set; }
    public bool HasCancellationToken { get; set; }
    public bool ReturnsVoidTask { get; set; }
    public bool IsStatic { get; set; }
    public HandlerLifetime Lifetime { get; set; }
    public List<ServiceParam> CtorServices { get; set; } = new List<ServiceParam>();
    public List<ServiceParam> MethodServices { get; set; } = new List<ServiceParam>();
}
```

**Step 3: Update DiscoverHandlers to support static methods**

In `DiscoverHandlers` (line 208), change the method filter from:

```csharp
.Where(m => config.MethodNames.Contains(m.Name) &&
            m.DeclaredAccessibility == Accessibility.Public &&
            !m.IsStatic);
```

to:

```csharp
.Where(m => config.MethodNames.Contains(m.Name) &&
            m.DeclaredAccessibility == Accessibility.Public);
```

**Step 4: Update parameter parsing to collect service params**

Replace the parameter parsing loop (lines 216-241) with logic that:
1. First param is message (unchanged)
2. Known types: `IMessageContext`, `CancellationToken` (handled as before)
3. Unknown types: collected as `MethodServices`
4. Remove the `parameters.Length > 3` limit to allow arbitrary service params

Replace the inner loop and validation (lines 216-241):

```csharp
foreach (var method in methods)
{
    var parameters = method.Parameters;
    if (parameters.Length == 0)
        continue;

    var messageType = parameters[0].Type;
    var hasContext = false;
    var hasCt = false;
    var methodServices = new List<ServiceParam>();

    var valid = true;
    for (var p = 1; p < parameters.Length; p++)
    {
        var pType = parameters[p].Type.ToDisplayString();
        if (pType == IMessageContextFull && !hasCt)
            hasContext = true;
        else if (pType == "System.Threading.CancellationToken")
            hasCt = true;
        else if (!hasCt) // service params must come before CancellationToken
            methodServices.Add(new ServiceParam
            {
                TypeFullName = pType,
                ParamName = parameters[p].Name,
            });
        else
        {
            valid = false;
            break;
        }
    }

    if (!valid)
        continue;
```

**Step 5: Read lifetime attribute and static flag**

After `valid` check, before creating `HandlerInfo`, add:

```csharp
    var isStatic = method.IsStatic && symbol.IsStatic;

    var lifetime = HandlerLifetime.Scoped; // default
    if (HasAttribute(symbol, SingletonHandlerAttr))
        lifetime = HandlerLifetime.Singleton;
    else if (HasAttribute(symbol, TransientHandlerAttr))
        lifetime = HandlerLifetime.Transient;
    // Static handlers default to Transient (no instance to cache)
    if (isStatic && lifetime == HandlerLifetime.Scoped)
        lifetime = HandlerLifetime.Transient;
```

**Step 6: Inspect constructor params for instance handlers**

After lifetime detection:

```csharp
    var ctorServices = new List<ServiceParam>();
    if (!isStatic)
    {
        var ctors = symbol.Constructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsImplicitlyDeclared)
            .ToList();
        if (ctors.Count == 1)
        {
            foreach (var cp in ctors[0].Parameters)
                ctorServices.Add(new ServiceParam
                {
                    TypeFullName = cp.Type.ToDisplayString(),
                    ParamName = cp.Name,
                });
        }
    }
```

**Step 7: Add new fields to HandlerInfo construction**

Add to both the notification and request `HandlerInfo` construction blocks:

```csharp
    IsStatic = isStatic,
    Lifetime = lifetime,
    CtorServices = ctorServices,
    MethodServices = methodServices,
```

**Step 8: Build to verify**

Run: `dotnet build src/Momentum.Messaging.Generators/Momentum.Messaging.Generators.csproj`
Expected: Build succeeded

**Step 9: Commit**

```bash
git add src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs
git commit -m "feat: extend handler discovery with static, lifetime, ctor/method services"
```

---

### Task 3: Emit Typed Dispatch Methods + Unsafe.As

**Files:**
- Modify: `src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs` (EmitMessageBus method)

This is the largest change. Replace the entire `EmitMessageBus` method.

**Step 1: Emit class header with singleton fields**

The generated class now has:
- `private readonly bool _scopedDispatch;` and `internal static bool HasBehaviors;` (existing)
- Per-singleton-handler fields: `private HandlerType? _handlerN;`
- Constructor accepts `MomentumOptions` (existing)

For each request handler with `Lifetime == Singleton && !IsStatic`, emit a field:
```csharp
private PingHandler? _pingHandler;
```

Use a sanitized name based on handler type (replace `.` with `_`).

**Step 2: Emit per-message typed dispatch methods**

For each request handler, emit a strongly-typed private method. The method shape depends on lifetime and static:

**Scoped (default):**
```csharp
private async Task<Pong> DispatchPingAsync(Ping msg, CancellationToken ct)
{
    using var scope = _sp.CreateScope();
    var sp = scope.ServiceProvider;
    var handler = new PingHandler(sp.GetRequiredService<IDb>());
    return await handler.HandleAsync(msg, sp.GetRequiredService<ILogger>(), ct).ConfigureAwait(false);
}
```

**Transient (no scope):**
```csharp
private async Task<Pong> DispatchPingAsync(Ping msg, CancellationToken ct)
{
    var handler = new PingHandler(_sp.GetRequiredService<IDb>());
    return await handler.HandleAsync(msg, ct).ConfigureAwait(false);
}
```

**Singleton (cached):**
```csharp
private async Task<Pong> DispatchPingAsync(Ping msg, CancellationToken ct)
{
    var handler = _pingHandler ??= new PingHandler(_sp.GetRequiredService<IDb>());
    return await handler.HandleAsync(msg, ct).ConfigureAwait(false);
}
```

**Static (no instance):**
```csharp
private async Task<Pong> DispatchPingAsync(Ping msg, CancellationToken ct)
{
    return await PingHandler.HandleAsync(msg, _sp.GetRequiredService<ILogger>(), ct).ConfigureAwait(false);
}
```

**Static with scoped services:**
```csharp
private async Task<Pong> DispatchPingAsync(Ping msg, CancellationToken ct)
{
    using var scope = _sp.CreateScope();
    var sp = scope.ServiceProvider;
    return await PingHandler.HandleAsync(msg, sp.GetRequiredService<ILogger>(), ct).ConfigureAwait(false);
}
```

Each typed method also handles:
- `HasContextParam`: wrap with `MessageContextScope` create/set/restore
- `HasBehaviors`: resolve and build behavior pipeline (same pattern as current, but typed)
- `ReturnsVoidTask`: `await handler.HandleAsync(...); return Unit.Value;`
- For scoped handlers with no ctor services and no method services and no context: simplify to just `using var scope = ...` with direct call

**Step 3: Emit SendAsync with Unsafe.As**

```csharp
public Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
{
    switch (request)
    {
        case Ping msg:
        {
            var task = DispatchPingAsync(msg, ct);
            return System.Runtime.CompilerServices.Unsafe.As<Task<Pong>, Task<TResponse>>(ref task);
        }
        // ... more cases ...
        default:
            throw new InvalidOperationException(...);
    }
}
```

Note: `SendAsync` is no longer `async` — it returns the task directly. Each typed dispatch method is `async` where needed.

For `ReturnsVoidTask` handlers (returning `Unit`): the typed dispatch method returns `Task<Unit>`, and `Unsafe.As<Task<Unit>, Task<TResponse>>` handles the conversion.

**Step 4: Emit PublishAsync (similar pattern)**

For notifications, the per-group dispatch also moves scope creation inside based on whether any handler in the group is scoped. Handler resolution uses `new Handler(...)` instead of `GetRequiredService`.

**Step 5: Add using directive for Unsafe**

In the generated code header, add:
```csharp
using System.Runtime.CompilerServices;
```

**Step 6: Build the full solution**

Run: `dotnet build Momentum.Messaging.sln`
Expected: Build succeeded, 0 warnings, 0 errors

**Step 7: Run the example**

Run: `dotnet run --project examples/Momentum.Messaging.Example`
Expected: Same output as before (MessageId, CorrelationId, handlers called)

**Step 8: Commit**

```bash
git add src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs
git commit -m "feat: typed dispatch methods with Unsafe.As, manual handler construction, per-handler scoping"
```

---

### Task 4: Update DI Registration for New Handler Modes

**Files:**
- Modify: `src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs` (EmitRegistration method)

**Step 1: Skip static handler registration**

In the handler registration loop, skip static handlers (they don't need DI):

```csharp
foreach (var h in handlers)
{
    if (h.IsStatic)
        continue;
    sb.AppendLine($"        services.TryAdd(new ServiceDescriptor(typeof({h.HandlerTypeFullName}), typeof({h.HandlerTypeFullName}), lifetime));");
}
```

Actually, since we're now using `new Handler(...)` directly, we don't need DI registration for ANY handler. The handler itself is never resolved from DI — only its dependencies are. Remove the handler registration loop entirely.

Keep behavior registrations (those are still resolved via `GetServices<IPipelineBehavior<T,R>>()`).

**Step 2: Build and verify**

Run: `dotnet build Momentum.Messaging.sln`
Expected: Build succeeded

**Step 3: Commit**

```bash
git add src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs
git commit -m "feat: remove handler DI registration, resolve only dependencies"
```

---

### Task 5: Remove `_scopedDispatch` Global Flag

**Files:**
- Modify: `src/Momentum.Messaging/MomentumBuilder.cs`
- Modify: `src/Momentum.Messaging/MomentumGeneratedHook.cs`
- Modify: `src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs`

Since scope creation is now per-handler (controlled by attributes), the global `_scopedDispatch` flag and `MomentumOptions.ScopedDispatch` are no longer needed. Scope creation moves entirely into the typed dispatch methods.

**Step 1: Remove `UseScopedDispatch()` from MomentumBuilder**

Remove the `_scopedDispatch` field and `UseScopedDispatch()` method. Remove `MomentumOptions.ScopedDispatch`. Keep `MomentumOptions` class if other options exist, otherwise simplify.

**Step 2: Remove `_scopedDispatch` from generated code**

The generated `GeneratedMessageBus` no longer needs the `_scopedDispatch` field or `MomentumOptions` constructor param. Simplify constructor back to `(IServiceProvider sp, INotificationPublishStrategy publishStrategy)`.

**Step 3: Simplify MomentumGeneratedHook signature**

Revert to: `Action<IServiceCollection, ServiceLifetime, IReadOnlyList<Type>>` (drop MomentumOptions param).

**Step 4: Build and verify**

Run: `dotnet build Momentum.Messaging.sln`
Expected: Build succeeded

**Step 5: Commit**

```bash
git add src/Momentum.Messaging/MomentumBuilder.cs src/Momentum.Messaging/MomentumGeneratedHook.cs src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs
git commit -m "refactor: remove global scoped dispatch, scope is now per-handler via attributes"
```

---

### Task 6: Add Raw Baseline Benchmarks

**Files:**
- Create: `benchmarks/Momentum.Messaging.Benchmarks/RawBaselineBenchmark.cs`

**Step 1: Create the raw baseline benchmark file**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Momentum.Messaging;

namespace Momentum.Messaging.Benchmarks;

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

    // ── Raw baselines ──

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

    // ── Framework baselines ──

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

// ── Messages and handlers ──

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

// MediatR comparison
public sealed record MediatRRawPing(int Id) : MediatR.IRequest<MediatRRawPong>;
public sealed record MediatRRawPong(int Value);

[IgnoreHandler]
public sealed class MediatRRawPingHandler : MediatR.IRequestHandler<MediatRRawPing, MediatRRawPong>
{
    public Task<MediatRRawPong> Handle(MediatRRawPing request, CancellationToken ct)
        => Task.FromResult(new MediatRRawPong(request.Id));
}
```

Note: `RawStaticHandler` and `RawInstanceHandler` are marked `[IgnoreHandler]` so Momentum's generator doesn't discover them (they're used for raw manual calls only). The `RawPing` message IS discovered by the existing `MomentumPingHandler`-style handler for the `Momentum_Send` benchmark — but we need a separate handler for it. Actually, `RawPing` will be picked up by the generator and needs its own Momentum handler.

We need a non-ignored handler for `RawPing` for the Momentum dispatch path:

```csharp
public sealed class RawPingHandler
{
    public Task<RawPong> HandleAsync(RawPing request, CancellationToken ct)
        => Task.FromResult(new RawPong(request.Id));
}
```

**Step 2: Build**

Run: `dotnet build Momentum.Messaging.sln`
Expected: Build succeeded

**Step 3: Run benchmarks**

Run: `dotnet run --project benchmarks/Momentum.Messaging.Benchmarks -c Release -- --filter 'RawBaselineBenchmark*'`
Expected: All benchmarks pass with results

**Step 4: Commit**

```bash
git add benchmarks/Momentum.Messaging.Benchmarks/RawBaselineBenchmark.cs
git commit -m "feat: add raw baseline benchmarks comparing manual vs Momentum vs MediatR"
```

---

### Task 7: Run Full Benchmark Suite and Compare

**Step 1: Run all benchmarks**

Run: `dotnet run --project benchmarks/Momentum.Messaging.Benchmarks -c Release -- --filter '*'`

**Step 2: Compare results**

Create a summary comparing:
- Raw static (floor) vs Momentum vs MediatR
- Raw scoped vs Momentum scoped handler
- Framework overhead = Momentum - Raw

**Step 3: Commit any benchmark adjustments**

---

## Key Considerations

- **Generator constraint**: netstandard2.0 / C# 11 — no collection expressions, `required`, `init`
- **AOT safety**: `Unsafe.As` is AOT-safe (JIT intrinsic, no reflection). Manual `new Handler()` is AOT-safe.
- **`TreatWarningsAsErrors=true`**: Watch for trim warnings on `Unsafe.As` — may need `#pragma warning disable`
- **Backward compatibility**: Existing handlers without lifetime attributes default to `[Scoped]` (same behavior as current `_scopedDispatch` off + `GetRequiredService`). Wait — current behavior is NO scope by default. The design says scoped by default. This is a behavioral change.
  - **Important**: Verify with user that scoped-by-default is intended even though current optimized code defaults to no scope.
- **Constructor ambiguity**: If a handler has multiple public constructors, skip ctor inspection and fall back to `GetRequiredService<Handler>()` resolution.
