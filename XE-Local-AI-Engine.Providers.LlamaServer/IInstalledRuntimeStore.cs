namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The authoritative record of the llama.cpp runtime that is actually installed on disk — written only after a
///     verified, smoke-tested install. Tier 2 of the 3-tier resolve (live API → this state → pinned floor).
/// </summary>
/// <param name="Tag">The installed release tag (for example <c>b9700</c>).</param>
/// <param name="Asset">The installed asset file name.</param>
/// <param name="Sha256">The lowercase hex SHA256 the installed archive was verified against.</param>
/// <param name="Variant">The acceleration variant of the installed binary.</param>
/// <param name="InstalledAtUtc">When the install completed (UTC).</param>
public sealed record InstalledRuntimeState(
    string Tag,
    string Asset,
    string Sha256,
    GpuVariant Variant,
    DateTimeOffset InstalledAtUtc);

/// <summary>
///     Reads/writes <c>installed-runtime.json</c> under the cache root (sibling to <c>llama.cpp/</c>). The single record
///     of the installed runtime version.
/// </summary>
/// <remarks>
///     Tolerant deserialize (absent or corrupt file → <see langword="null" />, no throw). Atomic write (temp file →
///     <see cref="File.Move(string, string, bool)" />). Owner-only (0600) permissions on non-Windows, mirroring the node
///     settings store posture.
/// </remarks>
public interface IInstalledRuntimeStore
{
    /// <summary>Reads the installed-runtime state, or <see langword="null" /> when absent/corrupt (first run).</summary>
    Task<InstalledRuntimeState?> ReadAsync(CancellationToken ct);

    /// <summary>Atomically writes the installed-runtime state after a verified, smoke-tested install.</summary>
    Task WriteAsync(InstalledRuntimeState state, CancellationToken ct);
}
