namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

using System.Runtime.InteropServices;

/// <summary>
///     A single pinned, hash-verified stable-diffusion.cpp prebuilt asset (one OS/arch/backend combination).
/// </summary>
public sealed class StableDiffusionAssetPin
{
    /// <summary>The release asset file name (for example <c>sd-master-37bc88b-bin-win-vulkan-x64.zip</c>).</summary>
    public required string AssetName { get; init; }

    /// <summary>Lowercase hex SHA256 the downloaded archive must match.</summary>
    public required string Sha256 { get; init; }

    /// <summary>
    ///     Path to <c>sd-server</c> inside the extracted archive. stable-diffusion.cpp ships the executable at the archive
    ///     root (a bare file name, NOT nested under <c>build/bin/</c> the way llama.cpp does).
    /// </summary>
    public required string ServerRelativePath { get; init; }

    /// <summary>
    ///     The companion CUDA-runtime archive name, set ONLY on the Windows x64 CUDA pin; <see langword="null" /> for
    ///     every other pin.
    /// </summary>
    /// <remarks>
    ///     stable-diffusion.cpp ships the CUDA runtime DLLs in a SEPARATE archive from the main build; without them next
    ///     to <c>sd-server.exe</c> the CUDA backend fails to load.
    /// </remarks>
    public string? CudartAssetName { get; init; }

    /// <summary>Lowercase hex SHA256 the companion CUDA-runtime archive must match. <see langword="null" /> when <see cref="CudartAssetName" /> is.</summary>
    public string? CudartSha256 { get; init; }
}

/// <summary>
///     Verified, pinned stable-diffusion.cpp prebuilt-release table — the recommended-pinned acquisition source for
///     <see cref="Implementation.StableDiffusionCppBinaryManager" /> when no managed source-built runtime is selected.
/// </summary>
/// <remarks>
///     Pinned tag <c>master-1028-37bc88b</c> (commit <c>37bc88b</c>), fetched from
///     <c>https://github.com/leejet/stable-diffusion.cpp/releases/download/{tag}/{asset}</c>. The project ships <b>rolling</b>
///     <c>master-&lt;n&gt;-&lt;sha&gt;</c> releases, so re-pin the tag AND every hash when bumping. stable-diffusion.cpp ships NO prebuilt
///     Linux CUDA asset, which <see cref="Implementation.SdGpuBackendSelector" /> enforces. See
///     docs/wiki/14-image-generation.md ("The pinned prebuilt release table").
/// </remarks>
public static class StableDiffusionReleasePins
{
    /// <summary>The recommended-pinned stable-diffusion.cpp rolling release tag.</summary>
    public const string PinnedTag = "master-1028-37bc88b";

    /// <summary>The exact canonical source revision used by official managed builds.</summary>
    public const string PinnedSourceCommitSha = "37bc88b5e97a213821949732ea66c58ae8ffc784";

    // stable-diffusion.cpp ships sd-server at the archive root as a bare file name (no build/bin/ nesting).
    private const string WindowsServerPath = "sd-server.exe";
    private const string UnixServerPath = "sd-server";

