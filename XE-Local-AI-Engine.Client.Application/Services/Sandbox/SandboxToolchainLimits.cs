namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The node's CPU / memory / process-count ceilings for every workload that runs a real toolchain — AgentHome,
///     Coder, work sessions and Development Mode. Bound from <c>LocalContainer:ToolchainLimits</c>.
///     <para>
///         <b>Why here.</b> Three features read these, so they cannot live in either per-feature sandbox section
///         (<c>AgentHome:Sandbox</c>, <c>Development:Sandbox</c>) without being mirrored and drifting.
///         <see cref="LocalContainerOptions" /> is already the node-wide sandbox section, already bound and validated
///         unconditionally, and already the home of the node-wide <see cref="LocalContainerOptions.MaxJailDiskBytes" />
///         ceiling — so a second node-wide ceiling belongs beside the first rather than in a new section of its own.
///     </para>
///     <para>
///         <b>Why these are NOT <c>run_python</c>'s numbers, measured rather than argued.</b> Until 2026-08-25 every
///         role shared the <c>Compute</c> section's ceilings — 2 CPU, 2048 MB, 64 tasks — which are sized for a
///         two-second script. On Linux those become <c>CPUQuota</c>, <c>MemoryMax</c> + <c>MemorySwapMax=0</c> and
///         <c>TasksMax</c> on a transient systemd scope, where <c>TasksMax</c> counts THREADS and a memory ceiling with
///         swap denied is an OOM kill rather than back-pressure. Measured against this repository's own Release build
///         on that date: at the shared numbers it failed with 15 errors ("Resource temporarily unavailable" starting
///         <c>csc</c>, "Failed to create CoreCLR, HRESULT: 0x8007000E"); under the 2048 MB memory ceiling alone it was
///         SIGKILLed mid-build and printed no summary at all; at 8192 MB it completed in 33.6 s with zero errors. The
///         operator's ruling followed from that: a build gets its own set, derived from the host.
///     </para>
///     <para>
///         Every member is optional and every unset member is derived from the host at startup — see
///         <see cref="SandboxResourceCeilings.DeriveToolchainDefaults" />. An operator overrides any of them
///         individually; <c>LocalContainerOptionsValidator</c> rejects a memory or process ceiling small enough that no
///         real build could run under it, because a ceiling that silently OOM-kills every attempt is worse than none.
///     </para>
/// </summary>
public sealed record SandboxToolchainLimits
{
    /// <summary>The smallest memory ceiling an operator may configure, in MB. Below this a .NET build cannot start.</summary>
    public const int MinimumMemoryMb = 1024;

    /// <summary>The smallest process/thread ceiling an operator may configure. MSBuild alone exceeds far less than this.</summary>
    public const int MinimumPidsLimit = 256;

    /// <summary>The memory floor the derived default never goes below, in MB, however little RAM the host reports.</summary>
    public const int DefaultMemoryFloorMb = 4096;

    /// <summary>The fraction of the host's physical RAM the derived memory default takes.</summary>
    public const double DefaultMemoryFraction = 0.75;

    /// <summary>The derived process/thread default. Generous on purpose: it counts threads, and a parallel build has many.</summary>
    public const int DefaultPidsLimit = 4096;

    /// <summary>CPU-core ceiling. Unset derives all of the host's logical cores.</summary>
    public double? CpuCount { get; init; }

    /// <summary>
    ///     Resident-memory ceiling in MB. Unset derives 75% of the host's physical RAM, never below
    ///     <see cref="DefaultMemoryFloorMb" /> and never above what the host actually has.
    /// </summary>
    public int? MemoryMb { get; init; }

    /// <summary>Process/thread ceiling. Unset derives <see cref="DefaultPidsLimit" />.</summary>
    public int? PidsLimit { get; init; }
}
