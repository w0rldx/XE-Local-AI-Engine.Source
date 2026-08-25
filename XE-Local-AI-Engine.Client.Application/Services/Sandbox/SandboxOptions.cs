namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Configuration-bound CONSTRAINT on the backend that serves the AgentHome, Coder and work-session workloads. The
///     backend is resolved once at startup as a singleton, so changing <see cref="Provider" /> requires a restart.
///     There is intentionally NO execution-capable code default: an unset provider leaves the candidate set
///     unconstrained and minimal-satisfying resolution lands on the deterministic fake in non-Production, while startup
///     validation rejects an unset provider in Production (a stripped config must never silently grant the
///     host-command-executing backend). Bound from the <c>AgentHome:Sandbox</c> section.
///     <para>
///         <b>Meaning change, ADR 0007.</b> This key used to NAME the provider; the named provider was what ran, full
///         stop. It now narrows the candidate set that <c>SandboxProviderSelector</c> chooses from, and the workload's
///         own <see cref="SandboxRequirements" /> decide whether the named backend may serve it at all. On every node
///         that ships today the outcome is identical — the declaration is host-toolchain and unisolated, which
///         <c>process</c> and <c>fake</c> both satisfy — but the failure mode is different and deliberately loud:
///         naming a backend that cannot honour the declaration throws
///         <see cref="SandboxCapabilityNotSupportedException" /> at startup, naming the unmet axis. It is never
///         quietly reinterpreted as a weaker one, because that is how a hardened node becomes an unhardened one.
///     </para>
/// </summary>
public sealed class SandboxOptions
{
    public const string SectionName = "AgentHome:Sandbox";

    /// <summary>
    ///     Selected provider name. When set, must match an <see cref="ISandboxRuntimeProvider.ProviderName" /> known to
    ///     DI. Null/blank means "unset" — fail-loud in Production, deterministic fake otherwise.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    ///     Whether the AgentHome, Coder and work-session sandboxes may run at all on a backend that cannot deny egress.
    ///     Off by default, which is the 2026-08-25 operator ruling's Option B: these roles ask for
    ///     <see cref="SandboxNetworkPolicy.None" /> wherever the backend advertises it and keep running where it cannot
    ///     be enforced, with the served posture visible in the isolation table the Development capability surface
    ///     reports.
    ///     <para>
    ///         Setting it makes denial a PRECONDITION (Option A): a node whose backend does not advertise network
    ///         confinement — Windows, or a Linux host whose user-namespace probe failed — refuses to prepare the sandbox
    ///         with <see cref="SandboxCapabilityNotSupportedException" /> naming this key, instead of serving one with
    ///         the host's network. <see cref="SandboxEgressPolicy" /> is the single decision site;
    ///         <see cref="DevelopmentSandboxOptions.RequireEgressDenial" /> is the same switch for Development Mode, and
    ///         the two are separate for the reason the two <c>Provider</c> keys are.
    ///     </para>
    ///     <para>
    ///         Operator configuration, deliberately not a stored node setting an API caller or a model can write: it can
    ///         only ever TIGHTEN what runs on this node, and a tightening switch something inside the node could clear
    ///         would not be one.
    ///     </para>
    /// </summary>
    public bool RequireEgressDenial { get; set; }
}
