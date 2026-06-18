namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

/// <summary>
///     Configuration-bound resolver for the AgentHome <see cref="ISandboxRuntimeProvider" />. Registered once as a
///     singleton so a provider change requires a restart. The default is the deterministic fake; the <c>"process"</c>
///     slot is the supervised-child <see cref="ProcessSandboxRuntimeProvider" /> (the successor to the removed
///     HostAgent-backed container provider).
/// </summary>
internal static class SandboxProviderSelector
{
    public static ISandboxRuntimeProvider Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var providerName = services.GetRequiredService<IOptions<SandboxOptions>>().Value.Provider;
        return providerName switch
        {
            FakeSandboxRuntimeProvider.Name => ActivatorUtilities.CreateInstance<FakeSandboxRuntimeProvider>(services),
            ProcessSandboxRuntimeProvider.Name => ActivatorUtilities.CreateInstance<ProcessSandboxRuntimeProvider>(services),
            _ => throw new InvalidOperationException($"Unknown sandbox provider '{providerName}'.")
        };
    }
}
