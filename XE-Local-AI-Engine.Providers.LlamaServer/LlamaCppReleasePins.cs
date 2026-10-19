namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Runtime.InteropServices;

/// <summary>
///     A single pinned, hash-verified llama.cpp prebuilt asset (one OS/arch/variant combination).
/// </summary>
public sealed class LlamaCppAssetPin
{
    /// <summary>The release asset file name (for example <c>llama-b11669-bin-win-vulkan-x64.zip</c>).</summary>
    public required string AssetName { get; init; }

    /// <summary>Lowercase hex SHA256 the downloaded archive must match.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Path to <c>llama-server</c> inside the extracted archive (Windows archives nest under <c>build/bin/</c>).</summary>
    public required string ServerRelativePath { get; init; }

    /// <summary>
    ///     The companion CUDA-runtime archive name, set ONLY on the Windows x64 CUDA pin and <see langword="null" /> on
    ///     every other pin, which has no second archive to fetch.
    /// </summary>
    /// <remarks>
    ///     llama.cpp ships the CUDA runtime DLLs (<c>cudart64_*.dll</c>, <c>cublas64_*.dll</c>,
    ///     <c>cublasLt64_*.dll</c>) in a SEPARATE archive from the main build; without them next to
    ///     <c>llama-server.exe</c> the ggml-cuda backend fails to load and the server silently runs CPU-only.
    /// </remarks>
    public string? CudartAssetName { get; init; }

    /// <summary>Lowercase hex SHA256 the companion CUDA-runtime archive must match. <see langword="null" /> when <see cref="CudartAssetName" /> is.</summary>
    public string? CudartSha256 { get; init; }
}

/// <summary>
///     Verified, pinned llama.cpp prebuilt-release table — the recommended-pinned acquisition source for
///     <see cref="Implementation.LlamaCppBinaryManager" />. No source-build, ever.
/// </summary>
/// <remarks>
///     SHA256 digests come from the GitHub release-assets API <c>digest</c> field, because llama.cpp publishes NO
///     <c>.sha256</c> sidecar files; re-pin <see cref="PinnedTag" /> and every hash together when bumping the
///     recommended version. Assets are named <c>llama-{tag}-bin-{os}-{variant}-{arch}.{ext}</c> and downloaded from
///     <c>https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{asset}</c>. Upstream ships NO prebuilt Linux
///     CUDA asset, so a Linux NVIDIA machine selects Vulkan — enforced by <see cref="Implementation.GpuVariantSelector" />.
/// </remarks>
public static class LlamaCppReleasePins
{
    /// <summary>The recommended-pinned llama.cpp release tag.</summary>
    public const string PinnedTag = "b11669";

    /// <summary>
    ///     The exact upstream commit SHA <see cref="PinnedTag" /> resolves to on <c>ggml-org/llama.cpp</c>.
    ///     <c>[secHIGH-1]</c>
    /// </summary>
    /// <remarks>
    ///     The in-app CUDA source build verifies the freshly-cloned tree's checked-out <c>HEAD</c> equals this and
    ///     HARD-FAILS before any cmake runs, so a moved tag or hijacked ref can never be built. Re-pin it alongside
    ///     <see cref="PinnedTag" /> when bumping the recommended version
    ///     (<c>git ls-remote https://github.com/ggml-org/llama.cpp refs/tags/&lt;tag&gt;</c>).
    /// </remarks>
    public const string PinnedCudaSourceCommitSha = "620d2225cc436ab0b001e3200e590e3b286ef9ab";

    /// <summary>Backend-neutral alias for the exact source commit behind <see cref="PinnedTag" />.</summary>
    public const string PinnedSourceCommitSha = PinnedCudaSourceCommitSha;

    private const string WindowsServerPath = "build/bin/llama-server.exe";
    private const string UnixServerPath = "build/bin/llama-server";

