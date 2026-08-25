namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     The ONE derivation of "what CPU / memory / process-count ceiling does this role's sandbox get on this node".
///     Every create site calls it with its own <see cref="SandboxRequirements" />, so a site cannot pass ceilings its
///     declaration does not claim, nor claim ceilings it does not pass — which is the half of ADR 0007 Decision 4's
///     guarantee that <c>SandboxSubstrateSelectionArchitectureTests</c> could not otherwise reach, because that file
///     constructs no consumer.
///     <para>
///         <b>One set of numbers for every role, by the operator's 2026-08-25 ruling.</b> They are read from
///         <see cref="ComputeOptions" /> — the <c>Compute</c> section — because <c>run_python</c> was the first and for
///         a long time the only role that asked for ceilings, and the ruling was to reuse its node defaults rather than
///         invent a second set that could drift from it. That is a deliberate trade and it is visible in the
///         configuration surface: raising <c>Compute:MemoryMb</c> to give scripts more room also raises what an
///         AgentHome run or a Development build may use. One number an operator can reason about beats four that
///         silently disagree.
///     </para>
///     <para>
///         <b>Known ceiling: these numbers are sized for arithmetic, not for builds.</b> The defaults are 2 CPU,
///         2048 MB and 64 tasks, chosen for a <c>run_python</c> call that lasts a second or two
///         (<see cref="ComputeOptions" />), and on Linux they become <c>CPUQuota</c> / <c>MemoryMax</c> +
///         <c>MemorySwapMax=0</c> / <c>TasksMax</c> on a transient systemd scope — where <c>TasksMax</c> counts
///         THREADS, not processes, and <c>MemoryMax</c> with swap denied is an OOM kill rather than pressure. Measured
///         on this repository's own Release build on 2026-08-25: at the defaults it failed with 15 errors
///         ("Resource temporarily unavailable" starting <c>csc</c>, "Failed to create CoreCLR, HRESULT: 0x8007000E");
///         with only <c>MemoryMax=2048M</c> applied it was SIGKILLed mid-build, printing no summary; at
///         <c>MemoryMax=8192M</c> it completed in 33.6 s with zero errors. A node that runs Development Mode builds
///         under a backend advertising <see cref="SandboxProviderCapabilities.SupportsResourceLimits" /> therefore has
///         to raise these numbers. They are not silently split per role here, because which numbers a build gets is an
///         operator decision and the ruling was one set — but the operator has to make it, and the isolation summary
///         now says which roles are bounded so the question is visible.
///     </para>
/// </summary>
public static class SandboxResourceCeilings
{
    /// <summary>
    ///     The ceiling to put on a create request, or <see langword="null" /> when the role asks for none or the backend
    ///     cannot impose one.
    ///     <para>
    ///         Capability-gated rather than unconditional, and that gate is not defensive coding:
    ///         <c>SandboxLifecycleRegistry.BuildLaunchPolicy</c> REFUSES a create request that carries ceilings to a
    ///         process backend whose host has no working systemd user scope, so asking unconditionally would stop
    ///         AgentHome and Development Mode running on such a node rather than harden them. A role that asks and does
    ///         not get is reported as unbounded by the isolation summary, with the measured probe reason.
    ///     </para>
    /// </summary>
    /// <param name="requirements">The role's ADR 0007 declaration; ceilings are derived only when it asks for them.</param>
    /// <param name="capabilities">The resolved backend's advertised capabilities.</param>
    /// <param name="nodeDefaults">The node's ceilings, from the <c>Compute</c> section. See the type remarks.</param>
    public static SandboxResourceLimits? Resolve(SandboxRequirements requirements,
        SandboxProviderCapabilities capabilities,
        ComputeOptions nodeDefaults)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(nodeDefaults);

        if (!requirements.RequestsResourceLimits || !capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits))
        {
            return null;
        }

        return new SandboxResourceLimits
        {
            CpuCount = nodeDefaults.CpuCount,
            MemoryMb = nodeDefaults.MemoryMb,
            PidsLimit = nodeDefaults.PidsLimit
        };
    }
}
