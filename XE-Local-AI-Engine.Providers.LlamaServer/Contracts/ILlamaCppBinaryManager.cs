namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Resolves, downloads, hash-verifies, and caches a prebuilt llama.cpp binary for the host. Never source-builds.
/// </summary>
/// <remarks>
///     Responsibility: pick the prebuilt release asset for the OS/arch + requested <see cref="GpuVariant" />
///     → download via <see cref="HttpClient" /> → verify SHA256 against the pinned hash (corrupt → re-download once then
///     surface a sanitized error) → cache under a stable app dir → track the recommended-pinned version vs a
///     user-selected upgrade (an upgrade must never delete the pinned fallback) → offline uses the cached pinned binary.
/// </remarks>
public interface ILlamaCppBinaryManager
{
    /// <summary>
    ///     Ensures a hash-verified <c>llama-server</c> binary for <paramref name="variant" /> is present on disk and
    ///     returns its resolved location. Idempotent: a cached, hash-valid binary is reused without re-download.
    /// </summary>
    /// <exception cref="LlamaRuntimeException">
    ///     The binary could not be acquired (download failure, repeated hash mismatch, no prebuilt for the host) — the
    ///     message is sanitized for direct display.
    /// </exception>
    Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct);

    /// <summary>
    ///     Resolves an ALREADY-AVAILABLE <c>llama-server</c> for <paramref name="variant" /> and returns
    ///     <see langword="null" /> when none is, instead of acquiring one.
    /// </summary>
    /// <remarks>
    ///     READ-ONLY means precisely: no network request of any kind, no directory created — the cache tree is only
    ///     probed with <see cref="File.Exists(string)" />/<see cref="Directory.Exists(string)" /> — and
    ///     <c>installed-runtime.json</c> read but never written, not even to self-heal a stale source-build record. It
    ///     is NOT process-free, and it deliberately skips the live-catalog tier. See
    ///     docs/wiki/03-local-runtime-and-providers.md, "The read-only installed-binary query".
    /// </remarks>
    /// <exception cref="LlamaRuntimeException">A configured bring-your-own override failed validation.</exception>
    Task<LlamaBinary?> TryGetInstalledBinaryAsync(GpuVariant variant, CancellationToken ct);

    /// <summary>
    ///     Runtime endpoint ensure surface. The caller owns <paramref name="mutationLease" /> through completion; the
    ///     supervisor spawn path uses the non-lease overload because its registered inflight spawn is the exclusion token.
    /// </summary>
    Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant,
        ILlamaServerRuntimeMutationLease mutationLease,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutationLease);
        return EnsureBinaryAsync(variant, ct);
    }

    /// <summary>
    ///     Installs a specific, dynamically-resolved release: downloads <paramref name="assetName" /> for
    ///     <paramref name="tag" />, verifies it against the live publisher <paramref name="digestSha256" /> (a
    ///     <c>sha256:</c> prefix is tolerated), extracts, smoke-tests and records it only on success.
    /// </summary>
    /// <param name="expectedSize">Catalog-reported asset size in bytes; a non-positive value means unknown.</param>
    /// <exception cref="LlamaRuntimeException">Malformed tag or asset name, or a failed download, size, digest or smoke check — sanitized.</exception>
    /// <remarks>
    ///     Extraction is atomic into the versioned cache dir and the record goes to <c>installed-runtime.json</c>; on
    ///     any failure the previously-installed binary is left untouched. A positive <paramref name="expectedSize" />
    ///     adds a pre-download ceiling check and a post-download length match, otherwise only the absolute download
    ///     ceiling applies. Direct callers are limited to startup provisioning before the supervisor accepts work —
    ///     runtime and API callers use the lease-bearing overload below — and both fail closed on a source record.
    /// </remarks>
    Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct);

    /// <summary>
    ///     Runtime endpoint mutation surface: the caller owns <paramref name="mutationLease" /> and must hold it
    ///     through completion.
    /// </summary>
    /// <remarks>
    ///     The manager validates source-record exclusion but neither acquires nor disposes the supervisor lease. That
    ///     explicit ownership avoids recursive lease acquisition while preventing endpoint updates during starts.
    /// </remarks>
    Task<LlamaBinary> InstallTagAsync(string tag,
        string assetName,
        string digestSha256,
        long expectedSize,
        GpuVariant variant,
        ILlamaServerRuntimeMutationLease mutationLease,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutationLease);
        return InstallTagAsync(tag, assetName, digestSha256, expectedSize, variant, ct);
    }

    /// <summary>
    ///     Adopts an in-app source-built CUDA runtime: validates the freshly-built <c>llama-server</c> under
    ///     <paramref name="buildBinDir" />, records it with its computed SHA256 and marks the managed-CUDA cached
    ///     signal available.
    /// </summary>
    /// <remarks>
    ///     Validation is the full path-chain perms and ownership walk plus a <c>--version</c> smoke test and a
    ///     <c>--list-devices</c> GPU-presence check; the <c>installed-runtime.json</c> record carries
    ///     <see cref="InstalledRuntimeState.SourceBuildPath" />. A validation failure records nothing, so a failed
    ///     build never becomes active and never silently degrades to CPU.
    /// </remarks>
    /// <param name="buildBinDir">Absolute directory holding the built <c>llama-server</c> (and its sibling <c>.so</c> files).</param>
    /// <param name="tag">The pinned llama.cpp tag the build was produced from (for the runtime record + rebuild-staleness).</param>
    /// <exception cref="LlamaRuntimeException">Validation failed (path-chain, smoke, or GPU presence) — sanitized for display.</exception>
    Task<InstalledRuntimeState> AdoptCudaSourceBuildAsync(string buildBinDir, string tag, CancellationToken ct);

    /// <summary>Adopts a generalized source-built runtime with exact provenance.</summary>
    Task<InstalledRuntimeState> AdoptSourceBuildAsync(string buildBinDir,
        string tag,
        GpuVariant variant,
        string sourceRepository,
        string sourceCommit,
        LlamaCppSourceRevisionMode revisionMode,
        string? requestedCommit,
        CancellationToken ct)
    {
        throw new NotSupportedException("Generalized source-build adoption is not supported by this binary manager.");
    }

    /// <summary>Adopts a generalized source build while preserving the explicit source-selection intent.</summary>
    Task<InstalledRuntimeState> AdoptSourceBuildAsync(string buildBinDir,
        string tag,
        GpuVariant variant,
        string sourceRepository,
        string sourceCommit,
        LlamaCppSourceRevisionMode revisionMode,
        string? requestedCommit,
        LlamaCppSourceSelection sourceSelection,
        CancellationToken ct)
    {
        return AdoptSourceBuildAsync(buildBinDir, tag, variant, sourceRepository, sourceCommit, revisionMode, requestedCommit, ct);
    }

    /// <summary>
    ///     Removes the recorded managed source-built runtime, clearing the record and the cached signal. Idempotent: a
    ///     no-op when no source build is recorded. <c>[secMED-3]</c>
    /// </summary>
    /// <remarks>
    ///     The on-disk build tree is deleted ONLY when the recorded
    ///     <see cref="InstalledRuntimeState.SourceBuildPath" /> is byte-for-byte the bin directory of a location this
    ///     manager computes itself under the cache root — the generalized <c>source-build/active</c> tree or the
    ///     pre-generalization <c>source-cuda/{PinnedTag}</c> one. Any other recorded path is NEVER deleted, and the
    ///     record is cleared regardless, so no caller reports success while stale source state stays active.
    /// </remarks>
    Task RemoveSourceBuildAsync(CancellationToken ct);
}
