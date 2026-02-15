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
    private const string ScopedHandlerAttr = "Momentum.Messaging.ScopedHandlerAttribute";
    private const string SingletonHandlerAttr = "Momentum.Messaging.SingletonHandlerAttribute";
    private const string TransientHandlerAttr = "Momentum.Messaging.TransientHandlerAttribute";
    private const string IRequestGeneric = "Momentum.Messaging.IRequest<TResponse>";
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
            .Where(static s => s is not null && (!s.IsAbstract || s.IsStatic))
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
            var handlers = DiscoverHandlers(candidates, config, out var multiCtorHandlers);

            // Report multiple-constructor errors
            foreach (var handlerName in multiCtorHandlers)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.MultipleConstructors, Location.None, handlerName));
            }
            if (multiCtorHandlers.Count > 0)
                return;

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
            }
            if (duplicates.Count > 0)
                return;

            ctx.AddSource("MomentumMessageBus.g.cs", EmitMessageBus(handlers));
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
        GeneratorConfig config,
        out List<string> multiCtorHandlers)
    {
        var handlers = new List<HandlerInfo>();
        multiCtorHandlers = new List<string>();

        foreach (var symbol in candidates)
        {
            if (HasAttribute(symbol, IgnoreHandlerAttr))
                continue;

            var hasExplicitAttr = HasAttribute(symbol, MomentumHandlerAttr);
            var matchesSuffix = config.HandlerSuffixes
                .Any(suffix => symbol.Name.EndsWith(suffix, StringComparison.Ordinal));

            if (!matchesSuffix && !hasExplicitAttr)
                continue;

            // Find matching methods across ALL configured method names (static and instance)
            var methods = symbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(m => config.MethodNames.Contains(m.Name) &&
                            m.DeclaredAccessibility == Accessibility.Public);

            foreach (var method in methods)
            {
                var parameters = method.Parameters;
                if (parameters.Length == 0)
                    continue;

                var messageType = parameters[0].Type;
                var hasContext = false;
                var hasCt = false;
                var methodServices = new List<ServiceParam>();

                // Parse remaining params: IMessageContext, CancellationToken, and unknown types as method services
                // Order enforced: message, [services...], [IMessageContext], [CancellationToken]
                var valid = true;
                for (var p = 1; p < parameters.Length; p++)
                {
                    var pType = parameters[p].Type.ToDisplayString();
                    if (pType == IMessageContextFull && !hasCt)
                        hasContext = true;
                    else if (pType == "System.Threading.CancellationToken")
                        hasCt = true;
                    else if (!hasContext && !hasCt)
                    {
                        // Unknown type before context/ct — treat as method-injected service
                        methodServices.Add(new ServiceParam
                        {
                            TypeFullName = pType,
                            ParamName = parameters[p].Name,
                        });
                    }
                    else
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                    continue;

                // Determine if this is a static handler
                var isStatic = method.IsStatic && symbol.IsStatic;

                // Determine lifetime from attributes
                HandlerLifetime lifetime;
                if (HasAttribute(symbol, SingletonHandlerAttr))
                    lifetime = HandlerLifetime.Singleton;
                else if (HasAttribute(symbol, TransientHandlerAttr))
                    lifetime = HandlerLifetime.Transient;
                else if (HasAttribute(symbol, ScopedHandlerAttr))
                    lifetime = HandlerLifetime.Scoped;
                else
                    lifetime = isStatic ? HandlerLifetime.Transient : HandlerLifetime.Scoped;

                // Collect constructor services for instance handlers
                var ctorServices = new List<ServiceParam>();
                if (!isStatic)
                {
                    var ctors = symbol.InstanceConstructors
                        .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsImplicitlyDeclared)
                        .ToList();

                    if (ctors.Count > 1)
                    {
                        multiCtorHandlers.Add(symbol.ToDisplayString());
                        continue;
                    }

                    if (ctors.Count == 1)
                    {
                        foreach (var cp in ctors[0].Parameters)
                        {
                            ctorServices.Add(new ServiceParam
                            {
                                TypeFullName = cp.Type.ToDisplayString(),
                                ParamName = cp.Name,
                            });
                        }
                    }
                }

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
                        ReturnsVoidTask = true,
                        IsStatic = isStatic,
                        Lifetime = lifetime,
                        CtorServices = ctorServices,
                        MethodServices = methodServices,
                    });
                    continue;
                }

                // Request
                var requestInterface = messageType.AllInterfaces
                    .FirstOrDefault(i => i.OriginalDefinition.ToDisplayString() == IRequestGeneric);

                if (requestInterface is not null)
                {
                    var responseType = requestInterface.TypeArguments[0];
                    // Detect if the handler method returns Task (void) vs Task<T>
                    var returnsVoidTask = method.ReturnType.ToDisplayString() == "System.Threading.Tasks.Task";
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
                        ReturnsVoidTask = returnsVoidTask,
                        IsStatic = isStatic,
                        Lifetime = lifetime,
                        CtorServices = ctorServices,
                        MethodServices = methodServices,
                    });
                }
            }
        }

        return handlers;
    }

    // ═════════════════════════════════════════════════════════════════════
    // Emit — GeneratedMessageBus
    // ═════════════════════════════════════════════════════════════════════

    private static string SanitizeTypeName(string fullName)
    {
        // Convert "Namespace.TypeName" -> "Namespace_TypeName" for use as method/field names
        return fullName.Replace('.', '_').Replace('<', '_').Replace('>', '_').Replace(',', '_').Replace(' ', '_');
    }

    private static string EmitMessageBus(List<HandlerInfo> handlers)
    {
        var requests = handlers.Where(h => !h.IsNotification).ToList();
        var notificationGroups = handlers.Where(h => h.IsNotification)
            .GroupBy(h => h.MessageTypeFullName)
            .ToList();

        // Collect singleton handlers that need fields
        var singletonHandlers = handlers
            .Where(h => h.Lifetime == HandlerLifetime.Singleton && !h.IsStatic)
            .GroupBy(h => h.HandlerTypeFullName)
            .ToList();

        var sb = new StringBuilder(8192);
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Momentum.Messaging;");
        sb.AppendLine();
        sb.AppendLine("namespace Momentum.Messaging.Generated;");
        sb.AppendLine();
        sb.AppendLine("internal sealed class GeneratedMessageBus : IMessageBus");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly IServiceProvider _sp;");
        sb.AppendLine("    private readonly INotificationPublishStrategy _publishStrategy;");
        sb.AppendLine("    private readonly bool _hasBehaviors;");

        // Emit singleton handler fields
        foreach (var group in singletonHandlers)
        {
            var fieldName = "_singleton_" + SanitizeTypeName(group.Key);
            sb.AppendLine($"    private {group.Key}? {fieldName};");
        }

        sb.AppendLine();
        sb.AppendLine("    public GeneratedMessageBus(IServiceProvider sp, INotificationPublishStrategy publishStrategy, bool hasBehaviors)");
        sb.AppendLine("    {");
        sb.AppendLine("        _sp = sp;");
        sb.AppendLine("        _publishStrategy = publishStrategy;");
        sb.AppendLine("        _hasBehaviors = hasBehaviors;");
        sb.AppendLine("    }");
        sb.AppendLine();

        // ── Async helper methods for cleanup on truly-async paths ──
        sb.AppendLine("    private static async Task<T> AwaitHandler<T>(Task<T> task, IServiceScope? scope, MessageContextScope? previousCtx, bool restoreCtx)");
        sb.AppendLine("    {");
        sb.AppendLine("        try { return await task.ConfigureAwait(false); }");
        sb.AppendLine("        finally");
        sb.AppendLine("        {");
        sb.AppendLine("            if (restoreCtx) MessageContextScope.RestoreCurrent(previousCtx);");
        sb.AppendLine("            scope?.Dispose();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private static async Task<Momentum.Messaging.Unit> AwaitVoidHandler(Task task, IServiceScope? scope, MessageContextScope? previousCtx, bool restoreCtx)");
        sb.AppendLine("    {");
        sb.AppendLine("        try { await task.ConfigureAwait(false); return Momentum.Messaging.Unit.Value; }");
        sb.AppendLine("        finally");
        sb.AppendLine("        {");
        sb.AppendLine("            if (restoreCtx) MessageContextScope.RestoreCurrent(previousCtx);");
        sb.AppendLine("            scope?.Dispose();");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        // ── Emit private handler processing methods for each request handler ──
        foreach (var req in requests)
        {
            var processName = "Process_" + SanitizeTypeName(req.MessageTypeFullName);
            EmitProcessMethod(sb, req, processName);
            EmitProcessWithPipelineMethod(sb, req, processName);
        }

        // ── SendAsync ── (pure routing — branches on _hasBehaviors at routing level)
        sb.AppendLine("    public Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken ct = default)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (request)");
        sb.AppendLine("        {");

        foreach (var req in requests)
        {
            var processName = "Process_" + SanitizeTypeName(req.MessageTypeFullName);
            sb.AppendLine($"            case {req.MessageTypeFullName} msg:");
            sb.AppendLine("            {");
            sb.AppendLine($"                var task = _hasBehaviors ? {processName}_WithPipeline(msg, ct) : {processName}(msg, ct);");
            sb.AppendLine($"                return Unsafe.As<Task<{req.ResponseTypeFullName}>, Task<TResponse>>(ref task);");
            sb.AppendLine("            }");
        }

        sb.AppendLine("            default:");
        sb.AppendLine("                throw new InvalidOperationException(");
        sb.AppendLine("                    $\"No handler found for {request.GetType().Name}. Ensure a handler follows Momentum conventions.\");");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        // ── PublishAsync ──
        sb.AppendLine("    public async Task PublishAsync<TNotification>(TNotification notification, CancellationToken ct = default)");
        sb.AppendLine("        where TNotification : INotification");
        sb.AppendLine("    {");

        foreach (var group in notificationGroups)
        {
            var anyNeedsContext = group.Any(h => h.HasContextParam);

            sb.AppendLine($"        if (notification is {group.Key} typedNotification)");
            sb.AppendLine("        {");

            if (anyNeedsContext)
            {
                sb.AppendLine("            var ctx = new MessageContextScope(this, MessageContextScope.Current);");
                sb.AppendLine("            var previous = MessageContextScope.SetCurrent(ctx);");
                sb.AppendLine("            try");
                sb.AppendLine("            {");
            }

            var indent = anyNeedsContext ? "                " : "            ";
            sb.AppendLine($"{indent}var handlers = new Func<{group.Key}, CancellationToken, Task>[]");
            sb.AppendLine($"{indent}{{");
            foreach (var h in group)
            {
                EmitNotificationHandlerLambda(sb, h, indent + "    ");
            }
            sb.AppendLine($"{indent}}};");
            sb.AppendLine($"{indent}await _publishStrategy.PublishAsync(");
            sb.AppendLine($"{indent}    (IReadOnlyList<Func<TNotification, CancellationToken, Task>>)(object)handlers,");
            sb.AppendLine($"{indent}    notification, ct).ConfigureAwait(false);");

            if (anyNeedsContext)
            {
                sb.AppendLine("            }");
                sb.AppendLine("            finally { MessageContextScope.RestoreCurrent(previous); }");
            }

            sb.AppendLine("            return;");
            sb.AppendLine("        }");
        }

        sb.AppendLine("    }");

        // ── Emit private typed dispatch methods for each notification group ──
        // (not needed — notification handlers are emitted inline in lambdas)

        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Emits a non-async handler processing method with synchronous fast path.
    /// Self-contained: owns its own DI scope, context, and cleanup.
    /// Called directly from SendAsync when no behaviors are registered.
    /// </summary>
    private static void EmitProcessMethod(StringBuilder sb, HandlerInfo req, string processName)
    {
        sb.AppendLine($"    private Task<{req.ResponseTypeFullName}> {processName}({req.MessageTypeFullName} msg, CancellationToken ct)");
        sb.AppendLine("    {");

        var needsScope = req.Lifetime == HandlerLifetime.Scoped;
        var staticWithScopedServices = req.IsStatic && req.MethodServices.Count > 0 && req.Lifetime == HandlerLifetime.Scoped;
        if (staticWithScopedServices)
            needsScope = true;

        var needsCleanup = needsScope || req.HasContextParam;
        var indent = "        ";
        var bodyIndent = needsCleanup ? "            " : "        ";
        var spRef = needsScope ? "sp" : "_sp";
        var restoreCtxArg = req.HasContextParam ? "true" : "false";

        // Resource acquisition (safe operations — before try)
        if (needsScope)
        {
            sb.AppendLine($"{indent}var scope = _sp.CreateScope();");
            sb.AppendLine($"{indent}var sp = scope.ServiceProvider;");
        }

        if (req.HasContextParam)
        {
            sb.AppendLine($"{indent}var ctx = new MessageContextScope(this, MessageContextScope.Current);");
            sb.AppendLine($"{indent}var previous = MessageContextScope.SetCurrent(ctx);");
        }

        // try block wraps everything that can throw (service resolution, handler construction, invocation)
        if (needsCleanup)
        {
            sb.AppendLine($"{indent}try");
            sb.AppendLine($"{indent}{{");
        }

        // Resolve method services (can throw)
        foreach (var svc in req.MethodServices)
        {
            sb.AppendLine($"{bodyIndent}var {svc.ParamName} = {spRef}.GetRequiredService<{svc.TypeFullName}>();");
        }

        // Build handler + call expression (construction can throw)
        string handlerCallExpr;
        if (req.IsStatic)
        {
            handlerCallExpr = BuildStaticHandlerCall(req, "msg");
        }
        else
        {
            EmitHandlerConstruction(sb, req, bodyIndent, spRef);
            handlerCallExpr = BuildInstanceHandlerCall(req, "msg");
        }

        // Handler invocation + sync fast path
        if (req.ReturnsVoidTask)
        {
            sb.AppendLine($"{bodyIndent}var __handlerTask = {handlerCallExpr};");
            sb.AppendLine($"{bodyIndent}if (__handlerTask.IsCompletedSuccessfully)");
            sb.AppendLine($"{bodyIndent}{{");
            if (req.HasContextParam)
                sb.AppendLine($"{bodyIndent}    MessageContextScope.RestoreCurrent(previous);");
            if (needsScope)
                sb.AppendLine($"{bodyIndent}    scope.Dispose();");
            sb.AppendLine($"{bodyIndent}    return Task.FromResult({UnitFull}.Value);");
            sb.AppendLine($"{bodyIndent}}}");
            sb.AppendLine($"{bodyIndent}return AwaitVoidHandler(__handlerTask, {(needsScope ? "scope" : "null")}, {(req.HasContextParam ? "previous" : "null")}, {restoreCtxArg});");
        }
        else
        {
            sb.AppendLine($"{bodyIndent}var __handlerTask = {handlerCallExpr};");
            sb.AppendLine($"{bodyIndent}if (__handlerTask.IsCompletedSuccessfully)");
            sb.AppendLine($"{bodyIndent}{{");
            if (req.HasContextParam)
                sb.AppendLine($"{bodyIndent}    MessageContextScope.RestoreCurrent(previous);");
            if (needsScope)
                sb.AppendLine($"{bodyIndent}    scope.Dispose();");
            sb.AppendLine($"{bodyIndent}    return __handlerTask;");
            sb.AppendLine($"{bodyIndent}}}");
            sb.AppendLine($"{bodyIndent}return AwaitHandler(__handlerTask, {(needsScope ? "scope" : "null")}, {(req.HasContextParam ? "previous" : "null")}, {restoreCtxArg});");
        }

        // Catch block — clean up on synchronous exceptions
        if (needsCleanup)
        {
            sb.AppendLine($"{indent}}}");
            sb.AppendLine($"{indent}catch");
            sb.AppendLine($"{indent}{{");
            if (req.HasContextParam)
                sb.AppendLine($"{indent}    MessageContextScope.RestoreCurrent(previous);");
            if (needsScope)
                sb.AppendLine($"{indent}    scope.Dispose();");
            sb.AppendLine($"{indent}    throw;");
            sb.AppendLine($"{indent}}}");
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitHandlerConstruction(StringBuilder sb, HandlerInfo req, string indent, string spRef)
    {
        var fieldName = "_singleton_" + SanitizeTypeName(req.HandlerTypeFullName);

        switch (req.Lifetime)
        {
            case HandlerLifetime.Singleton:
                // Cached in field, thread-safe lazy init
                if (req.CtorServices.Count == 0)
                {
                    sb.AppendLine($"{indent}var handler = LazyInitializer.EnsureInitialized(ref {fieldName}, () => new {req.HandlerTypeFullName}())!;");
                }
                else
                {
                    var ctorArgs = string.Join(", ", req.CtorServices.Select(s => $"_sp.GetRequiredService<{s.TypeFullName}>()"));
                    sb.AppendLine($"{indent}var handler = LazyInitializer.EnsureInitialized(ref {fieldName}, () => new {req.HandlerTypeFullName}({ctorArgs}))!;");
                }
                break;

            case HandlerLifetime.Transient:
                // New each time, no scope
                if (req.CtorServices.Count == 0)
                {
                    sb.AppendLine($"{indent}var handler = new {req.HandlerTypeFullName}();");
                }
                else
                {
                    var ctorArgs = string.Join(", ", req.CtorServices.Select(s => $"_sp.GetRequiredService<{s.TypeFullName}>()"));
                    sb.AppendLine($"{indent}var handler = new {req.HandlerTypeFullName}({ctorArgs});");
                }
                break;

            case HandlerLifetime.Scoped:
                // New each time, resolve from scope
                if (req.CtorServices.Count == 0)
                {
                    sb.AppendLine($"{indent}var handler = new {req.HandlerTypeFullName}();");
                }
                else
                {
                    var ctorArgs = string.Join(", ", req.CtorServices.Select(s => $"{spRef}.GetRequiredService<{s.TypeFullName}>()"));
                    sb.AppendLine($"{indent}var handler = new {req.HandlerTypeFullName}({ctorArgs});");
                }
                break;
        }
    }

    private static string BuildInstanceHandlerCall(HandlerInfo h, string msgVar)
    {
        var args = msgVar;
        foreach (var svc in h.MethodServices)
            args += ", " + svc.ParamName;
        if (h.HasContextParam)
            args += ", ctx";
        if (h.HasCancellationToken)
            args += ", ct";
        return "handler." + h.MethodName + "(" + args + ")";
    }

    private static string BuildStaticHandlerCall(HandlerInfo h, string msgVar)
    {
        var args = msgVar;
        foreach (var svc in h.MethodServices)
            args += ", " + svc.ParamName;
        if (h.HasContextParam)
            args += ", ctx";
        if (h.HasCancellationToken)
            args += ", ct";
        return h.HandlerTypeFullName + "." + h.MethodName + "(" + args + ")";
    }

    /// <summary>
    /// Emits an async handler processing method with the behavior pipeline.
    /// Self-contained: owns its own DI scope, context, and cleanup.
    /// Called from SendAsync when behaviors are registered.
    /// </summary>
    private static void EmitProcessWithPipelineMethod(StringBuilder sb, HandlerInfo req, string processName)
    {
        sb.AppendLine($"    private async Task<{req.ResponseTypeFullName}> {processName}_WithPipeline({req.MessageTypeFullName} msg, CancellationToken ct)");
        sb.AppendLine("    {");

        var indent = "        ";
        var needsScope = req.Lifetime == HandlerLifetime.Scoped ||
                         (req.IsStatic && req.MethodServices.Count > 0 && req.Lifetime == HandlerLifetime.Scoped);
        var spRef = needsScope ? "sp" : "_sp";

        // Self-contained scope creation (processing side)
        if (needsScope)
        {
            sb.AppendLine($"{indent}var scope = _sp.CreateScope();");
            sb.AppendLine($"{indent}var sp = scope.ServiceProvider;");
        }

        // Self-contained context creation
        if (req.HasContextParam)
        {
            sb.AppendLine($"{indent}var ctx = new MessageContextScope(this, MessageContextScope.Current);");
            sb.AppendLine($"{indent}var previous = MessageContextScope.SetCurrent(ctx);");
        }

        sb.AppendLine($"{indent}try");
        sb.AppendLine($"{indent}{{");

        var innerIndent = indent + "    ";

        // Resolve method services
        foreach (var svc in req.MethodServices)
        {
            sb.AppendLine($"{innerIndent}var {svc.ParamName} = {spRef}.GetRequiredService<{svc.TypeFullName}>();");
        }

        // Build handler + call
        string handlerCallExpr;
        if (req.IsStatic)
        {
            handlerCallExpr = BuildStaticHandlerCall(req, "msg");
        }
        else
        {
            EmitHandlerConstruction(sb, req, innerIndent, spRef);
            handlerCallExpr = BuildInstanceHandlerCall(req, "msg");
        }

        sb.AppendLine($"{innerIndent}var behaviors = {spRef}.GetServices<IPipelineBehavior<{req.MessageTypeFullName}, {req.ResponseTypeFullName}>>();");
        if (req.ReturnsVoidTask)
            sb.AppendLine($"{innerIndent}NextDelegate<{req.ResponseTypeFullName}> pipeline = async () => {{ await {handlerCallExpr}.ConfigureAwait(false); return {UnitFull}.Value; }};");
        else
            sb.AppendLine($"{innerIndent}NextDelegate<{req.ResponseTypeFullName}> pipeline = () => {handlerCallExpr};");
        sb.AppendLine($"{innerIndent}foreach (var behavior in behaviors.Reverse())");
        sb.AppendLine($"{innerIndent}{{");
        sb.AppendLine($"{innerIndent}    var next = pipeline;");
        sb.AppendLine($"{innerIndent}    var b = behavior;");
        sb.AppendLine($"{innerIndent}    pipeline = () => b.HandleAsync(msg, next, ct);");
        sb.AppendLine($"{innerIndent}}}");
        sb.AppendLine($"{innerIndent}return await pipeline().ConfigureAwait(false);");

        sb.AppendLine($"{indent}}}");
        sb.AppendLine($"{indent}finally");
        sb.AppendLine($"{indent}{{");
        if (req.HasContextParam)
            sb.AppendLine($"{indent}    MessageContextScope.RestoreCurrent(previous);");
        if (needsScope)
            sb.AppendLine($"{indent}    scope.Dispose();");
        sb.AppendLine($"{indent}}}");

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitNotificationHandlerLambda(StringBuilder sb, HandlerInfo h, string indent)
    {
        // Each notification handler lambda creates its own handler instance per-call
        // and resolves its own scope if needed
        var needsScope = h.Lifetime == HandlerLifetime.Scoped;
        var isStaticWithScopedServices = h.IsStatic && h.MethodServices.Count > 0 && h.Lifetime == HandlerLifetime.Scoped;
        if (isStaticWithScopedServices)
            needsScope = true;

        if (h.IsStatic && h.MethodServices.Count == 0 && !needsScope)
        {
            // Simple static call — no handler construction needed
            var call = BuildNotificationStaticCall(h);
            sb.AppendLine($"{indent}(n, c) => {call},");
        }
        else if (needsScope)
        {
            // Need a scope: use async lambda
            sb.AppendLine($"{indent}async (n, c) => {{");
            sb.AppendLine($"{indent}    using var scope = _sp.CreateScope();");
            sb.AppendLine($"{indent}    var sp = scope.ServiceProvider;");

            // Resolve method services
            foreach (var svc in h.MethodServices)
            {
                sb.AppendLine($"{indent}    var {svc.ParamName} = sp.GetRequiredService<{svc.TypeFullName}>();");
            }

            if (h.IsStatic)
            {
                var call = BuildNotificationStaticCallInLambda(h);
                sb.AppendLine($"{indent}    await {call}.ConfigureAwait(false);");
            }
            else
            {
                var ctorArgs = BuildCtorArgs(h, "sp");
                sb.AppendLine($"{indent}    var h = new {h.HandlerTypeFullName}({ctorArgs});");
                var call = BuildNotificationInstanceCallInLambda(h);
                sb.AppendLine($"{indent}    await {call}.ConfigureAwait(false);");
            }

            sb.AppendLine($"{indent}}},");
        }
        else
        {
            // Transient or singleton instance handler (no scope needed)
            if (h.Lifetime == HandlerLifetime.Singleton)
            {
                var fieldName = "_singleton_" + SanitizeTypeName(h.HandlerTypeFullName);
                // Resolve method services from root
                if (h.MethodServices.Count > 0)
                {
                    sb.AppendLine($"{indent}async (n, c) => {{");
                    foreach (var svc in h.MethodServices)
                    {
                        sb.AppendLine($"{indent}    var {svc.ParamName} = _sp.GetRequiredService<{svc.TypeFullName}>();");
                    }
                    var ctorArgs = BuildCtorArgs(h, "_sp");
                    sb.AppendLine($"{indent}    var h = LazyInitializer.EnsureInitialized(ref {fieldName}, () => new {h.HandlerTypeFullName}({ctorArgs}))!;");
                    var call = BuildNotificationInstanceCallInLambda(h);
                    sb.AppendLine($"{indent}    await {call}.ConfigureAwait(false);");
                    sb.AppendLine($"{indent}}},");
                }
                else
                {
                    var ctorArgs = BuildCtorArgs(h, "_sp");
                    sb.AppendLine($"{indent}(n, c) => {{");
                    sb.AppendLine($"{indent}    var h = LazyInitializer.EnsureInitialized(ref {fieldName}, () => new {h.HandlerTypeFullName}({ctorArgs}))!;");
                    var call = BuildNotificationInstanceCallInLambda(h);
                    sb.AppendLine($"{indent}    return {call};");
                    sb.AppendLine($"{indent}}},");
                }
            }
            else
            {
                // Transient instance handler
                if (h.MethodServices.Count > 0)
                {
                    sb.AppendLine($"{indent}async (n, c) => {{");
                    foreach (var svc in h.MethodServices)
                    {
                        sb.AppendLine($"{indent}    var {svc.ParamName} = _sp.GetRequiredService<{svc.TypeFullName}>();");
                    }
                    var ctorArgs = BuildCtorArgs(h, "_sp");
                    sb.AppendLine($"{indent}    var h = new {h.HandlerTypeFullName}({ctorArgs});");
                    var call = BuildNotificationInstanceCallInLambda(h);
                    sb.AppendLine($"{indent}    await {call}.ConfigureAwait(false);");
                    sb.AppendLine($"{indent}}},");
                }
                else
                {
                    var ctorArgs = BuildCtorArgs(h, "_sp");
                    sb.AppendLine($"{indent}(n, c) => {{ var h = new {h.HandlerTypeFullName}({ctorArgs}); return {BuildNotificationInstanceCallInLambda(h)}; }},");
                }
            }
        }
    }

    private static string BuildCtorArgs(HandlerInfo h, string spRef)
    {
        if (h.CtorServices.Count == 0)
            return "";
        return string.Join(", ", h.CtorServices.Select(s => $"{spRef}.GetRequiredService<{s.TypeFullName}>()"));
    }

    private static string BuildNotificationStaticCall(HandlerInfo h)
    {
        var args = "n";
        if (h.HasContextParam)
            args += ", MessageContextScope.Current!";
        if (h.HasCancellationToken)
            args += ", c";
        return h.HandlerTypeFullName + "." + h.MethodName + "(" + args + ")";
    }

    private static string BuildNotificationStaticCallInLambda(HandlerInfo h)
    {
        var args = "n";
        foreach (var svc in h.MethodServices)
            args += ", " + svc.ParamName;
        if (h.HasContextParam)
            args += ", MessageContextScope.Current!";
        if (h.HasCancellationToken)
            args += ", c";
        return h.HandlerTypeFullName + "." + h.MethodName + "(" + args + ")";
    }

    private static string BuildNotificationInstanceCallInLambda(HandlerInfo h)
    {
        var args = "n";
        foreach (var svc in h.MethodServices)
            args += ", " + svc.ParamName;
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
        sb.AppendLine("#pragma warning disable IL2055, IL2072, IL3050");
        sb.AppendLine("    private static void RegisterHandlers(IServiceCollection services, ServiceLifetime lifetime, IReadOnlyList<Type> behaviorTypes)");
        sb.AppendLine("    {");
        sb.AppendLine("        var hasBehaviors = behaviorTypes.Count > 0;");
        sb.AppendLine();

        sb.AppendLine("        // Pipeline behavior registrations.");
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
        sb.AppendLine("#pragma warning restore IL2055, IL2072, IL3050");
        sb.AppendLine();
        sb.AppendLine("        services.TryAddSingleton<IMessageBus>(sp =>");
        sb.AppendLine("            new GeneratedMessageBus(sp, sp.GetRequiredService<INotificationPublishStrategy>(), hasBehaviors));");
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

    public static readonly DiagnosticDescriptor MultipleConstructors = new(
        id: "MOM003",
        title: "Multiple public constructors",
        messageFormat: "Handler '{0}' has multiple public constructors. Handlers must have exactly one public constructor for source-generated construction.",
        category: "Momentum.Messaging",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}

// ═════════════════════════════════════════════════════════════════════════
// Internal models
// ═════════════════════════════════════════════════════════════════════════

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

internal sealed class GeneratorConfig
{
    public List<string> HandlerSuffixes { get; set; } = null!;
    public List<string> MethodNames { get; set; } = null!;
    public string? CustomDiscoveryStrategyType { get; set; }
}
