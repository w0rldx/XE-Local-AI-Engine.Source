namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Process-sandbox byte-budget configuration, bound from the <c>LocalContainer</c> section and consumed by
///     <c>ProcessSandboxRuntimeProvider</c>. The two values cover the two directions data enters the jail, which are
///     genuinely different controls: <see cref="MaxCopyFileBytes" /> bounds what the ENGINE copies in from the host,
///     and <see cref="MaxJailDiskBytes" /> bounds what the sandboxed CHILD writes for itself.
/// </summary>
public sealed record LocalContainerOptions
{
    public const string SectionName = "LocalContainer";

    /// <summary>The default per-file copy ceiling (64 MiB). A file over this is skipped and logged, never truncated.</summary>
    public const long DefaultMaxCopyFileBytes = 64L * 1024 * 1024;

    /// <summary>The default ceiling on how far a sandboxed command may grow its own jail directory (512 MiB).</summary>
    public const long DefaultMaxJailDiskBytes = 512L * 1024 * 1024;

    /// <summary>The per-file copy-into ceiling in bytes. Defaults to 64 MiB.</summary>
    public long MaxCopyFileBytes { get; init; } = DefaultMaxCopyFileBytes;

    /// <summary>
    ///     How many bytes a single sandboxed command may ADD to its jail directory before it is terminated. Measured as
    ///     growth from the jail's size when the command started, so a jail that legitimately starts non-empty after
    ///     copy-in is not charged for content it did not write. Defaults to 512 MiB; a non-positive value disables the
    ///     watchdog.
    /// </summary>
    public long MaxJailDiskBytes { get; init; } = DefaultMaxJailDiskBytes;
}
