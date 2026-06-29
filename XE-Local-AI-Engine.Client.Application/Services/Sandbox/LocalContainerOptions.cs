namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Process-sandbox copy-size limit configuration, bound from the <c>LocalContainer</c> section and consumed by
///     <c>ProcessSandboxRuntimeProvider</c>. The single live value bounds the whole-file copy-into transfer
///     (<see cref="MaxCopyFileBytes" />): a file over this ceiling is skipped and logged, never truncated.
/// </summary>
public sealed record LocalContainerOptions
{
    public const string SectionName = "LocalContainer";

    /// <summary>The default per-file copy ceiling (64 MiB). A file over this is skipped and logged, never truncated.</summary>
    public const long DefaultMaxCopyFileBytes = 64L * 1024 * 1024;

    /// <summary>The per-file copy-into ceiling in bytes. Defaults to 64 MiB.</summary>
    public long MaxCopyFileBytes { get; init; } = DefaultMaxCopyFileBytes;
}
