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
}
