namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp;

using System.Runtime.InteropServices;

/// <summary>
///     A single pinned, hash-verified stable-diffusion.cpp prebuilt asset (one OS/arch/backend combination).
/// </summary>
public sealed class StableDiffusionAssetPin
{
    /// <summary>The release asset file name (for example <c>sd-master-b167b94-bin-win-vulkan-x64.zip</c>).</summary>
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
///     Pinned tag <c>master-913-b167b94</c> (commit <c>b167b94</c>), fetched from
///     <c>https://github.com/leejet/stable-diffusion.cpp/releases/download/{tag}/{asset}</c>. The project ships <b>rolling</b>
///     <c>master-&lt;n&gt;-&lt;sha&gt;</c> releases, so re-pin the tag AND every hash when bumping. stable-diffusion.cpp ships NO prebuilt
///     Linux CUDA asset, which <see cref="Implementation.SdGpuBackendSelector" /> enforces. See
///     docs/wiki/14-image-generation.md ("The pinned prebuilt release table").
/// </remarks>
public static class StableDiffusionReleasePins
{
    /// <summary>The recommended-pinned stable-diffusion.cpp rolling release tag.</summary>
    public const string PinnedTag = "master-913-b167b94";

    /// <summary>The exact canonical source revision used by official managed builds.</summary>
    public const string PinnedSourceCommitSha = "b167b942f77ecb17e7f78e163a8c32ff7ac95c10";

    // stable-diffusion.cpp ships sd-server at the archive root as a bare file name (no build/bin/ nesting).
    private const string WindowsServerPath = "sd-server.exe";
    private const string UnixServerPath = "sd-server";

    // Keyed by (os, arch, backend). Verified against the master-913-b167b94 release-assets digest API on 2026-09-25.
    private static readonly IReadOnlyDictionary<PinKey, StableDiffusionAssetPin> Pins =
        new Dictionary<PinKey, StableDiffusionAssetPin>
        {
            // Windows x64 — the CUDA pin also carries its companion runtime archive (cudart-…); both digests are from
            // the master-913-b167b94 release-assets digest API. The cudart asset name is NOT tag-prefixed upstream.
            [new PinKey(OSPlatform.Windows, Architecture.X64, SdGpuBackend.Cuda)] =
                new()
                {
                    AssetName = "sd-master-b167b94-bin-win-cuda12-x64.zip",
                    Sha256 = "4d3977d84bfea5dc4118a583073cf71ffd9db1f0c888de7e5644007db6629190",
                    ServerRelativePath = WindowsServerPath,
                    CudartAssetName = "cudart-sd-bin-win-cu12-x64.zip",
                    CudartSha256 = "fe20366827d357c00797eebb58244dddab7fd9a348d70090c3871004c320f38d"
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, SdGpuBackend.Vulkan)] =
                new()
                {
                    AssetName = "sd-master-b167b94-bin-win-vulkan-x64.zip",
                    Sha256 = "8148cfc825a6a0fae98b72b495927b9ba75fb8666b8d15400c6bbca793b361c9",
                    ServerRelativePath = WindowsServerPath
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, SdGpuBackend.Cpu)] =
                new()
                {
                    AssetName = "sd-master-b167b94-bin-win-cpu-x64.zip",
                    Sha256 = "907709fc8c1af748616afc83111c2a3e43cf28fdc10c668b05b51515d2c86a6c",
                    ServerRelativePath = WindowsServerPath
                },

            // Linux x64 (no prebuilt CUDA exists upstream; managed source builds provide the CUDA lane).
            [new PinKey(OSPlatform.Linux, Architecture.X64, SdGpuBackend.Vulkan)] =
                new()
                {
                    AssetName = "sd-master-b167b94-bin-Linux-Ubuntu-24.04-x86_64-vulkan.zip",
                    Sha256 = "dd4478f9ab3e73adc215f3bb29456f8a9adf90142200a5da156ab7bc96bbfbe0",
                    ServerRelativePath = UnixServerPath
                },
            [new PinKey(OSPlatform.Linux, Architecture.X64, SdGpuBackend.Cpu)] =
                new()
                {
                    AssetName = "sd-master-b167b94-bin-Linux-Ubuntu-24.04-x86_64.zip",
                    Sha256 = "e6036c1f5c19be44694fad4f4292a4c49320d15cf9be97430c56aadf1192c342",
                    ServerRelativePath = UnixServerPath
                },

            // macOS arm64 (CPU floor; stable-diffusion.cpp uses Metal at runtime within the universal build).
            [new PinKey(OSPlatform.OSX, Architecture.Arm64, SdGpuBackend.Cpu)] =
                new()
                {
                    AssetName = "sd-master-b167b94-bin-Darwin-macOS-26.6.2-arm64.zip",
                    Sha256 = "f1b872bb32be3c9d69dac8188cc07ea72e054643cb5c8b33ac53c86351d11342",
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
