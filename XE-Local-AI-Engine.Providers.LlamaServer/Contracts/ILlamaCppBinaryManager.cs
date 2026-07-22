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
    ///     Installs a specific, dynamically-resolved release: downloads <paramref name="assetName" /> for
    ///     <paramref name="tag" />, verifies it against the live publisher <paramref name="digestSha256" /> (a
    ///     <c>sha256:</c> prefix is tolerated), atomically extracts into the versioned cache dir, smoke-tests the resolved
    ///     <c>llama-server</c>, and — only on success — records the install in <c>installed-runtime.json</c>. On any
    ///     failure the previously-installed binary is left untouched and a sanitized error is surfaced.
    /// </summary>
    /// <param name="expectedSize">
    ///     The catalog-reported asset size in bytes. A non-positive value means "unknown" and only the absolute download
    ///     ceiling is enforced; a positive value adds a pre-download ceiling check and a post-download length match.
    /// </param>
    /// <exception cref="LlamaRuntimeException">
    ///     The tag/asset name is malformed, or the download / size / digest verification / smoke test failed — sanitized
    ///     for display.
    /// </exception>
    Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct);

    /// <summary>
    ///     Adopts an in-app source-built CUDA runtime: validates the freshly-built <c>llama-server</c> under
    ///     <paramref name="buildBinDir" /> (full path-chain perms/ownership + <c>--version</c> smoke + <c>--list-devices</c>
    ///     GPU presence), records it in <c>installed-runtime.json</c> with its computed SHA256 and
    ///     <see cref="InstalledRuntimeState.SourceBuildPath" /> set, and marks the managed-CUDA cached signal available. A
    ///     validation failure throws a sanitized <see cref="LlamaRuntimeException" /> and records nothing — a failed build
    ///     never becomes active and never silently degrades to CPU.
    /// </summary>
    /// <param name="buildBinDir">Absolute directory holding the built <c>llama-server</c> (and its sibling <c>.so</c> files).</param>
    /// <param name="tag">The pinned llama.cpp tag the build was produced from (for the runtime record + rebuild-staleness).</param>
    /// <exception cref="LlamaRuntimeException">Validation failed (path-chain, smoke, or GPU presence) — sanitized for display.</exception>
    Task<InstalledRuntimeState> AdoptCudaSourceBuildAsync(string buildBinDir, string tag, CancellationToken ct);

    /// <summary>
    ///     Removes a managed CUDA source build: deletes the on-disk build tree (ONLY after asserting the recorded
    ///     <see cref="InstalledRuntimeState.SourceBuildPath" /> is a normalized child of
    ///     <c>{cacheRoot}/llama.cpp/source-cuda/</c> — never deleting outside it), clears the installed-runtime record, and
    ///     clears the managed-CUDA cached signal. Idempotent: a no-op when no source build is recorded. <c>[secMED-3]</c>
    /// </summary>
    Task RemoveCudaSourceBuildAsync(CancellationToken ct);

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

    /// <summary>Removes the active generalized source-built runtime.</summary>
    Task RemoveSourceBuildAsync(CancellationToken ct)
    {
        return RemoveCudaSourceBuildAsync(ct);
    }
}
