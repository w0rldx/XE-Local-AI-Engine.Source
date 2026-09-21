namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The authoritative record of the llama.cpp runtime that is actually installed on disk — written only after a
///     verified, smoke-tested install. Tier 2 of the 3-tier resolve (live API → this state → pinned floor).
/// </summary>
/// <param name="Tag">The installed release tag (for example <c>b9700</c>).</param>
/// <param name="Asset">The installed asset file name.</param>
/// <param name="Sha256">The lowercase hex SHA256 the installed archive was verified against.</param>
/// <param name="Variant">The acceleration variant of the installed binary.</param>
/// <param name="InstalledAtUtc">When the install completed (UTC).</param>
/// <param name="SourceBuildPath">Absolute directory of a source-built runtime, or <see langword="null" /> for a prebuilt.</param>
/// <remarks>
///     <see cref="SourceBuildPath" /> presence is the single signal that a record describes a <em>managed source
///     build</em>: readers key off it, or off the wire <c>isSourceBuild</c> flag, never parsing the sentinel
///     <see cref="Asset" /> value. It is an optional trailing positional, so an older file needs no migration.
/// </remarks>
public sealed record InstalledRuntimeState(
    string Tag,
    string Asset,
    string Sha256,
    GpuVariant Variant,
    DateTimeOffset InstalledAtUtc,
    string? SourceBuildPath = null,
    string? SourceRepository = null,
    string? SourceCommit = null,
    LlamaCppSourceRevisionMode? SourceRevisionMode = null,
    string? SourceRequestedCommit = null,
    LlamaCppSourceSelection? SourceSelection = null);

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
    /// <summary>
    ///     Takes the cross-process lock guarding the record, releasing it on dispose.
    /// </summary>
    /// <remarks>
    ///     <see cref="ReadAsync" /> and <see cref="WriteAsync" /> are each atomic on their own and do NOT take it; a
    ///     caller that READS the record and then writes based on what it read holds it across both. The record lives
    ///     under the shared user-level cache root, so two nodes on one machine otherwise interleave and one lands a
    ///     prebuilt record over the other's source build. Throws <see cref="IOException" /> when the lock stays held
    ///     past a bounded wait.
    /// </remarks>
    Task<IDisposable> AcquireAsync(CancellationToken ct);

    /// <summary>Reads the installed-runtime state, or <see langword="null" /> when absent/corrupt (first run).</summary>
    Task<InstalledRuntimeState?> ReadAsync(CancellationToken ct);

    /// <summary>Atomically writes the installed-runtime state after a verified, smoke-tested install.</summary>
    Task WriteAsync(InstalledRuntimeState state, CancellationToken ct);

    /// <summary>
    ///     Deletes the installed-runtime record, returning resolution to the pinned floor; idempotent, a missing file
    ///     being a no-op.
    /// </summary>
    /// <remarks>
    ///     Used when a managed source build is removed, or when a recorded source build is found missing or invalid at
    ///     serve time and must be discarded.
    /// </remarks>
    Task DeleteAsync(CancellationToken ct);
}
