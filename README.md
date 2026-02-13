# Momentum.Messaging — Design Session Context

> Exported from conversation on February 14, 2026.
> Use this document to resume the design session in a new conversation.

---

## Project Vision

Momentum.Messaging is an incredibly lightweight, high-performance, low memory footprint messaging framework for C# .NET. It prioritizes:

- **Best-in-class developer experience**
- **Extensibility** — messaging technologies (Kafka, RabbitMQ, EventHub) added as plugins via common interfaces
- **In-memory message bus** for local dispatch
- **Resilience** — inbox/outbox patterns for messages arriving from external systems
- **Dapper** for database operations (inbox/outbox persistence)
- **Persistence packages as extensions** (SQL Server, PostgreSQL, etc.)
- **Source generation over reflection** — zero runtime reflection, everything compile-time

---

## Key Design Decisions Made

### 1. Convention-Based Handlers (No Interfaces)

Handlers are plain classes — no `IRequestHandler<,>` interface required. Discovery is by convention:

- Classes ending in `Handler` (configurable suffix)
- With a `HandleAsync` method (configurable method name)
- First parameter implements `IRequest<T>` or `INotification`

Override mechanisms:

- `[MomentumHandler]` — explicit opt-in for non-conventional names
- `[IgnoreHandler]` — explicit opt-out even if name matches

### 2. Pure Source Generation (No Runtime Reflection Fallback)

We explicitly rejected a reflection-based fallback message bus. The source generator is the only path. If the generator hasn't run, `AddMomentum()` throws at startup via `MomentumGeneratedHook`.

The generator emits:

- `GeneratedMessageBus` with a `switch`-based dispatch table (zero reflection)
- `MomentumServiceRegistration` with all DI registrations
- Uses `[ModuleInitializer]` to plug into `MomentumGeneratedHook`

### 3. Multi-Value Handler Suffixes and Method Names

Both suffixes and method names support multiple values via:

**Assembly attributes** (preferred, lives in code):

```csharp
[assembly: MomentumMediator]
[assembly: MomentumHandlerSuffix("Handler")]
[assembly: MomentumHandlerSuffix("Processor")]
[assembly: MomentumHandlerSuffix("Consumer")]
[assembly: MomentumMethodName("HandleAsync")]
[assembly: MomentumMethodName("ExecuteAsync")]
```

**.csproj properties** (fallback, semicolon-separated):

```xml
<MomentumHandlerSuffix>Handler;Processor;Consumer</MomentumHandlerSuffix>
<MomentumMethodName>HandleAsync;ExecuteAsync</MomentumMethodName>
```

Assembly attributes take precedence if both are present.

### 4. Custom Discovery Strategy Override

```csharp
[assembly: MomentumDiscoveryStrategy(typeof(MyCompany.CustomDiscovery))]
```

Or via `.csproj`:

```xml
<MomentumDiscoveryStrategy>MyCompany.CustomDiscovery, MyCompany.Infra</MomentumDiscoveryStrategy>
```

### 5. No Runtime Discovery Code

All discovery configuration is compile-time only. No `IHandlerDiscoveryStrategy` at runtime, no `UseHandlerSuffix()` on the builder. The builder is purely for:

- Pipeline behaviors
- Notification publish strategy
- Handler DI lifetime

### 6. Serialization — Pluggable, No Default

`IMessageSerializer` has no default implementation. You must install a serializer package (e.g., `Momentum.Messaging.Json`). This keeps the core framework lightweight and forces an explicit choice.

### 7. Outbox as Pipeline Behavior

The outbox integrates as `OutboxBehavior<,>` — a pipeline behavior that wraps message bus handlers. NOT a decorator around `IMessagePublisher`.

Flow:

1. Handler runs, calls `IOutboxCollector.Add()` to stage messages
2. Handler writes business data using the ambient DB transaction
3. Behavior flushes collector → `IOutboxStore.StoreAsync()` in same transaction
4. Behavior commits the transaction (handler does NOT commit)
5. `OutboxProcessor` background service polls and dispatches to transport

### 8. Transaction Ownership — Behavior Owns the Transaction

The `OutboxBehavior` opens and commits/rolls back the DB transaction. Handlers never call commit or rollback. They just use `_tx.Current!.Transaction` for their Dapper calls.

### 9. Nested Handler Transaction Support

When handler A calls `bus.SendAsync()` (via `IMessageBus`) which triggers handler B:

