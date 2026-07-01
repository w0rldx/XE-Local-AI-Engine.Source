namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Resolves, downloads, hash-verifies, and caches a prebuilt stable-diffusion.cpp <c>sd-server</c> binary for the
///     host. Never source-builds. Mirrors <c>ILlamaCppBinaryManager</c> for the image runtime.
/// </summary>
/// <remarks>
///     Responsibility: pick the prebuilt release asset for the OS/arch + requested <see cref="SdGpuBackend" />
///     → download via <see cref="HttpClient" /> → verify SHA256 against the pinned hash (corrupt → re-download once then
///     surface a sanitized error) → cache under a stable app dir → offline uses the cached pinned binary. An active
///     operator bring-your-own override short-circuits all acquisition and serves the supplied binary instead.
/// </remarks>
public interface IStableDiffusionBinaryManager
{
    /// <summary>
    ///     Ensures a hash-verified <c>sd-server</c> binary for <paramref name="backend" /> is present on disk and returns
    ///     its resolved location. Idempotent: a cached binary is reused without re-download.
    /// </summary>
    /// <exception cref="StableDiffusionRuntimeException">
    ///     The binary could not be acquired (download failure, repeated hash mismatch, no prebuilt for the host, or a
    ///     broken bring-your-own override) — the message is sanitized for direct display.
    /// </exception>
    Task<SdBinary> EnsureBinaryAsync(SdGpuBackend backend, CancellationToken ct);
}
