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
}
