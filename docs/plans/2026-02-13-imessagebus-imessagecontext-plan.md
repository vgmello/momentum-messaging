# IMessageBus + IMessageContext Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Rename `IMediator` to `IMessageBus`, introduce `IMessageContext : IMessageBus` with ambient message metadata and auto-propagating correlation/causation IDs, rename transport-level `IMessageContext` to `IDeliveryContext`.

**Architecture:** `IMessageBus` is the singleton dispatch entry point. Each `SendAsync`/`PublishAsync` creates a DI scope with a `MessageContextScope` that tracks message identity and propagates correlation/causation via `AsyncLocal`. The source generator detects optional `IMessageContext` handler parameters and emits dispatch code accordingly.

**Tech Stack:** C# / .NET 10, Roslyn IIncrementalGenerator (netstandard2.0/C#11), Microsoft.Extensions.DependencyInjection

---

### Task 1: Rename IMessageContext to IDeliveryContext in Abstractions

Do this first — it frees up the `IMessageContext` name for the core package.

**Files:**
- Modify: `src/Momentum.Messaging.Abstractions/IMessageConsumer.cs`
- Modify: `src/Momentum.Messaging.Abstractions/SubscriptionOptions.cs`
- Modify: `src/Momentum.Messaging.Outbox/InboxMessageProcessor.cs`

**Step 1: Rename in IMessageConsumer.cs**

In `src/Momentum.Messaging.Abstractions/IMessageConsumer.cs`:

- Rename `IMessageContext` interface (line 38) to `IDeliveryContext`
- Update `IMessageProcessor.ProcessAsync` parameter (line 30) from `IMessageContext` to `IDeliveryContext`
- Update XML doc comments that reference the old name

After rename, the file should contain:

```csharp
public interface IMessageProcessor
{
    Task ProcessAsync(MessageEnvelope envelope, IDeliveryContext context, CancellationToken ct = default);
}

public interface IDeliveryContext
{
    MessageEnvelope Envelope { get; }
    Task AcknowledgeAsync(CancellationToken ct = default);
    Task RejectAsync(bool requeue = true, CancellationToken ct = default);
    int DeliveryAttempt { get; }
}
```

**Step 2: Update SubscriptionOptions.cs comment**

In `src/Momentum.Messaging.Abstractions/SubscriptionOptions.cs` line 25, change:

```
/// Set to false for manual ack via IMessageContext.
```

to:

```
/// Set to false for manual ack via IDeliveryContext.
```

**Step 3: Update InboxMessageProcessor.cs**

In `src/Momentum.Messaging.Outbox/InboxMessageProcessor.cs` line 28, change `IMessageContext context` to `IDeliveryContext context`.

**Step 4: Build to verify**

Run: `dotnet build Momentum.Messaging.sln --nologo`
Expected: 0 errors, 0 warnings

**Step 5: Commit**

```bash
git add -A && git commit -m "refactor: rename IMessageContext to IDeliveryContext in Abstractions"
```

---

### Task 2: Rename IMediator to IMessageBus

**Files:**
- Modify: `src/Momentum.Messaging/IMediator.cs` (rename file to `IMessageBus.cs`)
- Modify: `src/Momentum.Messaging/MomentumBuilder.cs`
- Modify: `src/Momentum.Messaging/MomentumGeneratedHook.cs` (update comment only)
- Modify: `src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs`
- Modify: `examples/Momentum.Messaging.Example/Program.cs`
- Modify: `examples/Momentum.Messaging.Example/OutboxExample.cs`

**Step 1: Rename the interface**

Rename file `src/Momentum.Messaging/IMediator.cs` to `src/Momentum.Messaging/IMessageBus.cs`.

In the file, change line 3:

```csharp
public interface IMediator
```

to:

```csharp
public interface IMessageBus
```

**Step 2: Update source generator emitted code**

In `src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs`:

Line 285 — change emitted class declaration:
```csharp
sb.AppendLine("internal sealed class GeneratedMediator : IMediator");
```
to:
```csharp
sb.AppendLine("internal sealed class GeneratedMessageBus : IMessageBus");
```

Line 290 — change emitted constructor:
```csharp
sb.AppendLine("    public GeneratedMediator(IServiceProvider sp, INotificationPublishStrategy publishStrategy)");
```
to:
```csharp
sb.AppendLine("    public GeneratedMessageBus(IServiceProvider sp, INotificationPublishStrategy publishStrategy)");
```

