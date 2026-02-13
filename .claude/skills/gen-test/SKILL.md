---
name: gen-test
description: Generate xUnit test classes for Momentum.Messaging components
disable-model-invocation: true
---

# Generate Tests

Generate xUnit test classes for Momentum.Messaging components. Creates well-structured tests following project conventions.

## Arguments

- `$ARGUMENTS` — The component or area to test (e.g., "OutboxBehavior", "source generator", "MomentumBuilder")

## Instructions

1. Identify the component to test from `$ARGUMENTS`
2. Read the source file(s) for that component
3. Determine the correct test project:
   - `tests/Momentum.Messaging.Tests/` for core mediator tests
   - `tests/Momentum.Messaging.Outbox.Tests/` for outbox/inbox tests
   - `tests/Momentum.Messaging.Generators.Tests/` for source generator tests (use `Microsoft.CodeAnalysis.CSharp.Testing` and `CSharpIncrementalGeneratorVerifier`)
4. If the test project doesn't exist yet, create it:
   - Use `dotnet new xunit` as a base
   - Add project reference to the component under test
   - Add to `Momentum.Messaging.sln`
   - Reference packages: `xunit`, `NSubstitute`, `FluentAssertions`, `Microsoft.NET.Test.Sdk`
   - For generator tests, also add `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` and `Microsoft.CodeAnalysis.CSharp.Workspaces`
5. Generate test class with:
   - One test method per public method/behavior
   - Arrange/Act/Assert pattern
   - NSubstitute for mocking interfaces
   - FluentAssertions for assertions
   - `CancellationToken.None` passed explicitly
   - Async test methods (`Task`-returning, not `void`)
   - Descriptive method names: `MethodName_Scenario_ExpectedResult`
6. Run `dotnet test` to verify tests compile and pass
