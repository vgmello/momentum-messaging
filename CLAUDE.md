# Momentum.Messaging

Lightweight, high-performance messaging framework for .NET. Zero runtime reflection via Roslyn source generators. Fully Native AOT compatible.

## Build & Test

```bash
dotnet build Momentum.Messaging.sln
dotnet test   # when test projects exist
```

## Architecture

Four projects in `src/`:

| Project                           | Target             | Purpose                                                |
| --------------------------------- | ------------------ | ------------------------------------------------------ |
| `Momentum.Messaging`              | net10.0            | Core message bus, IMessageContext, pipeline behaviors, builder, attributes |
| `Momentum.Messaging.Abstractions` | net10.0            | Transport & serialization contracts (zero deps)        |
| `Momentum.Messaging.Generators`   | **netstandard2.0** | Roslyn source generator (compile-time only)            |
| `Momentum.Messaging.Outbox`       | net10.0            | Transactional outbox/inbox pattern                     |

### Key constraint: Generators target netstandard2.0

`Momentum.Messaging.Generators` is a Roslyn `IIncrementalGenerator`. It **must** target `netstandard2.0` (required by the compiler). Do not change its target framework. All other projects target `net10.0`.

## Design Principles

- **Convention-based handlers**: Classes ending in `Handler` (configurable suffix) with `HandleAsync` methods. No `IRequestHandler<,>` interfaces.
- **`IMessageBus` dispatch**: The primary dispatch interface. Supports `SendAsync` (request/response) and `PublishAsync` (notifications).
- **`IMessageContext` ambient metadata**: Extends `IMessageBus` with ambient message metadata (`MessageId`, `CorrelationId`, `CausationId`, `Source`, `PartitionKey`, `Headers`, `Timestamp`, `Envelope`). Handlers can accept `IMessageContext` as a parameter to access scoped context during dispatch.
- **`IDeliveryContext` for transport**: Transport-level acknowledgment/rejection contract (formerly `IMessageContext` in Abstractions). Renamed to avoid collision with the new `IMessageContext`.
- **Source generation only**: No reflection fallback. If the generator hasn't run, `AddMomentum()` throws at startup.
- **AOT-first**: `IsAotCompatible=true`, `EnableTrimAnalyzer=true`, `TreatWarningsAsErrors=true`. Never introduce reflection, `dynamic`, expression compilation, or `Assembly.GetTypes()`.
- **Outbox as pipeline behavior**: `OutboxBehavior<,>` wraps handlers. The behavior owns the DB transaction -- handlers never commit/rollback.
- **Pluggable serialization**: `IMessageSerializer` has no default. Serializer packages are separate.

## Code Conventions

- File-scoped namespaces (no braces)
- Nullable reference types enabled globally
- `required` keyword for mandatory properties
- Collection expressions (`[]`) over `new List<T>()`
- `ConfigureAwait(false)` on all awaited calls
- Concrete DI registrations (no open generics at runtime)
- `sealed` on classes unless designed for inheritance

## Source Generator Notes

- Generator lives in `Momentum.Messaging.Generators/MomentumSourceGenerator.cs`
- Emits two files: `MomentumMessageBus.g.cs` (switch-based dispatch via `GeneratedMessageBus`) and `MomentumRegistration.g.cs` (DI + ModuleInitializer)
- Diagnostics: `MOM001` (no handlers found), `MOM002` (duplicate request handler)
- Discovery configuration: assembly attributes take precedence over `.csproj` properties

## Package Structure (Future)

```
Momentum.Messaging.Json             -- System.Text.Json serializer
Momentum.Messaging.MessagePack      -- MessagePack serializer
Momentum.Messaging.Kafka            -- Transport plugin
Momentum.Messaging.RabbitMq         -- Transport plugin
Momentum.Messaging.EventHubs        -- Transport plugin
Momentum.Messaging.SqlServer        -- Dapper-based outbox/inbox persistence
Momentum.Messaging.PostgreSql       -- Dapper-based outbox/inbox persistence
```