Line 405 — change emitted DI registration:
```csharp
sb.AppendLine("        services.TryAddSingleton<IMediator, GeneratedMediator>();");
```
to:
```csharp
sb.AppendLine("        services.TryAddSingleton<IMessageBus, GeneratedMessageBus>();");
```

Line 91 — rename emitted file:
```csharp
ctx.AddSource("MomentumMediator.g.cs", EmitMediator(handlers));
```
to:
```csharp
ctx.AddSource("MomentumMessageBus.g.cs", EmitMediator(handlers));
```

**Step 3: Update example Program.cs**

In `examples/Momentum.Messaging.Example/Program.cs`:

Line 129:
```csharp
var mediator = provider.GetRequiredService<IMediator>();
```
to:
```csharp
var bus = provider.GetRequiredService<IMessageBus>();
```

Lines 131, 134, 136 — change `mediator.` to `bus.`.

**Step 4: Update example OutboxExample.cs**

In `examples/Momentum.Messaging.Example/OutboxExample.cs`:

Line 92: `private readonly IMediator _mediator;` → `private readonly IMessageBus _bus;`
Line 96: `IMediator mediator` → `IMessageBus bus`
Line 98: `_mediator = mediator` → `_bus = bus`
Lines 110-111: `_mediator.SendAsync` → `_bus.SendAsync`

**Step 5: Build to verify**

Run: `dotnet build Momentum.Messaging.sln --nologo`
Expected: 0 errors, 0 warnings

**Step 6: Commit**

```bash
git add -A && git commit -m "refactor: rename IMediator to IMessageBus"
```

---

### Task 3: Create IMessageContext interface and MessageContextScope

**Files:**
- Create: `src/Momentum.Messaging/IMessageContext.cs`

**Step 1: Create the interface and internal implementation**

Create `src/Momentum.Messaging/IMessageContext.cs`:

```csharp
using Momentum.Messaging.Abstractions;

namespace Momentum.Messaging;

/// <summary>
/// Ambient message context available during dispatch. Extends <see cref="IMessageBus"/>
/// so handlers can send/publish with automatic correlation/causation propagation.
/// </summary>
public interface IMessageContext : IMessageBus
{
    /// <summary>Unique identifier for this message.</summary>
    string MessageId { get; }

    /// <summary>Correlation ID tracing the originating request chain.</summary>
    string? CorrelationId { get; }

    /// <summary>MessageId of the message that caused this one.</summary>
    string? CausationId { get; }

    /// <summary>Source topic/queue (null for in-memory origin).</summary>
    string? Source { get; }

    /// <summary>Partition/routing key (null if not partitioned).</summary>
    string? PartitionKey { get; }

    /// <summary>Message headers.</summary>
    MessageHeaders Headers { get; }

    /// <summary>When the message was created.</summary>
    DateTimeOffset Timestamp { get; }

    /// <summary>Raw inbound envelope when consumed from a transport; null for in-memory.</summary>
    MessageEnvelope? Envelope { get; }
}

/// <summary>
/// Scoped implementation of <see cref="IMessageContext"/>.
/// Created per dispatch by the generated message bus.
/// </summary>
internal sealed class MessageContextScope : IMessageContext
{
    private static readonly AsyncLocal<MessageContextScope?> CurrentScope = new();

    /// <summary>Get the current ambient context, or null if none.</summary>
    internal static MessageContextScope? Current => CurrentScope.Value;

    private readonly IMessageBus _bus;

    internal MessageContextScope(IMessageBus bus)
    {
        _bus = bus;
    }

    public string MessageId { get; internal set; } = null!;
    public string? CorrelationId { get; internal set; }
    public string? CausationId { get; internal set; }
    public string? Source { get; internal set; }
    public string? PartitionKey { get; internal set; }
    public MessageHeaders Headers { get; internal set; } = MessageHeaders.Empty;
    public DateTimeOffset Timestamp { get; internal set; }
    public MessageEnvelope? Envelope { get; internal set; }

    public Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)
        => _bus.SendAsync(request, ct);

    public Task PublishAsync<TNotification>(TNotification notification, CancellationToken ct = default)
        where TNotification : INotification
        => _bus.PublishAsync(notification, ct);

    /// <summary>
    /// Set this scope as the ambient context, returning the previous value
    /// so it can be restored after dispatch.
    /// </summary>
    internal static MessageContextScope? SetCurrent(MessageContextScope scope)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = scope;
        return previous;
    }

    /// <summary>Restore the previous ambient context.</summary>
    internal static void RestoreCurrent(MessageContextScope? previous)
    {
        CurrentScope.Value = previous;
    }
}
```

