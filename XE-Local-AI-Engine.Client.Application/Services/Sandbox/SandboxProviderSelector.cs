namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

/// <summary>
///     Configuration-bound resolvers for the two per-feature sandbox roles. Each is registered once
///     as a singleton factory, so a provider change requires a restart.
///     <para>
///         There is deliberately no resolver for a bare <see cref="ISandboxRuntimeProvider" />, and no DI registration
///         of that interface either: "the sandbox provider" is not a thing this engine has. AgentHome and Coder take
///         <see cref="IAgentSandboxRuntimeProvider" />; Development Mode takes
///         <see cref="IDevelopmentSandboxRuntimeProvider" />.
///     </para>
///     <para>
///         Both resolvers reach their providers through <c>GetRequiredService&lt;TConcrete&gt;()</c> rather than
///         <c>ActivatorUtilities.CreateInstance</c>, so two roles naming the same provider share ONE instance. That is
///         a correctness requirement, not a saving: <see cref="ProcessSandboxRuntimeProvider" /> allocates its jail
///         root once per instance, and Coder reaches AgentHome's live sandbox by attach key through
///         <see cref="ISandboxRuntimeProvider.ConnectAsync" /> — a second instance would answer "no such sandbox" to
///         every coder tool.
///     </para>
/// </summary>
internal static class SandboxProviderSelector
{
    /// <summary>
    ///     Resolves the AgentHome/Coder sandbox from <c>AgentHome:Sandbox:Provider</c>. It cannot return a container
    ///     provider — <see cref="DockerSandboxRuntimeProvider" /> does not implement
    ///     <see cref="IAgentSandboxRuntimeProvider" />, so that is a type error rather than a rule.
    /// </summary>
    public static IAgentSandboxRuntimeProvider ResolveAgent(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var providerName = ResolveAgentProviderName(services);
        return providerName switch
        {
            FakeSandboxRuntimeProvider.Name => services.GetRequiredService<FakeSandboxRuntimeProvider>(),
            ProcessSandboxRuntimeProvider.Name => services.GetRequiredService<ProcessSandboxRuntimeProvider>(),
            _ => throw new InvalidOperationException($"Unknown sandbox provider '{providerName}'.")
        };
    }

    /// <summary>
    ///     Resolves the Development Mode sandbox from <c>Development:Sandbox:Provider</c>, falling back to whatever the
    ///     agent role resolved when that is unset. The fallback is what makes introducing this seam a runtime no-op: a
    ///     node that sets only <c>AgentHome:Sandbox:Provider</c> keeps running Development Mode on that same provider
    ///     instance, exactly as it did when one registration served both.
    /// </summary>
    public static IDevelopmentSandboxRuntimeProvider ResolveDevelopment(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var configuredProvider = services.GetRequiredService<IOptions<DevelopmentSandboxOptions>>().Value.Provider;
        var providerName = string.IsNullOrWhiteSpace(configuredProvider)
            ? ResolveAgentProviderName(services)
            : configuredProvider;

        return providerName switch
        {
            FakeSandboxRuntimeProvider.Name => services.GetRequiredService<FakeSandboxRuntimeProvider>(),
            ProcessSandboxRuntimeProvider.Name => services.GetRequiredService<ProcessSandboxRuntimeProvider>(),
            DockerSandboxRuntimeProvider.Name => services.GetRequiredService<DockerSandboxRuntimeProvider>(),
            _ => throw new InvalidOperationException($"Unknown Development sandbox provider '{providerName}'.")
        };
    }

    /// <summary>
    ///     Resolves the work-session sandbox. It follows whatever the agent role resolved: there is no
    ///     <c>WorkSessions:Sandbox:Provider</c> key, because nothing in v1 executes inside this jail and inventing a
    ///     setting for a role with no consumer would be one more thing an operator can get wrong for no effect. Give it
    ///     its own key when a session tool needs one.
    /// </summary>
    public static IWorkSessionSandboxRuntimeProvider ResolveWorkSession(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var providerName = ResolveAgentProviderName(services);
        return providerName switch
        {
            FakeSandboxRuntimeProvider.Name => services.GetRequiredService<FakeSandboxRuntimeProvider>(),
            ProcessSandboxRuntimeProvider.Name => services.GetRequiredService<ProcessSandboxRuntimeProvider>(),
            _ => throw new InvalidOperationException($"Unknown work session sandbox provider '{providerName}'.")
        };
    }

    // An unset provider resolves to the deterministic fake. This is the safe non-Production path; in Production the
    // SandboxOptions startup validation rejects an unset provider before anything resolves the selector, so a
    // stripped config can never reach here and silently fall back.
    private static string ResolveAgentProviderName(IServiceProvider services)
    {
        var configuredProvider = services.GetRequiredService<IOptions<SandboxOptions>>().Value.Provider;
        return string.IsNullOrWhiteSpace(configuredProvider) ? FakeSandboxRuntimeProvider.Name : configuredProvider;
    }
}
