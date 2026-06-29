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

        // An unset provider resolves to the deterministic fake. This is the safe non-Production path; in Production the
        // SandboxOptions startup validation rejects an unset provider before anything resolves the selector, so a
        // stripped config can never reach here and silently fall back.
        var configuredProvider = services.GetRequiredService<IOptions<SandboxOptions>>().Value.Provider;
        var providerName = string.IsNullOrWhiteSpace(configuredProvider) ? FakeSandboxRuntimeProvider.Name : configuredProvider;
        return providerName switch
        {
            FakeSandboxRuntimeProvider.Name => ActivatorUtilities.CreateInstance<FakeSandboxRuntimeProvider>(services),
            ProcessSandboxRuntimeProvider.Name => ActivatorUtilities.CreateInstance<ProcessSandboxRuntimeProvider>(services),
            _ => throw new InvalidOperationException($"Unknown sandbox provider '{providerName}'.")
        };
    }
}