- `OutboxTransactionAccessor` is scoped — tracks nesting depth
- Transactional handlers join the ambient transaction (increment depth)
- Only the outermost scope (owner) commits/rolls back
- Non-transactional handlers get their own independent transaction

### 10. Configurable Global Transaction Mode

Two modes, set at startup:

```csharp
builder.Services.AddMomentumOutbox(outbox =>
{
    outbox.TransactionMode = TransactionMode.TransactionalByDefault;
    // or: TransactionMode.NonTransactionalByDefault
});
```

- **TransactionalByDefault**: all handlers join ambient transaction. Use `[NonTransactional]` to opt out.
- **NonTransactionalByDefault**: all handlers run independently. Use `[Transactional]` to opt in.

The source generator emits `IHandlerTransactionRegistry` that combines the global mode with per-handler attribute overrides.

### 11. Nested Handler Failure Semantics

Given: Handler A (transactional) → Handler B (non-transactional) → Handler C (transactional)

- A starts ambient transaction (owner)
- B runs independently, commits immediately — **safe from parent rollback**
- C joins A's ambient transaction
- If C throws: A + C rollback (business data + outbox messages), B's work survives

---

## Package Structure

```
Momentum.Messaging                  ← core message bus, IMessageContext, markers, builder, attributes
Momentum.Messaging.Abstractions     ← transport contracts (zero deps)
Momentum.Messaging.Generators       ← source generator (compile-time only)
Momentum.Messaging.Outbox           ← outbox/inbox, behavior, processor
─── future ───
Momentum.Messaging.Json             ← System.Text.Json serializer
Momentum.Messaging.MessagePack      ← MessagePack serializer
Momentum.Messaging.Kafka            ← transport plugin
Momentum.Messaging.RabbitMq         ← transport plugin
Momentum.Messaging.EventHubs        ← transport plugin
Momentum.Messaging.SqlServer        ← Dapper-based persistence
Momentum.Messaging.PostgreSql       ← Dapper-based persistence
```

---

## Repo Structure

```
momentum-messaging/
├── Momentum.Messaging.sln
├── Directory.Build.props
├── global.json
├── .editorconfig
├── src/
│   ├── Momentum.Messaging/
│   │   ├── Attributes.cs                    ← MomentumMediator, MomentumHandler, IgnoreHandler, suffixes, method names
│   │   ├── IMessageBus.cs                    ← IMessageBus, INotificationPublishStrategy, strategies
│   │   ├── IMessageContext.cs                ← IMessageContext (extends IMessageBus with ambient metadata)
│   │   ├── IPipelineBehavior.cs             ← IPipelineBehavior, NextDelegate
│   │   ├── Messages.cs                      ← IRequest<T>, IRequest, INotification, Unit
│   │   ├── MomentumBuilder.cs               ← AddMomentum(), MomentumBuilder
│   │   └── MomentumGeneratedHook.cs         ← Bridge for generated code
│   │
│   ├── Momentum.Messaging.Abstractions/
│   │   ├── IMessageConsumer.cs              ← IMessageConsumer, IMessageProcessor, IDeliveryContext
│   │   ├── IMessagePublisher.cs             ← IMessagePublisher, PublishOptions
│   │   ├── IMessageSerializer.cs            ← IMessageSerializer
│   │   ├── IMessageTypeRegistry.cs          ← IMessageTypeRegistry, DefaultMessageTypeRegistry
│   │   ├── MessageEnvelope.cs               ← MessageEnvelope, MessageEnvelope<T>, MessageHeaders
│   │   └── SubscriptionOptions.cs           ← SubscriptionOptions
│   │
│   ├── Momentum.Messaging.Generators/
│   │   ├── MomentumSourceGenerator.cs       ← Full IIncrementalGenerator implementation
│   │   ├── buildTransitive/
│   │   │   └── Momentum.Messaging.Generators.props  ← Auto-wires CompilerVisibleProperty
│   │   └── Momentum.Messaging.Generators.csproj
│   │
│   └── Momentum.Messaging.Outbox/
│       ├── IInboxStore.cs                   ← IInboxStore, InboxRecord, InboxStatus
│       ├── IOutboxStore.cs                  ← IOutboxStore, OutboxMessage, OutboxTransaction
│       ├── ITransactionalHandlerRegistry.cs ← IHandlerTransactionRegistry, TransactionMode
│       ├── InboxMessageProcessor.cs         ← Dedup decorator
│       ├── OutboxBehavior.cs                ← Pipeline behavior with tx management
│       ├── OutboxCollector.cs               ← IOutboxCollector, ambient/independent buckets
│       ├── OutboxProcessor.cs               ← BackgroundService for dispatching
│       ├── OutboxServiceCollectionExtensions.cs ← AddMomentumOutbox(), AddMomentumInbox()
│       ├── OutboxTransactionAccessor.cs     ← Nesting support, depth tracking
│       └── TransactionalAttribute.cs        ← [Transactional], [NonTransactional]
│
└── examples/
    └── Momentum.Messaging.Example/
        ├── Program.cs                       ← Message bus usage, conventions, multi-suffix
        └── OutboxExample.cs                 ← Full outbox flow with nested handlers
```

