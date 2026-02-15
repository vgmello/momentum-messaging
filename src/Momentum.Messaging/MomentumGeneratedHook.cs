using Microsoft.Extensions.DependencyInjection;

namespace Momentum.Messaging;

/// <summary>
/// Bridge between the core library and source-generated code.
/// The generated ModuleInitializer sets RegistrationAction.
/// </summary>
public static class MomentumGeneratedHook
{
    /// <summary>
    /// Set by the source-generated ModuleInitializer.
    /// Parameters: services, handler lifetime, behavior open generic types.
    /// </summary>
    public static Action<IServiceCollection, ServiceLifetime, IReadOnlyList<Type>>? RegistrationAction { get; set; }
}
