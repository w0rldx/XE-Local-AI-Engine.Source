namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

using System.Runtime.InteropServices;

/// <summary>
///     A single pinned, hash-verified stable-diffusion.cpp prebuilt asset (one OS/arch/backend combination).
/// </summary>
/// <param name="AssetName">The release asset file name (for example <c>sd-master-1a13107-bin-win-vulkan-x64.zip</c>).</param>
/// <param name="Sha256">Lowercase hex SHA256 the downloaded archive must match.</param>
/// <param name="ServerRelativePath">
///     Path to <c>sd-server</c> inside the extracted archive. stable-diffusion.cpp ships the executable at the archive
///     root (a bare file name, NOT nested under <c>build/bin/</c> the way llama.cpp does).
/// </param>
/// <param name="CudartAssetName">
///     The companion CUDA-runtime archive name, set ONLY on the Windows x64 CUDA pin. stable-diffusion.cpp ships the
///     CUDA runtime DLLs in a SEPARATE archive from the main build; without them next to <c>sd-server.exe</c> the CUDA
///     backend fails to load. <see langword="null" /> for every non-Windows-CUDA pin.
/// </param>
/// <param name="CudartSha256">Lowercase hex SHA256 the companion CUDA-runtime archive must match. <see langword="null" /> when <paramref name="CudartAssetName" /> is.</param>
public sealed record StableDiffusionAssetPin(
    string AssetName,
    string Sha256,
    string ServerRelativePath,
    string? CudartAssetName = null,
    string? CudartSha256 = null);

/// <summary>
///     Verified, pinned stable-diffusion.cpp prebuilt-release table — the recommended-pinned acquisition source for
///     <see cref="Implementation.StableDiffusionCppBinaryManager" />. No source-build, ever.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Pinned tag <c>master-742-1a13107</c></strong> (spike-frozen 2026-07-01, commit <c>1a13107</c>).
///         SHA256 digests are taken from the GitHub release-assets API <c>digest</c> field — stable-diffusion.cpp
///         publishes NO <c>.sha256</c> sidecar files, so the digest API is the source of truth. The project ships
///         <b>rolling</b> <c>master-&lt;n&gt;-&lt;sha&gt;</c> releases (no semver, moves daily); re-pin (tag + every hash)
///         when bumping the recommended version.
///     </para>
///     <para>
///         <strong>Download URL:</strong>
///         <c>https://github.com/leejet/stable-diffusion.cpp/releases/download/{tag}/{asset}</c>.
///     </para>
///     <para>
///         <strong>Constraint:</strong> stable-diffusion.cpp ships NO prebuilt Linux CUDA asset — a Linux NVIDIA box
///         selects Vulkan (enforced by <see cref="Implementation.SdGpuBackendSelector" />). Windows CUDA also needs the
///         separate <c>cudart-…</c> runtime archive; the Windows-CUDA pin row carries it as
///         <see cref="StableDiffusionAssetPin.CudartAssetName" />/<see cref="StableDiffusionAssetPin.CudartSha256" />.
///     </para>
/// </remarks>
public static class StableDiffusionReleasePins
{
    /// <summary>The recommended-pinned stable-diffusion.cpp rolling release tag.</summary>
    public const string PinnedTag = "master-742-1a13107";

    // stable-diffusion.cpp ships sd-server at the archive root as a bare file name (no build/bin/ nesting).
    private const string WindowsServerPath = "sd-server.exe";
    private const string UnixServerPath = "sd-server";

    // Keyed by (os, arch, backend). Verified against the master-742-1a13107 release-assets digest API on 2026-07-01.
    private static readonly IReadOnlyDictionary<(OSPlatform Os, Architecture Arch, SdGpuBackend Backend), StableDiffusionAssetPin> Pins =
        new Dictionary<(OSPlatform, Architecture, SdGpuBackend), StableDiffusionAssetPin>
        {
            // Windows x64 — the CUDA pin also carries its companion runtime archive (cudart-…); both digests are from
            // the master-742-1a13107 release-assets digest API. The cudart asset name is NOT tag-prefixed upstream.
            [(OSPlatform.Windows, Architecture.X64, SdGpuBackend.Cuda)] =
                new("sd-master-1a13107-bin-win-cuda12-x64.zip", "86ae82bd9fa53f703b426d7c1853f53de3b3ff8efccc426ffab2a037e1a230b2", WindowsServerPath,
                    CudartAssetName: "cudart-sd-bin-win-cu12-x64.zip",
                    CudartSha256: "fe20366827d357c00797eebb58244dddab7fd9a348d70090c3871004c320f38d"),
            [(OSPlatform.Windows, Architecture.X64, SdGpuBackend.Vulkan)] =
                new("sd-master-1a13107-bin-win-vulkan-x64.zip", "a72ff5a59b45438e55626868c2ba47417d419c4c4dabb7b6f0ee7d46a353dfea", WindowsServerPath),
            [(OSPlatform.Windows, Architecture.X64, SdGpuBackend.Cpu)] =
                new("sd-master-1a13107-bin-win-cpu-x64.zip", "9fb05b3e4544294126bfd8b4ce4100e72b31129a57215433f992796acc0df08f", WindowsServerPath),

            // Linux x64 (no prebuilt CUDA exists upstream — a Linux NVIDIA box falls back to Vulkan).
            [(OSPlatform.Linux, Architecture.X64, SdGpuBackend.Vulkan)] =
                new("sd-master-1a13107-bin-Linux-Ubuntu-24.04-x86_64-vulkan.zip", "c29937b7d12d09d5d18295894d998d09aa73b17fec792c833683cf1a88f35add", UnixServerPath),
            [(OSPlatform.Linux, Architecture.X64, SdGpuBackend.Cpu)] =
                new("sd-master-1a13107-bin-Linux-Ubuntu-24.04-x86_64.zip", "7da69c45f33c91e0802daf3d2195d174503dd448809569bac173a4e76301ddf5", UnixServerPath),

            // macOS arm64 (CPU floor; stable-diffusion.cpp uses Metal at runtime within the universal build).
            [(OSPlatform.OSX, Architecture.Arm64, SdGpuBackend.Cpu)] =
                new("sd-master-1a13107-bin-Darwin-macOS-15.7.7-arm64.zip", "f8c3a7f0c32b3ca786f3fcbef016b53376dba7bb49d5d4b39932ae5746c5562b", UnixServerPath)
        };

    /// <summary>Builds the absolute download URL for a named asset in the given release tag.</summary>
    public static Uri DownloadUri(string tag, string assetName)
    {
        return new Uri($"https://github.com/leejet/stable-diffusion.cpp/releases/download/{tag}/{assetName}");
    }

    /// <summary>
    ///     Resolves the pinned asset for the given OS/arch/backend, falling back to the CPU floor when no GPU prebuilt
    ///     exists for the host. Returns <see langword="null" /> only when even the CPU floor is unavailable.
    /// </summary>
    public static StableDiffusionAssetPin? Resolve(OSPlatform os, Architecture arch, SdGpuBackend backend)
    {
        if (Pins.TryGetValue((os, arch, backend), out var pin))
        {
            return pin;
        }

        // Fall back to the universal CPU floor for the host OS/arch.
        return Pins.TryGetValue((os, arch, SdGpuBackend.Cpu), out var cpuPin) ? cpuPin : null;
    }
}
