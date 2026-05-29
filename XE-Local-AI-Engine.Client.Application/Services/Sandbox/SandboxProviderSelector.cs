namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;

/// <summary>
///     Configuration-bound resolver for the AgentHome <see cref="ISandboxRuntimeProvider" /> (AgentHome plan §6.2,
///     restart-required for v1). Registered once as a singleton so a provider change requires a restart. The
///     <c>"local-container"</c> slot is reserved for Marker J-local and throws until that provider ships.
/// </summary>
internal static class SandboxProviderSelector
{
    public const string LocalContainerProvider = "local-container";

    public static ISandboxRuntimeProvider Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var providerName = services.GetRequiredService<IOptions<SandboxOptions>>().Value.Provider;
        return providerName switch
        {
            FakeSandboxRuntimeProvider.Name => ActivatorUtilities.CreateInstance<FakeSandboxRuntimeProvider>(services),
            LocalContainerProvider => throw new InvalidOperationException(
                "Sandbox provider 'local-container' is not available until Marker J-local."),
            _ => throw new InvalidOperationException($"Unknown sandbox provider '{providerName}'.")
        };
    }
}
