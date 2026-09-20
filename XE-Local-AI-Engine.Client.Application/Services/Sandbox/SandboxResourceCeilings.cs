namespace XE_Local_AI_Engine.Client.Services.Sandbox;

using XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     The ONE derivation of "what CPU, memory and process-count ceiling does this role's sandbox get on this node".
/// </summary>
/// <remarks>
///     Every create site calls it with its own <see cref="SandboxRequirements" />, so a site cannot pass ceilings its declaration does not
///     claim nor claim ceilings it does not pass — the half of ADR 0007 Decision 4's guarantee an architecture test cannot reach, since
///     that file constructs no consumer. Two sets, picked by the declaration alone: <c>ComputeTool</c> takes the <c>Compute</c> section's
///     tight numbers, <c>HostToolchain</c> takes <c>LocalContainer:ToolchainLimits</c>. There are two because one shared set killed
///     <c>dotnet build</c> outright, measured on <see cref="SandboxToolchainLimits" />.
/// </remarks>
public static class SandboxResourceCeilings
{
    // Derived once, at first use, from the same source CapabilityReportComposer and HardwareProbeEnvironment use. It is container-aware:
    // under a cgroup memory limit it reports the limit, so a constrained node derives a ceiling that fits it, not the hardware underneath.
    private static readonly SandboxResourceLimits HostToolchainDefaults =
        DeriveToolchainDefaults(Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    /// <summary>The ceiling to put on a create request, or <see langword="null" /> when the role asks for none or none can be imposed.</summary>
    /// <param name="requirements">The role's ADR 0007 declaration, which picks the profile.</param>
    /// <param name="capabilities">The resolved backend's advertised capabilities.</param>
    /// <param name="computeDefaults">The <c>Compute</c> section, for <see cref="SandboxCeilingProfile.ComputeTool" />.</param>
    /// <param name="nodeDefaults">
    ///     The node-wide sandbox section, for <c>HostToolchain</c>. Both are taken although a call uses one, so this stays a pure function
    ///     of the declaration.
    /// </param>
    /// <remarks>
    ///     Capability-gated, and the gate is not defensive coding: <c>SandboxLifecycleRegistry.BuildLaunchPolicy</c> REFUSES a request
    ///     carrying ceilings to a process backend whose host has no working systemd user scope, so asking unconditionally would stop
    ///     AgentHome and Development Mode running there rather than harden them. A role that asks and does not get is reported as
    ///     unbounded by the isolation summary, with the probe reason.
    /// </remarks>
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

    /// <summary>The host-toolchain ceilings this node uses, with each unset member filled from the host.</summary>
    /// <remarks>Exposed so callers and tests can state the EFFECTIVE numbers instead of restating the formula.</remarks>
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
    ///     The derivation itself, as a pure function of the two host facts, so it is testable without a machine of the right shape.
    /// </summary>
    /// <remarks>
    ///     CPU is every logical core: a build is the workload the operator is waiting on, and the ceiling bounds a runaway rather than
    ///     reserving headroom. Memory is 75% of physical RAM — leaving the engine, the model runtime and the OS the rest — floored at
    ///     <see cref="SandboxToolchainLimits.DefaultMemoryFloorMb" />, because 75% of a small machine is under what a .NET build needs, and
    ///     capped at physical RAM so the floor cannot promise memory that does not exist. A host reporting no memory gets the floor.
    /// </remarks>
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
