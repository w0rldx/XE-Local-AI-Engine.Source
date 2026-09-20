namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The node's CPU, memory and process-count ceilings for every workload that runs a real toolchain — AgentHome, Coder, work sessions
///     and Development Mode. Bound from <c>LocalContainer:ToolchainLimits</c>.
/// </summary>
/// <remarks>
///     Node-wide rather than per-feature because three features read them and a mirrored copy would drift; it sits beside the node-wide
///     <see cref="LocalContainerOptions.MaxJailDiskBytes" />. These are NOT <c>run_python</c>'s numbers, measured rather than argued: under
///     the compute profile's 2 CPU / 2048 MB / 64 tasks this repository's Release build failed with 15 errors, and under 2048 MB alone it
///     was SIGKILLed mid-build, while 8192 MB completed it — on Linux <c>TasksMax</c> counts THREADS and a memory ceiling with swap denied
///     is an OOM kill, not back-pressure. Unset members derive from the host.
/// </remarks>
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
