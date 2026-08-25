namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     The ONE derivation of "what CPU / memory / process-count ceiling does this role's sandbox get on this node".
///     Every create site calls it with its own <see cref="SandboxRequirements" />, so a site cannot pass ceilings its
///     declaration does not claim, nor claim ceilings it does not pass — which is the half of ADR 0007 Decision 4's
///     guarantee that <c>SandboxSubstrateSelectionArchitectureTests</c> could not otherwise reach, because that file
///     constructs no consumer.
///     <para>
///         <b>Two sets, picked by the declaration alone.</b>
///         <see cref="SandboxCeilingProfile.ComputeTool" /> takes the <c>Compute</c> section's tight numbers;
///         <see cref="SandboxCeilingProfile.HostToolchain" /> takes <c>LocalContainer:ToolchainLimits</c>, derived from
///         the host wherever the operator has not overridden it. Which applies is a value on the workload's constant,
///         not a role-name <c>switch</c> here — see <see cref="SandboxCeilingProfile" /> — so adding a workload means
///         choosing a profile in the one reviewed file rather than editing this function.
///     </para>
///     <para>
///         The reason there are two at all is measured, not argued, and it is recorded on
///         <see cref="SandboxToolchainLimits" />: one shared set killed <c>dotnet build</c> outright.
///     </para>
/// </summary>
public static class SandboxResourceCeilings
{
    // Derived once, at first use. GC.GetGCMemoryInfo is the same source CapabilityReportComposer and
    // HardwareProbeEnvironment already use for "how much memory does this machine have", and it is container-aware:
    // under a cgroup memory limit it reports the limit, so a node inside a constrained container derives a ceiling
    // that fits it rather than one describing the hardware underneath.
    private static readonly SandboxResourceLimits HostToolchainDefaults =
        DeriveToolchainDefaults(Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

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
    /// <param name="requirements">The role's ADR 0007 declaration, which picks the profile.</param>
    /// <param name="capabilities">The resolved backend's advertised capabilities.</param>
    /// <param name="computeDefaults">The <c>Compute</c> section, for <see cref="SandboxCeilingProfile.ComputeTool" />.</param>
    /// <param name="nodeDefaults">
    ///     The node-wide sandbox section, for <see cref="SandboxCeilingProfile.HostToolchain" />. Both are taken even
    ///     though a given call uses one, so this stays a pure function of the declaration: a caller cannot change which
    ///     set a role gets by injecting a different option.
    /// </param>
    public static SandboxResourceLimits? Resolve(SandboxRequirements requirements,
        SandboxProviderCapabilities capabilities,
        ComputeOptions computeDefaults,
        LocalContainerOptions nodeDefaults)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(computeDefaults);
        ArgumentNullException.ThrowIfNull(nodeDefaults);

        if (!capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits))
        {
            return null;
        }

        return requirements.Ceilings switch
        {
            SandboxCeilingProfile.ComputeTool => new SandboxResourceLimits
            {
                CpuCount = computeDefaults.CpuCount,
                MemoryMb = computeDefaults.MemoryMb,
                PidsLimit = computeDefaults.PidsLimit
            },
            SandboxCeilingProfile.HostToolchain => ResolveToolchain(nodeDefaults.ToolchainLimits),
            _ => null
        };
    }

    /// <summary>
    ///     The host-toolchain ceilings this node uses, with each unset member filled from the host. Exposed so callers
    ///     and tests can state the EFFECTIVE numbers instead of restating the formula.
    /// </summary>
    public static SandboxResourceLimits ResolveToolchain(SandboxToolchainLimits configured)
    {
        ArgumentNullException.ThrowIfNull(configured);

        return new SandboxResourceLimits
        {
            CpuCount = configured.CpuCount ?? HostToolchainDefaults.CpuCount,
            MemoryMb = configured.MemoryMb ?? HostToolchainDefaults.MemoryMb,
            PidsLimit = configured.PidsLimit ?? HostToolchainDefaults.PidsLimit
        };
    }

    /// <summary>
    ///     The derivation itself, as a pure function of the two host facts, so it is testable without needing a machine
    ///     that happens to have the right shape.
    ///     <para>
    ///         CPU is every logical core: a build is the workload the operator is waiting on, and the ceiling exists to
    ///         bound a runaway rather than to reserve headroom the machine is not otherwise using. Memory is 75% of
    ///         physical RAM — leaving the engine, the model runtime and the OS the rest — floored at
    ///         <see cref="SandboxToolchainLimits.DefaultMemoryFloorMb" />, because 75% of a small machine is below what
    ///         a .NET build needs, and capped at physical RAM so the floor cannot promise memory that does not exist. A
    ///         host that reports no memory at all (the API can return 0) gets the floor.
    ///     </para>
    /// </summary>
    public static SandboxResourceLimits DeriveToolchainDefaults(int processorCount, long totalPhysicalBytes)
    {
        var physicalMb = totalPhysicalBytes > 0 ? (int)Math.Min(totalPhysicalBytes / (1024 * 1024), int.MaxValue) : 0;
        var memoryMb = physicalMb <= 0
            ? SandboxToolchainLimits.DefaultMemoryFloorMb
            : Math.Min(Math.Max((int)(physicalMb * SandboxToolchainLimits.DefaultMemoryFraction),
                    SandboxToolchainLimits.DefaultMemoryFloorMb),
                physicalMb);

        return new SandboxResourceLimits
        {
            CpuCount = Math.Max(processorCount, val2: 1),
            MemoryMb = memoryMb,
            PidsLimit = SandboxToolchainLimits.DefaultPidsLimit
        };
    }
}
