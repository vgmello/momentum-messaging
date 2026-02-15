# IMessageBus + IMessageContext Design

> Date: 2026-02-13

## Problem

`IMediator` is too narrow a name for the framework's dispatch interface. As Momentum.Messaging grows to support external transports, handlers need access to ambient message metadata (correlation IDs, headers, source topic) without extra boilerplate. Today there is no way for a handler to access or propagate this context.

## Design

### Interface Hierarchy

```csharp
public interface IMessageBus
{
    Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default);
    Task PublishAsync<TNotification>(TNotification notification, CancellationToken ct = default)
        where TNotification : INotification;
}

public interface IMessageContext : IMessageBus
{
    // Identity
    string MessageId { get; }
    string? CorrelationId { get; }
    string? CausationId { get; }

    // Routing
    string? Source { get; }
    string? PartitionKey { get; }

    // Metadata
    MessageHeaders Headers { get; }
    DateTimeOffset Timestamp { get; }

    // Raw envelope (when consumed from transport)
    MessageEnvelope? Envelope { get; }
}
```

`IMessageBus` replaces `IMediator`. `IMessageContext` extends it with ambient metadata.

### Handler Signatures

The source generator supports these parameter patterns:

```csharp
// Minimal — same as today, just renamed
HandleAsync(CreateOrder req, CancellationToken ct)

// With context parameter — generator detects IMessageContext
HandleAsync(CreateOrder req, IMessageContext ctx, CancellationToken ct)

// Constructor injection also works — IMessageContext is scoped per dispatch
public CreateOrderHandler(IMessageContext ctx) => _ctx = ctx;
```

Generator supports 1-3 method parameters:
1. `(TMessage)` or `(TMessage, CancellationToken)`
2. `(TMessage, IMessageContext)` or `(TMessage, IMessageContext, CancellationToken)`

### Context Lifecycle & Scoping

Every `SendAsync`/`PublishAsync` call creates a new `MessageContextScope`:

- `MessageId` = new GUID (even for in-memory calls)
- `CorrelationId` = new GUID at root, inherited from parent in nested calls
- `CausationId` = null at root, parent's `MessageId` in nested calls
- `Source` = null for in-memory, populated from inbound envelope for transport
- `Headers` = empty for in-memory, populated from envelope for transport
- `Timestamp` = `DateTimeOffset.UtcNow`

Parent context propagation uses `AsyncLocal<MessageContextScope?>`. Each dispatch creates a DI scope, resolves the handler within it. After dispatch, `AsyncLocal` restores the previous value.

### DI Registration

Generated code emits:

```csharp
services.TryAddSingleton<IMessageBus, GeneratedMessageBus>();
services.TryAddScoped<IMessageContext, MessageContextScope>();
```

`GeneratedMessageBus` creates a DI scope per dispatch, initializes `MessageContextScope`, and resolves the handler within that scope.

### Renames

| Before | After |
|--------|-------|
| `IMediator` | `IMessageBus` |
| `GeneratedMediator` (emitted) | `GeneratedMessageBus` (emitted) |
| `IMessageContext` (in Abstractions — ack/reject) | `IDeliveryContext` |

### New Types

| Type | Location | Lifetime |
|------|----------|----------|
| `IMessageContext` | `Momentum.Messaging` | Scoped |
| `MessageContextScope` | `Momentum.Messaging` (internal) | Scoped |

### Unchanged

- `IRequest<T>`, `INotification`, `Unit`
- `IPipelineBehavior<,>`, `NextDelegate<>`
- Handler discovery conventions (suffix, method name)
- Transport abstractions (`MessageEnvelope`, `IMessagePublisher`, etc.)
- Outbox/inbox behavior (references updated from `IMediator` to `IMessageBus`)
- `MomentumBuilder` API
