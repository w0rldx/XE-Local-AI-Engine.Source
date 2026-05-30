namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

/// <summary>
///     Configuration-bound resolver for the AgentHome <see cref="ISandboxRuntimeProvider" /> (AgentHome plan §6.2,
///     restart-required for v1). Registered once as a singleton so a provider change requires a restart. The MVP
///     default is the deterministic fake; Marker J-local fills the <c>"local-container"</c> slot with the
///     HostAgent-backed <see cref="LocalContainerSandboxProvider" />.
/// </summary>
internal static class SandboxProviderSelector
{
    public const string LocalContainerProvider = LocalContainerSandboxProvider.Name;

    public static ISandboxRuntimeProvider Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var providerName = services.GetRequiredService<IOptions<SandboxOptions>>().Value.Provider;
        return providerName switch
        {
            FakeSandboxRuntimeProvider.Name => ActivatorUtilities.CreateInstance<FakeSandboxRuntimeProvider>(services),
            LocalContainerProvider => ActivatorUtilities.CreateInstance<LocalContainerSandboxProvider>(services),
            _ => throw new InvalidOperationException($"Unknown sandbox provider '{providerName}'.")
        };
    }
}
