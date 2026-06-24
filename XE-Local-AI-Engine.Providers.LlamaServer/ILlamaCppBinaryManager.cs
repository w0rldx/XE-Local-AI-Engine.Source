namespace XE_Local_AI_Engine.Providers.LlamaServer;

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
}
