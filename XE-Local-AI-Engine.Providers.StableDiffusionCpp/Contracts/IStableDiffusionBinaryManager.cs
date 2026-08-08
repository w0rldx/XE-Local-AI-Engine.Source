namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Resolves a validated managed stable-diffusion.cpp runtime when selected; otherwise downloads, hash-verifies, and
///     caches the exact pinned <c>sd-server</c> prebuilt for the host/backend. Source compilation is delegated to the
///     managed source-build service. Mirrors <c>ILlamaCppBinaryManager</c> for the image runtime.
/// </summary>
/// <remarks>
///     Responsibility: operator override → authoritative installed managed runtime → exact prebuilt release asset for
///     the OS/arch + requested <see cref="SdGpuBackend" /> → download via <see cref="HttpClient" /> → verify SHA256
///     against the pinned hash (corrupt → re-download once then surface a sanitized error) → cache under a stable app
///     dir → offline uses the cached pinned binary.
/// </remarks>
public interface IStableDiffusionBinaryManager
{
    /// <summary>
    ///     Ensures a validated <c>sd-server</c> binary for <paramref name="backend" /> is present on disk and returns its
    ///     resolved location. Idempotent: a valid managed runtime or cached prebuilt is reused.
    /// </summary>
    /// <exception cref="StableDiffusionRuntimeException">
    ///     The binary could not be acquired (download failure, repeated hash mismatch, no prebuilt for the host, or a
    ///     broken bring-your-own override) — the message is sanitized for direct display.
    /// </exception>
    Task<SdBinary> EnsureBinaryAsync(SdGpuBackend backend, CancellationToken ct);
}