**Step 2: Build to verify**

Run: `dotnet build Momentum.Messaging.sln --nologo`
Expected: 0 errors, 0 warnings

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: add IMessageContext interface and MessageContextScope"
```

---

### Task 4: Update source generator — scoped dispatch with context

This is the largest task. The generator must:
1. Detect `IMessageContext` parameters in handler methods
2. Emit `GeneratedMessageBus` that creates DI scopes and `MessageContextScope` per dispatch
3. Pass the context to handlers that accept it

**Files:**
- Modify: `src/Momentum.Messaging.Generators/MomentumSourceGenerator.cs`

**Step 1: Add HasContext to HandlerInfo**

Add a new property to the `HandlerInfo` class (around line 458):

```csharp
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
}
```

**Step 2: Update DiscoverHandlers to detect IMessageContext parameter**

Add a constant at top of class (after line 20):

```csharp
private const string IMessageContextFull = "Momentum.Messaging.IMessageContext";
```

Replace the parameter validation block in `DiscoverHandlers` (lines 214-222) with:

```csharp
foreach (var method in methods)
{
    var parameters = method.Parameters;
    if (parameters.Length == 0 || parameters.Length > 3)
        continue;

    var messageType = parameters[0].Type;
    var hasContext = false;
    var hasCt = false;

    // Parse remaining params: optional IMessageContext, optional CancellationToken
    for (var p = 1; p < parameters.Length; p++)
    {
        var pType = parameters[p].Type.ToDisplayString();
        if (pType == IMessageContextFull)
            hasContext = true;
        else if (pType == "System.Threading.CancellationToken")
            hasCt = true;
        else
            goto nextMethod; // unknown param type — skip this method
    }
```

Update both handler creation blocks (request + notification) to include:

```csharp
HasContextParam = hasContext,
HasCancellationToken = hasCt,
```

Add `nextMethod:` continue label at the end of the inner foreach:

```csharp
    nextMethod:;
}
```

**Step 3: Rewrite EmitMediator to create scopes and pass context**

Replace the `EmitMediator` method entirely. The generated `GeneratedMessageBus`:
- Injects `IServiceProvider` and `INotificationPublishStrategy`
- Each `SendAsync` call creates a DI scope, creates `MessageContextScope`, sets `AsyncLocal`, resolves handler, dispatches, restores `AsyncLocal`
- Correlation/causation propagation: reads `MessageContextScope.Current` to get parent, inherits `CorrelationId`, sets `CausationId = parent.MessageId`

```csharp
private static string EmitMediator(List<HandlerInfo> handlers)
{
    var requests = handlers.Where(h => !h.IsNotification).ToList();
    var notificationGroups = handlers.Where(h => h.IsNotification)
        .GroupBy(h => h.MessageTypeFullName)
        .ToList();

    var sb = new StringBuilder(8192);
    sb.AppendLine("// <auto-generated />");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    sb.AppendLine("using Momentum.Messaging;");
    sb.AppendLine();
    sb.AppendLine("namespace Momentum.Messaging.Generated;");
    sb.AppendLine();
    sb.AppendLine("internal sealed class GeneratedMessageBus : IMessageBus");
    sb.AppendLine("{");
    sb.AppendLine("    private readonly IServiceProvider _sp;");
    sb.AppendLine("    private readonly INotificationPublishStrategy _publishStrategy;");
    sb.AppendLine();
    sb.AppendLine("    public GeneratedMessageBus(IServiceProvider sp, INotificationPublishStrategy publishStrategy)");
    sb.AppendLine("    {");
    sb.AppendLine("        _sp = sp;");
    sb.AppendLine("        _publishStrategy = publishStrategy;");
    sb.AppendLine("    }");
    sb.AppendLine();

    // ── Helper: create scoped context ──
    sb.AppendLine("    private static MessageContextScope CreateScope(IServiceProvider scopedSp, IMessageBus bus)");
    sb.AppendLine("    {");
    sb.AppendLine("        var parent = MessageContextScope.Current;");
    sb.AppendLine("        var scope = new MessageContextScope(bus);");
    sb.AppendLine("        scope.MessageId = System.Guid.NewGuid().ToString(\"N\");");
    sb.AppendLine("        scope.CorrelationId = parent?.CorrelationId ?? System.Guid.NewGuid().ToString(\"N\");");
    sb.AppendLine("        scope.CausationId = parent?.MessageId;");
    sb.AppendLine("        scope.Timestamp = System.DateTimeOffset.UtcNow;");
    sb.AppendLine("        return scope;");
    sb.AppendLine("    }");
    sb.AppendLine();

    // ── SendAsync ──
    sb.AppendLine("    public async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)");
    sb.AppendLine("    {");
    sb.AppendLine("        using var diScope = _sp.CreateScope();");
    sb.AppendLine("        var scopedSp = diScope.ServiceProvider;");
    sb.AppendLine("        var ctx = CreateScope(scopedSp, this);");
    sb.AppendLine("        var previous = MessageContextScope.SetCurrent(ctx);");
    sb.AppendLine("        try");
    sb.AppendLine("        {");
    sb.AppendLine("            switch (request)");
    sb.AppendLine("            {");

    foreach (var req in requests)
    {
        sb.AppendLine($"                case {req.MessageTypeFullName} msg:");
        sb.AppendLine("                {");
        sb.AppendLine($"                    var handler = scopedSp.GetRequiredService<{req.HandlerTypeFullName}>();");
        sb.AppendLine($"                    var behaviors = scopedSp.GetServices<IPipelineBehavior<{req.MessageTypeFullName}, {req.ResponseTypeFullName}>>();");

        // Build the innermost call based on handler signature
        var handlerCall = BuildHandlerCall(req, "msg");
        sb.AppendLine($"                    NextDelegate<{req.ResponseTypeFullName}> pipeline = () => {handlerCall};");

        sb.AppendLine("                    foreach (var behavior in behaviors.Reverse())");
        sb.AppendLine("                    {");
        sb.AppendLine("                        var next = pipeline;");
        sb.AppendLine("                        var b = behavior;");
        sb.AppendLine("                        pipeline = () => b.HandleAsync(msg, next, ct);");
        sb.AppendLine("                    }");
        sb.AppendLine("                    return (Task<TResponse>)(object)pipeline();");
        sb.AppendLine("                }");
    }

    sb.AppendLine("                default:");
    sb.AppendLine("                    throw new InvalidOperationException(");
    sb.AppendLine("                        $\"No handler found for {request.GetType().Name}. Ensure a handler follows Momentum conventions.\");");
    sb.AppendLine("            }");
    sb.AppendLine("        }");
    sb.AppendLine("        finally");
    sb.AppendLine("        {");
    sb.AppendLine("            MessageContextScope.RestoreCurrent(previous);");
    sb.AppendLine("        }");
    sb.AppendLine("    }");
    sb.AppendLine();

    // ── PublishAsync ──
    sb.AppendLine("    public async Task PublishAsync<TNotification>(TNotification notification, CancellationToken ct = default)");
    sb.AppendLine("        where TNotification : INotification");
    sb.AppendLine("    {");
    sb.AppendLine("        using var diScope = _sp.CreateScope();");
    sb.AppendLine("        var scopedSp = diScope.ServiceProvider;");
    sb.AppendLine("        var ctx = CreateScope(scopedSp, this);");
    sb.AppendLine("        var previous = MessageContextScope.SetCurrent(ctx);");
    sb.AppendLine("        try");
    sb.AppendLine("        {");

    foreach (var group in notificationGroups)
    {
        sb.AppendLine($"            if (notification is {group.Key} typedNotification)");
        sb.AppendLine("            {");
        sb.AppendLine($"                var handlers = new Func<{group.Key}, CancellationToken, Task>[]");
        sb.AppendLine("                {");
        foreach (var h in group)
        {
            var call = BuildNotificationHandlerCall(h);
            sb.AppendLine($"                    (n, c) => {{ var h = scopedSp.GetRequiredService<{h.HandlerTypeFullName}>(); return {call}; }},");
        }
        sb.AppendLine("                };");
        sb.AppendLine("                await _publishStrategy.PublishAsync(");
        sb.AppendLine("                    (IReadOnlyList<Func<TNotification, CancellationToken, Task>>)(object)handlers,");
        sb.AppendLine("                    notification, ct).ConfigureAwait(false);");
        sb.AppendLine("                return;");
        sb.AppendLine("            }");
    }

    sb.AppendLine("        }");
    sb.AppendLine("        finally");
    sb.AppendLine("        {");
    sb.AppendLine("            MessageContextScope.RestoreCurrent(previous);");
    sb.AppendLine("        }");
    sb.AppendLine("    }");
    sb.AppendLine("}");

    return sb.ToString();
}

