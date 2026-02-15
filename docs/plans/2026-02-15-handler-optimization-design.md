# Handler Optimization Design

## Context

Momentum now matches MediatR for basic Send/Publish throughput after eliminating per-dispatch DI scoping. The next step is to close the remaining gap by removing DI resolution overhead entirely, supporting static handlers, eliminating boxing casts, and moving scope control to per-handler attributes.

## Goals

1. **Static handler support** — static classes with static `HandleAsync` methods, no DI registration needed
2. **Method injection** — services injected into handler methods (not constructors), resolved by the generator via type inference
3. **Zero-boxing dispatch** — eliminate `(TResponse)(object)` double-cast via per-message typed dispatch methods and `Unsafe.As`
4. **Per-handler scope control** — attributes `[Scoped]` (default), `[Singleton]`, `[Transient]` control DI scope creation per handler
5. **Comprehensive benchmarks** — raw manual baselines (static, instance, scoped, context, behaviors) vs Momentum vs MediatR

## Design

### 1. Handler Discovery Enhancements

**Static handlers**: Remove the `!m.IsStatic` filter in `DiscoverHandlers`. If both the class and method are static, mark `IsStatic = true` in `HandlerInfo`. Static handlers are not registered in DI.

**Method parameter inspection**: After identifying the message param, `IMessageContext`, and `CancellationToken` by type, classify all remaining parameters as DI-injected services. Store as `List<ServiceParam>` (type + name) on `HandlerInfo`.

**Constructor inspection** (instance handlers only): Inspect the single public constructor to enumerate parameters. All constructor params are DI-resolved. Needed for `new Handler(svc1, svc2)` emission.

**`HandlerInfo` additions**:
```
IsStatic: bool
MethodServices: List<ServiceParam>   // Services injected into HandleAsync method
CtorServices: List<ServiceParam>     // Constructor params (instance handlers only)
```

### 2. Per-Handler Lifetime Attributes

New attributes read at compile time by the generator:

| Attribute | Effect | Generated Code |
|-----------|--------|----------------|
| (none / `[Scoped]`) | Default. DI scope per dispatch. | `using var scope = sp.CreateScope(); resolve from scope` |
| `[Singleton]` | Cached in field on GeneratedMessageBus. | `_handler ??= new Handler(sp.GetRequired...)` |
| `[Transient]` | New instance per call, no scope. | `new Handler(sp.GetRequired...)` from root provider |

For static handlers, the attribute controls how method-injected services are resolved (from a scope or from root), not the handler instance.

### 3. Strongly-Typed Per-Message Dispatch Methods

For each request handler, emit a private typed dispatch method:

```csharp
// Instance, scoped (default):
private async Task<Pong> DispatchPingAsync(Ping msg, CancellationToken ct)
{
    using var scope = _sp.CreateScope();
    var sp = scope.ServiceProvider;
    var handler = new PingHandler(sp.GetRequiredService<IDb>());
    return await handler.HandleAsync(msg, ct).ConfigureAwait(false);
}

// Static, no services:
private static Task<Pong> DispatchPingAsync(Ping msg, CancellationToken ct)
    => PingHandler.HandleAsync(msg, ct);

// Static, method-injected services (scoped):
private async Task<Pong> DispatchPingAsync(Ping msg, CancellationToken ct)
{
    using var scope = _sp.CreateScope();
    return await PingHandler.HandleAsync(msg, scope.ServiceProvider.GetRequiredService<ILogger>(), ct)
        .ConfigureAwait(false);
}

// Singleton (cached in field):
private PingHandler? _pingHandler;
private async Task<Pong> DispatchPingAsync(Ping msg, CancellationToken ct)
{
    var handler = _pingHandler ??= new PingHandler(_sp.GetRequiredService<IDb>());
    return await handler.HandleAsync(msg, ct).ConfigureAwait(false);
}
```

### 4. Zero-Boxing SendAsync via Unsafe.As

Public `SendAsync<TResponse>` switches and calls typed methods, using `Unsafe.As` to reinterpret `Task<ConcreteResponse>` as `Task<TResponse>`:

```csharp
public Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
{
    switch (request)
    {
        case Ping msg:
        {
            var task = DispatchPingAsync(msg, ct);
            return Unsafe.As<Task<Pong>, Task<TResponse>>(ref task);
        }
        default:
            throw new InvalidOperationException(...);
    }
}
```

### 5. Context and Pipeline Integration

Context creation remains per-handler (only when `HasContextParam`), inside the typed dispatch method. Behavior pipeline resolution also moves inside the typed dispatch method, eliminating the need for `(TResponse)(object)` cast in the pipeline return.

### 6. Benchmark Design

**`RawBaselineBenchmark.cs`** — theoretical floor:

| Benchmark | What it measures |
|-----------|------------------|
| `Raw_Static_NoServices` | Static method direct call (absolute floor) |
| `Raw_Static_WithServices` | Static method + DI service resolution |
| `Raw_Instance_NewDirect` | `new Handler()` + call |
| `Raw_Instance_WithDI` | `new Handler(sp.GetRequired...)` + call |
| `Raw_Instance_Scoped` | Same inside `sp.CreateScope()` |
| `Raw_WithContext` | Manual MessageContextScope + handler call |
| `Raw_WithBehavior` | Manual behavior chain wrapping handler |

All with `[Params(10_000, 100_000)]`, `[MemoryDiagnoser]`, `[ShortRunJob]`.

## Files to Modify

| File | Change |
|------|--------|
| `src/Momentum.Messaging/Attributes.cs` | Add `[Scoped]`, `[Singleton]`, `[Transient]` handler lifetime attributes |
| `src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs` | Extend discovery (static, ctor params, method services, lifetime attr), restructure `EmitMessageBus` (typed methods, Unsafe.As, per-handler scope), update `EmitRegistration` (skip static handlers) |
| `benchmarks/.../RawBaselineBenchmark.cs` | New benchmark class for raw manual baselines |

## Constraints

- Generator targets netstandard2.0 / C# 11 — no collection expressions, `required`, etc.
- AOT-first: `Unsafe.As` is AOT-safe (no reflection). All type information known at compile time.
- `TreatWarningsAsErrors=true` — must handle any new trim/AOT warnings.
