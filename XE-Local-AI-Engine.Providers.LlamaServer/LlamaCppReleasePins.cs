namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Runtime.InteropServices;

/// <summary>
///     A single pinned, hash-verified llama.cpp prebuilt asset (one OS/arch/variant combination).
/// </summary>
/// <param name="AssetName">The release asset file name (for example <c>llama-b10201-bin-win-vulkan-x64.zip</c>).</param>
/// <param name="Sha256">Lowercase hex SHA256 the downloaded archive must match.</param>
/// <param name="ServerRelativePath">
///     Path to <c>llama-server</c> inside the extracted archive (Windows archives nest under <c>build/bin/</c>).
/// </param>
/// <param name="CudartAssetName">
///     The companion CUDA-runtime archive name, set ONLY on the Windows x64 CUDA pin. llama.cpp ships the CUDA runtime
///     DLLs (<c>cudart64_*.dll</c>, <c>cublas64_*.dll</c>, <c>cublasLt64_*.dll</c>) in a SEPARATE archive from the main
///     build; without them next to <c>llama-server.exe</c> the ggml-cuda backend fails to load and the server silently
///     runs CPU-only. <see langword="null" /> for every non-Windows-CUDA pin (no second archive to fetch).
/// </param>
/// <param name="CudartSha256">Lowercase hex SHA256 the companion CUDA-runtime archive must match. <see langword="null" /> when <paramref name="CudartAssetName" /> is.</param>
public sealed record LlamaCppAssetPin(
    string AssetName,
    string Sha256,
    string ServerRelativePath,
    string? CudartAssetName = null,
    string? CudartSha256 = null);

/// <summary>
///     Verified, pinned llama.cpp prebuilt-release table — the recommended-pinned acquisition source for
///     <see cref="LlamaCppBinaryManager" />. No source-build, ever.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Pinned tag <c>b10201</c></strong> (published 2026-07-31). SHA256 digests are taken from the GitHub
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
///         <c>cudart-…</c> runtime archive; the Windows-CUDA pin row carries it as
///         <see cref="LlamaCppAssetPin.CudartAssetName" />/<see cref="LlamaCppAssetPin.CudartSha256" /> and
///         <see cref="LlamaCppBinaryManager" /> fetches it alongside the main archive.
///     </para>
/// </remarks>
public static class LlamaCppReleasePins
{
    /// <summary>The recommended-pinned llama.cpp release tag.</summary>
    public const string PinnedTag = "b10201";

    /// <summary>
    ///     The exact upstream commit SHA the <see cref="PinnedTag" /> tag (<c>b10201</c>) resolves to on
    ///     <c>ggml-org/llama.cpp</c>. The in-app CUDA source build verifies the freshly-cloned tree's checked-out
    ///     <c>HEAD</c> equals this and HARD-FAILS before any cmake runs, so a moved tag / hijacked ref can never be built.
    ///     Re-pin this alongside <see cref="PinnedTag" /> when bumping the recommended version
    ///     (<c>git ls-remote https://github.com/ggml-org/llama.cpp refs/tags/&lt;tag&gt;</c>). <c>[secHIGH-1]</c>
    /// </summary>
    public const string PinnedCudaSourceCommitSha = "8f4646a63ee29f2e0ab971b0290b141938769762";

    /// <summary>Backend-neutral alias for the exact source commit behind <see cref="PinnedTag" />.</summary>
    public const string PinnedSourceCommitSha = PinnedCudaSourceCommitSha;

    private const string WindowsServerPath = "build/bin/llama-server.exe";
    private const string UnixServerPath = "build/bin/llama-server";

