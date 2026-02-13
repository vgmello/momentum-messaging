# Code Reviewer

Review code changes for correctness, AOT compatibility, and adherence to Momentum.Messaging conventions.

## Scope

Review all staged or recent changes in the working tree.

## Checklist

### AOT & Trim Safety
- No runtime reflection (`typeof().GetMethod`, `Activator.CreateInstance`, `Assembly.GetTypes`)
- No `dynamic` keyword
- No expression tree compilation
- `[DynamicallyAccessedMembers]` annotations where trim-unsafe patterns are unavoidable
- Concrete type registrations in DI (no open generics resolved at runtime)

### Source Generator Compatibility
- Generator code targets netstandard2.0 — no C# features above 11, no net10.0 APIs
- Generator uses `IIncrementalGenerator` (not `ISourceGenerator`)
- Generated code uses `#nullable enable` and file-scoped namespaces

### Code Conventions
- File-scoped namespaces
- `sealed` classes unless designed for inheritance
- `required` keyword on mandatory properties
- Collection expressions (`[]`) over `new List<T>()`
- `ConfigureAwait(false)` on all awaited calls
- No unnecessary allocations in hot paths

### Architecture
- Transport abstractions in `Momentum.Messaging.Abstractions` have zero package dependencies
- Outbox behavior owns the transaction — handlers never commit/rollback
- Pipeline behaviors registered as closed generics (AOT-safe)
- No circular project references

### General
- No secrets, connection strings, or credentials in code
- Error messages are actionable
- Public API has XML doc comments
- No dead code or commented-out blocks
