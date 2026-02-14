using Microsoft.Extensions.DependencyInjection;

namespace Momentum.Messaging;

/// <summary>
/// Bridge between the core library and source-generated code.
/// The generated ModuleInitializer sets RegistrationAction.
/// </summary>
public static class MomentumGeneratedHook
{
    public static Action<IServiceCollection, ServiceLifetime>? RegistrationAction { get; set; }
}