    // Keyed by (os, arch, variant). Verified against the b10201 release-assets digest API on 2026-07-31.
    private static readonly IReadOnlyDictionary<(OSPlatform Os, Architecture Arch, GpuVariant Variant), LlamaCppAssetPin> Pins =
        new Dictionary<(OSPlatform, Architecture, GpuVariant), LlamaCppAssetPin>
        {
            // Windows x64 — the CUDA pin also carries its companion runtime archive (cudart-…); both digests are from
            // the b10201 release-assets digest API. The cudart asset name is NOT tag-prefixed upstream, and its digest is
            // unchanged from b9692 because upstream ships the same CUDA 12.4 runtime archive across those releases.
            [(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda)] =
                new("llama-b10201-bin-win-cuda-12.4-x64.zip", "0b25fa35df1acb01a7bf0325fe554cbae1d7be39dfd630e979ad5a5ddc66599b", WindowsServerPath,
                    CudartAssetName: "cudart-llama-bin-win-cuda-12.4-x64.zip",
                    CudartSha256: "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6"),
            [(OSPlatform.Windows, Architecture.X64, GpuVariant.Vulkan)] =
                new("llama-b10201-bin-win-vulkan-x64.zip", "7284f987944f0700b0d039b1b2f786302308f1479b8f1d61efa0a0ba35acea42", WindowsServerPath),
            [(OSPlatform.Windows, Architecture.X64, GpuVariant.Cpu)] =
                new("llama-b10201-bin-win-cpu-x64.zip", "8b8d4f0f6738e11842dd5250de0736052de41b0ef4de8d3fb119c37335de2833", WindowsServerPath),

            // Windows arm64 (CPU floor only)
            [(OSPlatform.Windows, Architecture.Arm64, GpuVariant.Cpu)] =
                new("llama-b10201-bin-win-cpu-arm64.zip", "e1b97a489cb66d04f92f53d2c633ede0721c4e26dc0bfdcf7ec6f4e31091f1a8", WindowsServerPath),

            // Linux x64 (no prebuilt CUDA exists upstream)
            [(OSPlatform.Linux, Architecture.X64, GpuVariant.Vulkan)] =
                new("llama-b10201-bin-ubuntu-vulkan-x64.tar.gz", "ac495ca88439c0218a226b01120526aa051ed5adaacc6abe207c753931b03a57", UnixServerPath),
            [(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)] =
                new("llama-b10201-bin-ubuntu-x64.tar.gz", "7a985be324ebbce0de698fe34e830990838fce13ddb90a92c7f87ea2532ba797", UnixServerPath),

            // Linux arm64
            [(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Vulkan)] =
                new("llama-b10201-bin-ubuntu-vulkan-arm64.tar.gz", "5e350769055d053a204b9d4479af560b3b8e9c71729bb1da8b03f2aa70d19533", UnixServerPath),
            [(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Cpu)] =
                new("llama-b10201-bin-ubuntu-arm64.tar.gz", "8e001059da48a95bfb17ebab2d7e118ad15878b69840458d7cbbf443591af9e7", UnixServerPath),

            // macOS (CPU floor; llama.cpp uses Metal at runtime within the universal build)
            [(OSPlatform.OSX, Architecture.Arm64, GpuVariant.Cpu)] =
                new("llama-b10201-bin-macos-arm64.tar.gz", "4c323231709d670d7282ed1efdc4b479831305d7e8a6ba3c18bb0cff2dae401e", UnixServerPath),
            [(OSPlatform.OSX, Architecture.X64, GpuVariant.Cpu)] =
                new("llama-b10201-bin-macos-x64.tar.gz", "ab3f2f59dbc06914dcceb062a8aef8f56bf303eacfa008cc27f8fb0e9206e1bf", UnixServerPath)
        };

    /// <summary>Builds the absolute download URL for a named asset in the given release tag.</summary>
    public static Uri DownloadUri(string tag, string assetName)
    {
        return new Uri($"https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{assetName}");
    }

    /// <summary>
    ///     Derives the companion CUDA-runtime archive name from a Windows-CUDA main asset name. The main asset is
    ///     <c>llama-{tag}-bin-win-cuda-{ver}-x64.zip</c>; its cudart companion is <c>cudart-llama-bin-win-cuda-{ver}-x64.zip</c>
    ///     (the cudart name is NOT tag-prefixed). Returns <see langword="null" /> for any name that is not a Windows-CUDA
    ///     main asset, so only the Windows-CUDA acquisition path ever pairs a second archive.
    /// </summary>
    public static string? DeriveCudartAssetName(string? mainAssetName)
    {
        if (string.IsNullOrWhiteSpace(mainAssetName))
        {
            return null;
        }

        const string prefix = "llama-";
        const string suffix = "-bin-win-cuda-";
        var bin = mainAssetName.IndexOf(suffix, StringComparison.Ordinal);
        if (!mainAssetName.StartsWith(prefix, StringComparison.Ordinal)
            || bin < 0
            || !mainAssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Keep everything from "-bin-win-cuda-" onward (carries the CUDA version + "-x64.zip"); re-prefix with "cudart-llama".
        var fromBin = mainAssetName[bin..];
        return $"cudart-llama{fromBin}";
    }

    /// <summary>
    ///     Resolves the pinned asset for the given OS/arch/variant, falling back to the CPU floor when no GPU prebuilt
    ///     exists for the host. Returns <see langword="null" /> only when even the CPU floor is unavailable.
    ///     <para>
    ///         <b>Caution:</b> a GPU-variant request whose (os, arch, variant) has no prebuilt (e.g. Linux CUDA) returns
    ///         the CPU floor pin here — a non-null CPU archive. Serving that as a GPU-variant binary would mislabel a CPU
    ///         build (the supervisor then emits GPU placement flags against it). A caller acquiring a GPU variant must use
    ///         <see cref="TryResolveExact" /> and treat a null result as "no prebuilt", never fall through to this floor.
    ///     </para>
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

    /// <summary>
    ///     Resolves the pin for EXACTLY the given (os, arch, variant) with NO CPU-floor fallback — returns
    ///     <see langword="null" /> when no genuine prebuilt asset exists for that precise combination. This is the
    ///     acquisition path for a GPU variant: unlike <see cref="Resolve" />, it never substitutes the CPU
    ///     archive, so a Linux CUDA request (which has no upstream prebuilt) resolves to null and the binary manager fails
    ///     with the sanitized "no prebuilt" error instead of serving a CPU build stamped as CUDA.
    /// </summary>
    public static LlamaCppAssetPin? TryResolveExact(OSPlatform os, Architecture arch, GpuVariant variant)
    {
        return Pins.TryGetValue((os, arch, variant), out var pin) ? pin : null;
    }
}
