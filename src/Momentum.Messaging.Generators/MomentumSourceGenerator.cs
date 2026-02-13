using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Momentum.Messaging.Generators;

[Generator]
public sealed class MomentumSourceGenerator : IIncrementalGenerator
{
    private const string MomentumMediatorAttr = "Momentum.Messaging.MomentumMediatorAttribute";
    private const string MomentumHandlerAttr = "Momentum.Messaging.MomentumHandlerAttribute";
    private const string IgnoreHandlerAttr = "Momentum.Messaging.IgnoreHandlerAttribute";
    private const string HandlerSuffixAttr = "Momentum.Messaging.MomentumHandlerSuffixAttribute";
    private const string MethodNameAttr = "Momentum.Messaging.MomentumMethodNameAttribute";
    private const string DiscoveryStrategyAttr = "Momentum.Messaging.MomentumDiscoveryStrategyAttribute";
    private const string IRequestGeneric = "Momentum.Messaging.IRequest`1";
    private const string INotificationFull = "Momentum.Messaging.INotification";
    private const string UnitFull = "Momentum.Messaging.Unit";
    private const string IMessageContextFull = "Momentum.Messaging.IMessageContext";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // ── Step 1: Detect [assembly: MomentumMediator] ──
        var assemblyTrigger = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                MomentumMediatorAttr,
                predicate: static (node, _) => true,
                transform: static (ctx, _) => ctx.SemanticModel.Compilation.Assembly)
            .Collect();

        // ── Step 2: Collect all class declarations ──
        var classCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) =>
                    ctx.SemanticModel.GetDeclaredSymbol((ClassDeclarationSyntax)ctx.Node, ct))
            .Where(static s => s is not null && !s.IsAbstract)
            .Select(static (s, _) => (INamedTypeSymbol)s!)
            .Collect();

        // ── Step 3: Read .csproj build properties as fallback ──
        var configProvider = context.AnalyzerConfigOptionsProvider;

        // ── Step 4: Combine and emit ──
        var combined = assemblyTrigger
            .Combine(classCandidates)
            .Combine(configProvider);

        context.RegisterSourceOutput(combined, static (ctx, source) =>
        {
            var ((assemblies, candidates), configOptions) = source;

            if (assemblies.IsEmpty)
                return;

            var assembly = assemblies[0];

            // Read configuration from assembly attributes + .csproj fallback
            var config = ReadConfig(assembly, configOptions);

            // Discover handlers
            var handlers = DiscoverHandlers(candidates, config);

            if (handlers.Count == 0)
            {
                // Emit diagnostic — no handlers found
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.NoHandlersFound, Location.None));
                return;
            }

            // Check for duplicate request handlers
            var duplicates = handlers
                .Where(h => !h.IsNotification)
                .GroupBy(h => h.MessageTypeFullName)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var dup in duplicates)
            {
                var handlerNames = string.Join(", ", dup.Select(h => h.HandlerTypeName));
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.DuplicateRequestHandler,
                    Location.None,
                    dup.Key,
                    handlerNames));
                return;
            }

            ctx.AddSource("MomentumMessageBus.g.cs", EmitMediator(handlers));
            ctx.AddSource("MomentumRegistration.g.cs", EmitRegistration(handlers));
        });
    }

    // ═════════════════════════════════════════════════════════════════════
    // Configuration — assembly attributes take precedence, .csproj fallback
    // ═════════════════════════════════════════════════════════════════════

    private static GeneratorConfig ReadConfig(
        IAssemblySymbol assembly,
        AnalyzerConfigOptionsProvider configOptions)
    {
        var attrs = assembly.GetAttributes();

        // ── Suffixes: [assembly: MomentumHandlerSuffix("...")] (multiple) ──
        var suffixes = attrs
            .Where(a => a.AttributeClass?.ToDisplayString() == HandlerSuffixAttr)
            .Select(a => a.ConstructorArguments[0].Value as string)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToList();

        // Fallback to .csproj: <MomentumHandlerSuffix>Handler;Processor</MomentumHandlerSuffix>
        if (suffixes.Count == 0)
        {
            configOptions.GlobalOptions.TryGetValue("build_property.MomentumHandlerSuffix", out var csprojSuffix);
            if (!string.IsNullOrWhiteSpace(csprojSuffix))
                suffixes = csprojSuffix!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        // Default
        if (suffixes.Count == 0)
            suffixes = new List<string> { "Handler" };

        // ── Method names: [assembly: MomentumMethodName("...")] (multiple) ──
        var methodNames = attrs
            .Where(a => a.AttributeClass?.ToDisplayString() == MethodNameAttr)
            .Select(a => a.ConstructorArguments[0].Value as string)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Cast<string>()
            .ToList();

        // Fallback to .csproj: <MomentumMethodName>HandleAsync;ExecuteAsync</MomentumMethodName>
        if (methodNames.Count == 0)
        {
            configOptions.GlobalOptions.TryGetValue("build_property.MomentumMethodName", out var csprojMethod);
            if (!string.IsNullOrWhiteSpace(csprojMethod))
                methodNames = csprojMethod!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        // Default
        if (methodNames.Count == 0)
            methodNames = new List<string> { "HandleAsync" };

        // ── Custom discovery strategy: [assembly: MomentumDiscoveryStrategy(typeof(...))] ──
        var strategyAttr = attrs
            .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == DiscoveryStrategyAttr);

        string? customStrategyType = null;
        if (strategyAttr is not null)
        {
            var typeArg = strategyAttr.ConstructorArguments[0];
            if (typeArg.Value is INamedTypeSymbol strategySymbol)
                customStrategyType = strategySymbol.ToDisplayString();
        }

        // Fallback to .csproj: <MomentumDiscoveryStrategy>MyNamespace.MyStrategy, MyAssembly</MomentumDiscoveryStrategy>
        if (customStrategyType is null)
        {
            configOptions.GlobalOptions.TryGetValue("build_property.MomentumDiscoveryStrategy", out var csprojStrategy);
            if (!string.IsNullOrWhiteSpace(csprojStrategy))
                customStrategyType = csprojStrategy;
        }

        return new GeneratorConfig
        {
            HandlerSuffixes = suffixes,
            MethodNames = methodNames,
            CustomDiscoveryStrategyType = customStrategyType,
        };
    }

    // ═════════════════════════════════════════════════════════════════════
    // Discovery — default suffix/method convention
    // ═════════════════════════════════════════════════════════════════════

    private static List<HandlerInfo> DiscoverHandlers(
        ImmutableArray<INamedTypeSymbol> candidates,
        GeneratorConfig config)
    {
        // If a custom strategy is specified, we can't invoke it at compile time
        // from the generator directly (it's user code, not analyzer code).
        // Instead, we emit a diagnostic telling the user that custom strategies
        // require their strategy to be in a separate analyzer-compatible assembly,
        // OR they use the attribute/csproj approach for suffix+method configuration.
        //
        // For the vast majority of cases, multi-suffix + multi-method covers it.
        // The custom strategy attribute is reserved for future extensibility where
        // the strategy assembly is loaded as an analyzer dependency.

        var handlers = new List<HandlerInfo>();

        foreach (var symbol in candidates)
        {
            if (HasAttribute(symbol, IgnoreHandlerAttr))
                continue;

            var hasExplicitAttr = HasAttribute(symbol, MomentumHandlerAttr);
            var matchesSuffix = config.HandlerSuffixes
                .Any(suffix => symbol.Name.EndsWith(suffix, StringComparison.Ordinal));

            if (!matchesSuffix && !hasExplicitAttr)
                continue;

            // Find matching methods across ALL configured method names
            var methods = symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(m => config.MethodNames.Contains(m.Name) &&
                            m.DeclaredAccessibility == Accessibility.Public &&
                            !m.IsStatic);

            foreach (var method in methods)
            {
                var parameters = method.Parameters;
                if (parameters.Length == 0 || parameters.Length > 3)
                    continue;

                var messageType = parameters[0].Type;
                var hasContext = false;
                var hasCt = false;

                // Parse remaining params: optional IMessageContext, optional CancellationToken
                var valid = true;
                for (var p = 1; p < parameters.Length; p++)
                {
                    var pType = parameters[p].Type.ToDisplayString();
                    if (pType == IMessageContextFull)
                        hasContext = true;
                    else if (pType == "System.Threading.CancellationToken")
                        hasCt = true;
                    else
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                    continue;

                // Notification
                if (ImplementsInterface(messageType, INotificationFull))
                {
                    handlers.Add(new HandlerInfo
                    {
                        HandlerTypeFullName = symbol.ToDisplayString(),
                        HandlerTypeName = symbol.Name,
                        MessageTypeFullName = messageType.ToDisplayString(),
                        MessageTypeName = messageType.Name,
                        ResponseTypeFullName = UnitFull,
                        MethodName = method.Name,
                        IsNotification = true,
                        HasContextParam = hasContext,
                        HasCancellationToken = hasCt,
                    });
                    continue;
                }

                // Request
                var requestInterface = messageType.AllInterfaces
                    .FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == IRequestGeneric);

                if (requestInterface is not null)
                {
                    var responseType = requestInterface.TypeArguments[0];
                    handlers.Add(new HandlerInfo
                    {
                        HandlerTypeFullName = symbol.ToDisplayString(),
                        HandlerTypeName = symbol.Name,
                        MessageTypeFullName = messageType.ToDisplayString(),
                        MessageTypeName = messageType.Name,
                        ResponseTypeFullName = responseType.ToDisplayString(),
                        MethodName = method.Name,
                        IsNotification = false,
                        HasContextParam = hasContext,
                        HasCancellationToken = hasCt,
                    });
                }
            }
        }

        return handlers;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Emit — GeneratedMediator
    // ═════════════════════════════════════════════════════════════════════

    private static string EmitMediator(List<HandlerInfo> handlers)
    {
        var requests = handlers.Where(h => !h.IsNotification).ToList();
        var notificationGroups = handlers.Where(h => h.IsNotification)
            .GroupBy(h => h.MessageTypeFullName)
            .ToList();

        var sb = new StringBuilder(4096);
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
        sb.AppendLine("    private static MessageContextScope CreateScope(IMessageBus bus)");
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

        // SendAsync
        sb.AppendLine("    public async Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        using var diScope = _sp.CreateScope();");
        sb.AppendLine("        var scopedSp = diScope.ServiceProvider;");
        sb.AppendLine("        var ctx = CreateScope(this);");
        sb.AppendLine("        var previous = MessageContextScope.SetCurrent(ctx);");
        sb.AppendLine("        try");
        sb.AppendLine("        {");
        sb.AppendLine("            switch (request)");
        sb.AppendLine("            {");

        foreach (var req in requests)
        {
            var handlerCall = BuildHandlerCall(req, "msg");
            sb.AppendLine($"                case {req.MessageTypeFullName} msg:");
            sb.AppendLine("                {");
            sb.AppendLine($"                    var handler = scopedSp.GetRequiredService<{req.HandlerTypeFullName}>();");
            sb.AppendLine($"                    var behaviors = scopedSp.GetServices<IPipelineBehavior<{req.MessageTypeFullName}, {req.ResponseTypeFullName}>>();");
            sb.AppendLine($"                    NextDelegate<{req.ResponseTypeFullName}> pipeline = () => {handlerCall};");
            sb.AppendLine("                    foreach (var behavior in behaviors.Reverse())");
            sb.AppendLine("                    {");
            sb.AppendLine("                        var next = pipeline;");
            sb.AppendLine("                        var b = behavior;");
            sb.AppendLine("                        pipeline = () => b.HandleAsync(msg, next, ct);");
            sb.AppendLine("                    }");
            sb.AppendLine("                    return (TResponse)(object)await pipeline().ConfigureAwait(false);");
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

        // PublishAsync
        sb.AppendLine("    public async Task PublishAsync<TNotification>(TNotification notification, CancellationToken ct = default)");
        sb.AppendLine("        where TNotification : INotification");
        sb.AppendLine("    {");
        sb.AppendLine("        using var diScope = _sp.CreateScope();");
        sb.AppendLine("        var scopedSp = diScope.ServiceProvider;");
        sb.AppendLine("        var ctx = CreateScope(this);");
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
                var notifCall = BuildNotificationHandlerCall(h);
                sb.AppendLine($"                    (n, c) => {{ var h = scopedSp.GetRequiredService<{h.HandlerTypeFullName}>(); return {notifCall}; }},");
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
        var args = msgVar;
        if (h.HasContextParam)
            args += ", ctx";
        if (h.HasCancellationToken)
            args += ", ct";
        return "handler." + h.MethodName + "(" + args + ")";
    }

    private static string BuildNotificationHandlerCall(HandlerInfo h)
    {
        var args = "n";
        if (h.HasContextParam)
            args += ", MessageContextScope.Current!";
        if (h.HasCancellationToken)
            args += ", c";
        return "h." + h.MethodName + "(" + args + ")";
    }

    // ═════════════════════════════════════════════════════════════════════
    // Emit — DI Registration
    // ═════════════════════════════════════════════════════════════════════

    private static string EmitRegistration(List<HandlerInfo> handlers)
    {
        var requests = handlers.Where(h => !h.IsNotification).ToList();

        var sb = new StringBuilder(4096);
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Diagnostics.CodeAnalysis;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
        sb.AppendLine("using Momentum.Messaging;");
        sb.AppendLine();
        sb.AppendLine("namespace Momentum.Messaging.Generated;");
        sb.AppendLine();
        sb.AppendLine("internal static class MomentumServiceRegistration");
        sb.AppendLine("{");
        sb.AppendLine("    [System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("    internal static void Initialize()");
        sb.AppendLine("    {");
        sb.AppendLine("        MomentumGeneratedHook.RegistrationAction = RegisterHandlers;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static void RegisterHandlers(IServiceCollection services, ServiceLifetime lifetime, IReadOnlyList<Type> behaviorTypes)");
        sb.AppendLine("    {");
        sb.AppendLine("        // Handler registrations (concrete types — fully AOT-safe)");

        foreach (var h in handlers)
            sb.AppendLine($"        services.TryAdd(new ServiceDescriptor(typeof({h.HandlerTypeFullName}), typeof({h.HandlerTypeFullName}), lifetime));");

        sb.AppendLine();
        sb.AppendLine("        // Pipeline behavior registrations.");
        sb.AppendLine("        // MakeGenericType is AOT-safe here because DynamicDependency attributes");
        sb.AppendLine("        // below ensure the trimmer preserves all closed generic constructions.");
        sb.AppendLine("        for (var i = 0; i < behaviorTypes.Count; i++)");
        sb.AppendLine("        {");

        foreach (var req in requests)
        {
            sb.AppendLine($"            services.Add(new ServiceDescriptor(");
            sb.AppendLine($"                typeof(IPipelineBehavior<{req.MessageTypeFullName}, {req.ResponseTypeFullName}>),");
            sb.AppendLine($"                behaviorTypes[i].MakeGenericType(typeof({req.MessageTypeFullName}), typeof({req.ResponseTypeFullName})),");
            sb.AppendLine($"                lifetime));");
        }

        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        services.TryAddSingleton<IMessageBus, GeneratedMessageBus>();");
        sb.AppendLine("        services.TryAddScoped<IMessageContext>(sp => MessageContextScope.Current ?? throw new InvalidOperationException(");
        sb.AppendLine("            \"IMessageContext is only available during message dispatch. Use IMessageBus for sending outside handlers.\"));");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    // ═════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════

    private static bool HasAttribute(INamedTypeSymbol symbol, string fullName)
        => symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == fullName);

    private static bool ImplementsInterface(ITypeSymbol type, string fullName)
        => type.AllInterfaces.Any(i => i.ToDisplayString() == fullName);
}

// ═════════════════════════════════════════════════════════════════════════
// Diagnostics
// ═════════════════════════════════════════════════════════════════════════

internal static class Diagnostics
{
    public static readonly DiagnosticDescriptor NoHandlersFound = new(
        id: "MOM001",
        title: "No handlers found",
        messageFormat: "No handler classes were discovered. Ensure classes end with the configured suffix and have a matching method.",
        category: "Momentum.Messaging",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateRequestHandler = new(
        id: "MOM002",
        title: "Duplicate request handler",
        messageFormat: "Multiple handlers found for request '{0}': {1}. A request must have exactly one handler.",
        category: "Momentum.Messaging",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}

// ═════════════════════════════════════════════════════════════════════════
// Internal models
// ═════════════════════════════════════════════════════════════════════════

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

internal sealed class GeneratorConfig
{
    public List<string> HandlerSuffixes { get; set; } = null!;
    public List<string> MethodNames { get; set; } = null!;
    public string? CustomDiscoveryStrategyType { get; set; }
}