private static string BuildHandlerCall(HandlerInfo h, string msgVar)
{
    // Build: handler.MethodName(msg[, ctx][, ct])
    var args = msgVar;
    if (h.HasContextParam)
        args += ", ctx";
    if (h.HasCancellationToken)
        args += ", ct";
    return $"handler.{h.MethodName}({args})";
}

private static string BuildNotificationHandlerCall(HandlerInfo h)
{
    // Build: h.MethodName(n[, ctx][, c])
    var args = "n";
    if (h.HasContextParam)
        args += ", MessageContextScope.Current!";
    if (h.HasCancellationToken)
        args += ", c";
    return $"h.{h.MethodName}({args})";
}
```

**Step 4: Update EmitRegistration — register IMessageContext scoped**

In `EmitRegistration`, after the `IMessageBus` registration line (was line 405), add:

```csharp
sb.AppendLine("        services.TryAddScoped<IMessageContext>(sp => MessageContextScope.Current ?? throw new InvalidOperationException(");
sb.AppendLine("            \"IMessageContext is only available during message dispatch. Use IMessageBus for sending outside handlers.\"));");
```

**Step 5: Update EmitMediator to make CreateScope and MessageContextScope internal accessible**

The generated code lives in `Momentum.Messaging.Generated` namespace and needs access to `MessageContextScope` which is `internal` in `Momentum.Messaging`. Since the generator emits code into the consuming assembly (not into `Momentum.Messaging` itself), we need `MessageContextScope` to be `public` instead of `internal`.

Change `MessageContextScope` visibility in `src/Momentum.Messaging/IMessageContext.cs` from `internal sealed class` to `public sealed class`. Keep the `internal set` on properties and `internal` constructors/methods.

**Step 6: Build to verify**

Run: `dotnet build Momentum.Messaging.sln --nologo`
Expected: 0 errors, 0 warnings

**Step 7: Commit**

```bash
git add -A && git commit -m "feat: update source generator for scoped dispatch with IMessageContext"
```

---

### Task 5: Update CLAUDE.md and design doc

**Files:**
- Modify: `CLAUDE.md`
- Modify: `docs/design/design.md`

**Step 1: Update CLAUDE.md**

Replace `IMediator` references with `IMessageBus`. Add `IMessageContext` to the architecture table or design principles section. Mention `IDeliveryContext` rename.

**Step 2: Build to verify nothing broke**

Run: `dotnet build Momentum.Messaging.sln --nologo`
Expected: 0 errors, 0 warnings

**Step 3: Commit**

```bash
git add -A && git commit -m "docs: update CLAUDE.md and design doc for IMessageBus/IMessageContext"
```

---

### Task 6: Update example to demonstrate IMessageContext

**Files:**
- Modify: `examples/Momentum.Messaging.Example/Program.cs`

**Step 1: Add a handler that uses IMessageContext**

Add a new handler to `Program.cs` that demonstrates the context parameter:

```csharp
public sealed class CreateOrderHandler
{
    public async Task<OrderResult> HandleAsync(CreateOrder req, IMessageContext ctx, CancellationToken ct)
    {
        Console.WriteLine($"  MessageId:     {ctx.MessageId}");
        Console.WriteLine($"  CorrelationId: {ctx.CorrelationId}");
        Console.WriteLine($"  CausationId:   {ctx.CausationId ?? "(root)"}");
        return new OrderResult(Guid.NewGuid(), DateTime.UtcNow);
    }
}
```

**Step 2: Build and run to verify**

Run: `dotnet build Momentum.Messaging.sln --nologo && dotnet run --project examples/Momentum.Messaging.Example`
Expected: Output shows MessageId, CorrelationId, CausationId values

**Step 3: Commit**

```bash
git add -A && git commit -m "feat: update example to demonstrate IMessageContext usage"
```