    // Keyed by (os, arch, variant). Verified against the b11669 release-assets digest API on 2026-10-19.
    private static readonly IReadOnlyDictionary<PinKey, LlamaCppAssetPin> Pins =
        new Dictionary<PinKey, LlamaCppAssetPin>
        {
            // Windows x64 — the CUDA pin also carries its companion runtime archive; both digests come from the release-assets digest API. The cudart asset name is NOT
            // tag-prefixed upstream, and its digest is unchanged from b9692 through b11669, upstream shipping the same CUDA 12.4 runtime archive across those releases.
            [new PinKey(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda)] =
                new()
                {
                    AssetName = "llama-b11669-bin-win-cuda-12.4-x64.zip",
                    Sha256 = "c2da1f73ed176ae093ac5aceb978a7b167a16113f6d74c6789eeaee2135cb8dc",
                    ServerRelativePath = WindowsServerPath,
                    CudartAssetName = "cudart-llama-bin-win-cuda-12.4-x64.zip",
                    CudartSha256 = "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6"
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, GpuVariant.Vulkan)] =
                new()
                {
                    AssetName = "llama-b11669-bin-win-vulkan-x64.zip",
                    Sha256 = "2c07574e7bde71321cb785282b20cc821e951132149eeceeea32ddebdbf0598e",
                    ServerRelativePath = WindowsServerPath
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11669-bin-win-cpu-x64.zip",
                    Sha256 = "37b3efcf962847ca869745fd4ccf7f5e7e8dacfeff9fa3523b4ca8cdc66d632b",
                    ServerRelativePath = WindowsServerPath
                },