---

## Source Generator Details

The `MomentumSourceGenerator` (`IIncrementalGenerator`):

1. **Trigger**: Detects `[assembly: MomentumMediator]`
2. **Configuration**: Reads from assembly attributes first, `.csproj` properties as fallback
3. **Discovery**: Scans classes for suffix match + method name match, respects `[MomentumHandler]` and `[IgnoreHandler]`
4. **Emits**:
   - `MomentumMessageBus.g.cs` — `GeneratedMessageBus` with switch dispatch
   - `MomentumRegistration.g.cs` — DI registrations + `[ModuleInitializer]` hook
5. **Diagnostics**:
   - `MOM001` (Warning): No handlers found
   - `MOM002` (Error): Duplicate request handlers for same message type

---

## What's Been Built (Feature Sets 1 & 2)

### Feature Set 1: In-Memory Message Bus ✅

- `IMessageBus` as primary dispatch interface (`SendAsync`, `PublishAsync`)
- `IMessageContext` extends `IMessageBus` with ambient metadata (MessageId, CorrelationId, CausationId, Headers, etc.)
- Convention-based handler discovery
- Source generator with multi-suffix/multi-method support and scoped `IMessageContext` injection
- Pipeline behaviors (middleware)
- Notification publish strategies (sequential, parallel)
- Assembly attribute + .csproj configuration

### Feature Set 2: Transport Abstractions + Outbox/Inbox ✅

- `MessageEnvelope` wire format
- `IMessagePublisher` / `IMessageConsumer` / `IDeliveryContext` contracts
- `IMessageSerializer` (pluggable, no default)
- `IMessageTypeRegistry` for deserialization routing
- `OutboxBehavior<,>` with transaction ownership
- `OutboxProcessor` background service
- `InboxMessageProcessor` dedup decorator
- Nested handler transaction support
- Configurable global transaction mode (`TransactionalByDefault` / `NonTransactionalByDefault`)
- `[Transactional]` / `[NonTransactional]` per-handler overrides

---

## What's Next (Not Yet Built)

### Feature Set 3: Transport Plugins

- `Momentum.Messaging.Kafka` — implements `IMessagePublisher` + `IMessageConsumer`
- `Momentum.Messaging.RabbitMq`
- `Momentum.Messaging.EventHubs`

### Feature Set 4: Persistence Plugins

- `Momentum.Messaging.SqlServer` — Dapper-based `IOutboxStore` + `IInboxStore`
- `Momentum.Messaging.PostgreSql`
- SQL migration scripts for outbox/inbox tables

### Feature Set 5: Resilience & Observability

- Retry policies (Polly integration?)
- Circuit breakers
- Dead-letter queue handling
- OpenTelemetry integration
- Distributed tracing via correlation/causation IDs

### Feature Set 6: Serializer Plugins

- `Momentum.Messaging.Json` (System.Text.Json)
- `Momentum.Messaging.MessagePack`

---

## Open Design Questions

1. **Behaviors convention-based?** — Currently `IPipelineBehavior<,>` is the one place interfaces are used. Should behaviors also be convention-based (classes ending in `Behavior` with a wrapping method)?

2. **Multiple handlers per class** — Should a single handler class support multiple `HandleAsync` overloads for different message types, or strict one-message-per-class?

3. **Streaming** — Should we add `IStreamRequest<TResponse>` returning `IAsyncEnumerable<TResponse>`?

4. **Source generator for outbox** — The generator should also emit `IHandlerTransactionRegistry` based on `[Transactional]`/`[NonTransactional]` attributes on handler classes. This is designed but not yet implemented in the generator code.

5. **Connection management** — The `OutboxBehavior` currently injects `IDbConnection`. Should we introduce an `IConnectionFactory` to support connection pooling and multiple databases?

---

## GitHub Repo

https://github.com/vgmello/momentum-messaging

Code has been packaged for push — see the zip file exported from this session.