    // Keyed by (os, arch, backend). Verified against the master-1028-37bc88b release-assets digest API on 2026-10-30.
    private static readonly IReadOnlyDictionary<PinKey, StableDiffusionAssetPin> Pins =
        new Dictionary<PinKey, StableDiffusionAssetPin>
        {
            // Windows x64 — the CUDA pin also carries its companion runtime archive (cudart-…); both digests are from
            // the master-1028-37bc88b release-assets digest API. The cudart asset name is NOT tag-prefixed upstream.
            [new PinKey(OSPlatform.Windows, Architecture.X64, SdGpuBackend.Cuda)] =
                new()
                {
                    AssetName = "sd-master-37bc88b-bin-win-cuda12-x64.zip",
                    Sha256 = "62fe56041925f4cd1d6ef3f69ec52cae55839efef27f4e2ed4c8b6a82ca31c59",
                    ServerRelativePath = WindowsServerPath,
                    CudartAssetName = "cudart-sd-bin-win-cu12-x64.zip",
                    CudartSha256 = "fe20366827d357c00797eebb58244dddab7fd9a348d70090c3871004c320f38d"
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, SdGpuBackend.Vulkan)] =
                new()
                {
                    AssetName = "sd-master-37bc88b-bin-win-vulkan-x64.zip",
                    Sha256 = "c077a6dbd596f1dbcd3fd05d715a363dea41da1de0e893eaf7fd96e0b4c314f0",
                    ServerRelativePath = WindowsServerPath
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, SdGpuBackend.Cpu)] =
                new()
                {
                    AssetName = "sd-master-37bc88b-bin-win-cpu-x64.zip",
                    Sha256 = "13ed02c2f6bf297a756789b601a0598dcfc6ff7aadffdc5db1f462ad0e390ab0",
                    ServerRelativePath = WindowsServerPath
                },

            // Linux x64 (no prebuilt CUDA exists upstream; managed source builds provide the CUDA lane).
            [new PinKey(OSPlatform.Linux, Architecture.X64, SdGpuBackend.Vulkan)] =
                new()
                {
                    AssetName = "sd-master-37bc88b-bin-Linux-Ubuntu-24.04-x86_64-vulkan.zip",
                    Sha256 = "3e84a0c0e87898bc9aeb71bf4adeb08e89b3d1424d1ab018bca4add907721b37",
                    ServerRelativePath = UnixServerPath
                },
            [new PinKey(OSPlatform.Linux, Architecture.X64, SdGpuBackend.Cpu)] =
                new()
                {
                    AssetName = "sd-master-37bc88b-bin-Linux-Ubuntu-24.04-x86_64.zip",
                    Sha256 = "5a7fefd58b4827a8966c08e10390343af52dbfe0f56ef23e171b457affd4b648",
                    ServerRelativePath = UnixServerPath
                },

            // macOS arm64 (CPU floor; stable-diffusion.cpp uses Metal at runtime within the universal build).
            [new PinKey(OSPlatform.OSX, Architecture.Arm64, SdGpuBackend.Cpu)] =
                new()
                {
                    AssetName = "sd-master-37bc88b-bin-Darwin-macOS-26.6.2-arm64.zip",
                    Sha256 = "d79e0771c724c265ca11ca313fa87008b1977aa11ea84940e47925f16f64c9c9",
                    ServerRelativePath = UnixServerPath
                }
        };

    /// <summary>Builds the absolute download URL for a named asset in the given release tag.</summary>
    public static Uri DownloadUri(string tag, string assetName)
    {
        return new Uri($"https://github.com/leejet/stable-diffusion.cpp/releases/download/{tag}/{assetName}");
    }

    /// <summary>
    ///     Resolves the pinned asset for the given OS/arch/backend, degrading to the CPU floor when no GPU prebuilt
    ///     exists for the host; <see langword="null" /> only when even the CPU floor is unavailable.
    /// </summary>
    /// <remarks>
    ///     Runtime acquisition takes that degrade only for an explicit CPU selection; GPU selections must use
    ///     <see cref="ResolveExact" /> so the resolved bytes cannot contradict the requested backend.
    /// </remarks>
    public static StableDiffusionAssetPin? Resolve(OSPlatform os, Architecture arch, SdGpuBackend backend)
    {
        if (Pins.TryGetValue(new PinKey(os, arch, backend), out var pin))
        {
            return pin;
        }

        // Fall back to the universal CPU floor for the host OS/arch.
        return Pins.TryGetValue(new PinKey(os, arch, SdGpuBackend.Cpu), out var cpuPin) ? cpuPin : null;
    }

    /// <summary>
    ///     Resolves only the exact OS/arch/backend asset. Unlike <see cref="Resolve"/>, a missing GPU prebuilt never
    ///     substitutes the CPU floor.
    /// </summary>
    public static StableDiffusionAssetPin? ResolveExact(OSPlatform os, Architecture arch, SdGpuBackend backend)
    {
        return Pins.TryGetValue(new PinKey(os, arch, backend), out var pin) ? pin : null;
    }

    /// <summary>The host shape one prebuilt asset is pinned for.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PinKey(OSPlatform Os, Architecture Arch, SdGpuBackend Backend);
}
