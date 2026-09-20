namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Configuration-bound CONSTRAINT on the backend that serves the AgentHome, Coder and work-session workloads, bound from the
///     <c>AgentHome:Sandbox</c> section and resolved once at startup, so a change requires a restart.
/// </summary>
/// <remarks>
///     There is intentionally NO execution-capable default: an unset provider leaves the candidate set unconstrained and resolution lands
///     on the deterministic fake outside Production, while startup validation rejects an unset provider IN Production, so a stripped config
///     never silently grants the host-command-executing backend. The key CONSTRAINS rather than names: the workload's own
///     <see cref="SandboxRequirements" /> decide whether the named backend may serve it, and naming one that cannot honour the declaration
///     throws <see cref="SandboxCapabilityNotSupportedException" /> at startup with the unmet axis, never a quiet reinterpretation.
/// </remarks>
public sealed class SandboxOptions
{
    public const string SectionName = "AgentHome:Sandbox";

    /// <summary>
    ///     Selected provider name; when set it must match an <see cref="ISandboxRuntimeProvider.ProviderName" /> known to DI. Null or blank
    ///     means unset — fail-loud in Production, deterministic fake otherwise.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    ///     Whether the AgentHome, Coder and work-session sandboxes may run at all on a backend that cannot deny egress. Off by default.
    /// </summary>
    /// <remarks>
    ///     Off, these roles ask for <see cref="SandboxNetworkPolicy.None" /> wherever the backend advertises it and keep running where it
    ///     cannot be enforced, the served posture visible in the isolation table. Set, denial is a PRECONDITION: a node whose backend does
    ///     not advertise network confinement refuses to prepare the sandbox with <see cref="SandboxCapabilityNotSupportedException" />
    ///     naming this key. <see cref="SandboxEgressPolicy" /> is the single decision site. Operator configuration, deliberately not a
    ///     stored node setting an API caller or a model can write: a tightening switch something inside the node could clear is not one.
    /// </remarks>
    public bool RequireEgressDenial { get; set; }
}