            // Windows arm64 (CPU floor only)
            [new PinKey(OSPlatform.Windows, Architecture.Arm64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11669-bin-win-cpu-arm64.zip",
                    Sha256 = "9cff39ffed17edcd03a7d876073f74d7b48cd43a1e5eafdd59ee094185ad871b",
                    ServerRelativePath = WindowsServerPath
                },

            // Linux x64 (no prebuilt CUDA exists upstream)
            [new PinKey(OSPlatform.Linux, Architecture.X64, GpuVariant.Vulkan)] =
                new()
                {
                    AssetName = "llama-b11669-bin-ubuntu-vulkan-x64.tar.gz",
                    Sha256 = "e8709e5ba6c1b093dea7f3de57f4aba842f9a188fba6b465782d65d6fcfa4d9f",
                    ServerRelativePath = UnixServerPath
                },
            [new PinKey(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11669-bin-ubuntu-x64.tar.gz",
                    Sha256 = "cf07c1c112d709bd67e522017471de57ed0ec8bea031d732c5422351c07f65ac",
                    ServerRelativePath = UnixServerPath
                },

            // Linux arm64
            [new PinKey(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Vulkan)] =
                new()
                {
                    AssetName = "llama-b11669-bin-ubuntu-vulkan-arm64.tar.gz",
                    Sha256 = "d283522e479c0d532e3a04d715dd852ffb2d95925caa6d41ef2ba53a65e46a51",
                    ServerRelativePath = UnixServerPath
                },
            [new PinKey(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11669-bin-ubuntu-arm64.tar.gz",
                    Sha256 = "9ac6fda2058d35677608a06f751a50122ddfa4ce0ddb7f084466b8e79b6d4a99",
                    ServerRelativePath = UnixServerPath
                },

            // macOS (CPU floor; llama.cpp uses Metal at runtime within the universal build)
            [new PinKey(OSPlatform.OSX, Architecture.Arm64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11669-bin-macos-arm64.tar.gz",
                    Sha256 = "dbed55493d72bc1ba2babc8770d669c2289b5bc4ee9fb2a4dfc158d45b7d13fd",
                    ServerRelativePath = UnixServerPath
                },
            [new PinKey(OSPlatform.OSX, Architecture.X64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11669-bin-macos-x64.tar.gz",
                    Sha256 = "cab28238d78158109b48deb27b05d50e0dcef56275d2e76da9ca8281526c7532",
                    ServerRelativePath = UnixServerPath
                }
        };

    /// <summary>Builds the absolute download URL for a named asset in the given release tag.</summary>
    public static Uri DownloadUri(string tag, string assetName)
    {
        return new Uri($"https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{assetName}");
    }

    /// <summary>
    ///     Derives the companion CUDA-runtime archive name from a Windows-CUDA main asset name, or
    ///     <see langword="null" /> for any name that is not one.
    /// </summary>
    /// <remarks>
    ///     The main asset is <c>llama-{tag}-bin-win-cuda-{ver}-x64.zip</c> and its cudart companion
    ///     <c>cudart-llama-bin-win-cuda-{ver}-x64.zip</c> — the cudart name is NOT tag-prefixed. Returning null for
    ///     anything else is what keeps a second archive on the Windows-CUDA acquisition path alone.
    /// </remarks>
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
    ///     exists for the host; <see langword="null" /> only when even the CPU floor is unavailable.
    /// </summary>
    /// <remarks>
    ///     CAUTION: a GPU-variant request whose (os, arch, variant) has no prebuilt — Linux CUDA — returns the CPU
    ///     floor pin here, a non-null CPU archive, and serving that as a GPU-variant binary would mislabel a CPU build
    ///     the supervisor then emits GPU placement flags against. A caller acquiring a GPU variant must use
    ///     <see cref="TryResolveExact" /> and treat null as "no prebuilt", never fall through to this floor.
    /// </remarks>
    public static LlamaCppAssetPin? Resolve(OSPlatform os, Architecture arch, GpuVariant variant)
    {
        if (Pins.TryGetValue(new PinKey(os, arch, variant), out var pin))
        {
            return pin;
        }

        // Fall back to the universal CPU floor for the host OS/arch.
        return Pins.TryGetValue(new PinKey(os, arch, GpuVariant.Cpu), out var cpuPin) ? cpuPin : null;
    }

    /// <summary>
    ///     Resolves the pin for EXACTLY the given (os, arch, variant) with NO CPU-floor fallback, returning
    ///     <see langword="null" /> when no genuine prebuilt asset exists for that precise combination.
    /// </summary>
    /// <remarks>
    ///     This is the acquisition path for a GPU variant: unlike <see cref="Resolve" /> it never substitutes the CPU
    ///     archive, so a Linux CUDA request — which has no upstream prebuilt — resolves to null and the binary manager
    ///     fails with the sanitized "no prebuilt" error instead of serving a CPU build stamped as CUDA.
    /// </remarks>
    public static LlamaCppAssetPin? TryResolveExact(OSPlatform os, Architecture arch, GpuVariant variant)
    {
        return Pins.TryGetValue(new PinKey(os, arch, variant), out var pin) ? pin : null;
    }

    /// <summary>
    ///     Resolves the pin an ACQUISITION of <paramref name="variant" /> may use: the CPU floor for a CPU request, the
    ///     exact GPU pin otherwise, <see langword="null" /> when no genuine prebuilt exists.
    /// </summary>
    /// <remarks>
    ///     Every acquisition path (ensure, install, catalog asset matching) routes through this so none can install the
    ///     CPU floor under a GPU label; a null answer is reported with <see cref="MissingPrebuiltMessage" />.
    /// </remarks>
    public static LlamaCppAssetPin? ResolveForAcquisition(OSPlatform os, Architecture arch, GpuVariant variant)
    {
        return variant == GpuVariant.Cpu ? Resolve(os, arch, variant) : TryResolveExact(os, arch, variant);
    }

    /// <summary>
    ///     The user-safe refusal for a request <see cref="ResolveForAcquisition" /> answers null: it names the variant and
    ///     host and points at the in-app source build and the prebuilt variants that do exist.
    /// </summary>
    public static string MissingPrebuiltMessage(OSPlatform os, Architecture arch, GpuVariant variant)
    {
        var alternatives = Pins.Keys
                               .Where(key => key.Os == os && key.Arch == arch && key.Variant != variant)
                               .Select(key => VariantDisplayName(key.Variant))
                               .Order(StringComparer.Ordinal)
                               .ToList();
        if (alternatives.Count == 0)
        {
            return "No prebuilt llama.cpp runtime is available for this operating system and CPU architecture.";
        }

        var osName = "Linux";
        if (os == OSPlatform.Windows)
        {
            osName = "Windows";
        }
        else if (os == OSPlatform.OSX)
        {
            osName = "macOS";
        }

        var archName = arch == Architecture.Arm64 ? "arm64" : "x64";
        return $"llama.cpp publishes no prebuilt {VariantDisplayName(variant)} build for {osName} {archName}; build it from source in Node settings → "
               + $"llama.cpp build from source, or choose {string.Join(" or ", alternatives)}.";
    }

    private static string VariantDisplayName(GpuVariant variant) =>
        variant switch
        {
            GpuVariant.Cuda => "CUDA",
            GpuVariant.Vulkan => "Vulkan",
            _ => "CPU"
        };

    /// <summary>The host shape one prebuilt asset is pinned for.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PinKey(OSPlatform Os, Architecture Arch, GpuVariant Variant);
}
