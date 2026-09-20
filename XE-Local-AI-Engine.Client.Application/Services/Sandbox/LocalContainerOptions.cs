namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>The node-wide sandbox ceilings, bound from the <c>LocalContainer</c> section.</summary>
/// <remarks>
///     The byte budgets cover the two directions data enters the jail, which are genuinely different controls:
///     <see cref="MaxCopyFileBytes" /> bounds what the ENGINE copies in from the host, <see cref="MaxJailDiskBytes" /> what the sandboxed
///     CHILD writes for itself, and <see cref="ToolchainLimits" /> what it may consume while doing so. That last one lives here rather than
///     in either per-feature sandbox section because AgentHome, work sessions and Development Mode all read it, and a mirrored copy in each
///     would drift.
/// </remarks>
public sealed record LocalContainerOptions
{
    public const string SectionName = "LocalContainer";

    /// <summary>The default per-file copy ceiling (64 MiB). A file over this is skipped and logged, never truncated.</summary>
    public const long DefaultMaxCopyFileBytes = 64L * 1024 * 1024;

    /// <summary>The default ceiling on how much a sandbox's commands may leave in its jail directory (512 MiB).</summary>
    public const long DefaultMaxJailDiskBytes = 512L * 1024 * 1024;

    /// <summary>The per-file copy-into ceiling in bytes. Defaults to 64 MiB.</summary>
    public long MaxCopyFileBytes { get; init; } = DefaultMaxCopyFileBytes;

    /// <summary>
    ///     How many bytes a sandbox's COMMANDS may leave in its jail directory before the one running is terminated; 512 MiB by default,
    ///     and a non-positive value disables the watchdog.
    /// </summary>
    /// <remarks>
    ///     Measured as the jail's occupancy above what it held when the sandbox ran its first command, so a jail that legitimately starts
    ///     non-empty after copy-in is not charged for content it did not write, and a sandbox cannot accumulate an unbounded amount by
    ///     running one command after another just under the line. This is the NODE-WIDE operator ceiling: a sandbox may ask for a tighter
    ///     one through <see cref="SandboxCreateRequest.MaxJailDiskBytes" />, never a looser one.
    /// </remarks>
    public long MaxJailDiskBytes { get; init; } = DefaultMaxJailDiskBytes;

    /// <summary>
    ///     CPU, memory and process-count ceilings for every workload that runs a real toolchain — AgentHome, Coder, work sessions and
    ///     Development Mode.
    /// </summary>
    /// <remarks>
    ///     Each member is optional and each unset one is derived from this host at startup; <see cref="SandboxToolchainLimits" /> carries
    ///     the derivation and the measurement that made these separate from <c>run_python</c>'s.
    /// </remarks>
    public SandboxToolchainLimits ToolchainLimits { get; init; } = new();
}
