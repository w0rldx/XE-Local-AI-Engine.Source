namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Runtime.InteropServices;

/// <summary>
///     A single pinned, hash-verified llama.cpp prebuilt asset (one OS/arch/variant combination).
/// </summary>
/// <param name="AssetName">The release asset file name (for example <c>llama-b9692-bin-win-vulkan-x64.zip</c>).</param>
/// <param name="Sha256">Lowercase hex SHA256 the downloaded archive must match.</param>
/// <param name="ServerRelativePath">
///     Path to <c>llama-server</c> inside the extracted archive (Windows archives nest under <c>build/bin/</c>).
/// </param>
public sealed record LlamaCppAssetPin(string AssetName, string Sha256, string ServerRelativePath);

/// <summary>
///     Verified, pinned llama.cpp prebuilt-release table — the recommended-pinned acquisition source for
///     <see cref="LlamaCppBinaryManager" />. No source-build, ever.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Pinned tag <c>b9692</c></strong> (published 2026-06-17). SHA256 digests are taken from the GitHub
///         release-assets API <c>digest</c> field — llama.cpp publishes NO <c>.sha256</c> sidecar files, so the digest
///         API is the source of truth. Re-pin (tag + every hash) when bumping the recommended version.
///     </para>
///     <para>
///         <strong>Asset scheme:</strong> <c>llama-{tag}-bin-{os}-{variant}-{arch}.{ext}</c>; download URL
///         <c>https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{asset}</c>.
///     </para>
///     <para>
///         <strong>Constraint:</strong> llama.cpp ships NO prebuilt Linux CUDA asset — a Linux NVIDIA box selects
///         Vulkan (enforced by <see cref="GpuVariantSelector" />). Windows CUDA also needs the separate
///         <c>cudart-…</c> runtime archive; that second-archive handling is a documented follow-up for the supervisor
///         lane and not modeled in this pin row.
///     </para>
/// </remarks>
public static class LlamaCppReleasePins
{
    /// <summary>The recommended-pinned llama.cpp release tag.</summary>
    public const string PinnedTag = "b9692";

    private const string WindowsServerPath = "build/bin/llama-server.exe";
    private const string UnixServerPath = "build/bin/llama-server";

    // Keyed by (os, arch, variant). Verified against the b9692 release-assets digest API on 2026-06-17.
    private static readonly IReadOnlyDictionary<(OSPlatform Os, Architecture Arch, GpuVariant Variant), LlamaCppAssetPin> Pins =
        new Dictionary<(OSPlatform, Architecture, GpuVariant), LlamaCppAssetPin>
        {
            // Windows x64
            [(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda)] =
                new("llama-b9692-bin-win-cuda-12.4-x64.zip", "a10476e348762d75464a698c2e170e814860f3d5959488cb23234e913ef50bc5", WindowsServerPath),
            [(OSPlatform.Windows, Architecture.X64, GpuVariant.Vulkan)] =
                new("llama-b9692-bin-win-vulkan-x64.zip", "6d241d2e1f5dc351966bc9aca0f50adf230e094706854fc126ca7331b7f478cd", WindowsServerPath),
            [(OSPlatform.Windows, Architecture.X64, GpuVariant.Cpu)] =
                new("llama-b9692-bin-win-cpu-x64.zip", "7a285e595c8c6557b53c91da7be88b9f3ff20826a565547dc08fa6c3588e0994", WindowsServerPath),

            // Windows arm64 (CPU floor only)
            [(OSPlatform.Windows, Architecture.Arm64, GpuVariant.Cpu)] =
                new("llama-b9692-bin-win-cpu-arm64.zip", "66c62a3533e511db334b0ba087efa079c2ea88a01ebe92274d20aef37a92f68b", WindowsServerPath),

            // Linux x64 (no prebuilt CUDA exists upstream)
            [(OSPlatform.Linux, Architecture.X64, GpuVariant.Vulkan)] =
                new("llama-b9692-bin-ubuntu-vulkan-x64.tar.gz", "3c0ebf913bb9b021307b87454485aefdb8961182b637e60b846ea98d53c6274c", UnixServerPath),
            [(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)] =
                new("llama-b9692-bin-ubuntu-x64.tar.gz", "148a3b157ed347eae27ab4a4702be42ad3e8fe180fb604003ac86fb1886fda56", UnixServerPath),

            // Linux arm64
            [(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Vulkan)] =
                new("llama-b9692-bin-ubuntu-vulkan-arm64.tar.gz", "d037cb9383e40166b4b410a3fa732b18d7d8e534d0a4fbdc8890599da1f56505", UnixServerPath),
            [(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Cpu)] =
                new("llama-b9692-bin-ubuntu-arm64.tar.gz", "7ba37e66c01d992ebf3bc3972268d1bdd2c304a0aa382ff0dfe2b98ce7db6bdd", UnixServerPath),

            // macOS (CPU floor; llama.cpp uses Metal at runtime within the universal build)
            [(OSPlatform.OSX, Architecture.Arm64, GpuVariant.Cpu)] =
                new("llama-b9692-bin-macos-arm64.tar.gz", "751c2978074d52288682fa74cc8ffddae039e45a816affccb05b08ea4c40f0be", UnixServerPath),
            [(OSPlatform.OSX, Architecture.X64, GpuVariant.Cpu)] =
                new("llama-b9692-bin-macos-x64.tar.gz", "95ef72d64fa16b40e1aeef59cdb3424f768b31cc9a4b490934bd66e7a6fdd7c8", UnixServerPath)
        };

    /// <summary>Builds the absolute download URL for a named asset in the given release tag.</summary>
    public static Uri DownloadUri(string tag, string assetName)
    {
        return new Uri($"https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{assetName}");
    }

    /// <summary>
    ///     Resolves the pinned asset for the given OS/arch/variant, falling back to the CPU floor when no GPU prebuilt
    ///     exists for the host. Returns <see langword="null" /> only when even the CPU floor is unavailable.
    /// </summary>
    public static LlamaCppAssetPin? Resolve(OSPlatform os, Architecture arch, GpuVariant variant)
    {
        if (Pins.TryGetValue((os, arch, variant), out var pin))
        {
            return pin;
        }

        // Fall back to the universal CPU floor for the host OS/arch.
        return Pins.TryGetValue((os, arch, GpuVariant.Cpu), out var cpuPin) ? cpuPin : null;
    }
}
