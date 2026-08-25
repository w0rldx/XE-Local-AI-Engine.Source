namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     The node-wide sandbox ceilings, bound from the <c>LocalContainer</c> section. The byte budgets cover the two
///     directions data enters the jail, which are genuinely different controls: <see cref="MaxCopyFileBytes" /> bounds
///     what the ENGINE copies in from the host, and <see cref="MaxJailDiskBytes" /> bounds what the sandboxed CHILD
///     writes for itself. <see cref="ToolchainLimits" /> bounds what it may consume while doing so.
///     <para>
///         This is the section a node-wide sandbox ceiling belongs in, and the reason
///         <see cref="ToolchainLimits" /> lives here rather than in either per-feature sandbox section: AgentHome,
///         work sessions and Development Mode all read it, and <c>AgentHome:Sandbox</c> / <c>Development:Sandbox</c>
///         would have had to mirror it and then drift.
///     </para>
/// </summary>
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
    ///     How many bytes a sandbox's COMMANDS may leave in its jail directory before the one running is terminated.
    ///     Measured as the jail's occupancy above what it held when the sandbox ran its first command, so a jail that
    ///     legitimately starts non-empty after copy-in is not charged for content it did not write — and so a sandbox
    ///     cannot accumulate an unbounded amount by running one command after another, each staying just under the
    ///     line. Defaults to 512 MiB; a non-positive value disables the watchdog.
    ///     <para>
    ///         This is the NODE-WIDE ceiling — the operator's. A single sandbox may ask for a tighter one of its own
    ///         through <see cref="SandboxCreateRequest.MaxJailDiskBytes" />; nothing can ask for a looser one.
    ///     </para>
    /// </summary>
    public long MaxJailDiskBytes { get; init; } = DefaultMaxJailDiskBytes;

    /// <summary>
    ///     CPU / memory / process-count ceilings for every workload that runs a real toolchain — AgentHome, Coder, work
    ///     sessions and Development Mode. Each member is optional and each unset one is derived from this host at
    ///     startup; see <see cref="SandboxToolchainLimits" /> for the derivation and for the measurement that made
    ///     these separate from <c>run_python</c>'s.
    /// </summary>
    public SandboxToolchainLimits ToolchainLimits { get; init; } = new();
}
