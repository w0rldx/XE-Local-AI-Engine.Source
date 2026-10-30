namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Runtime.InteropServices;

/// <summary>
///     A single pinned, hash-verified llama.cpp prebuilt asset (one OS/arch/variant combination).
/// </summary>
public sealed class LlamaCppAssetPin
{
    /// <summary>The release asset file name (for example <c>llama-b12014-bin-win-vulkan-x64.zip</c>).</summary>
    public required string AssetName { get; init; }

    /// <summary>Lowercase hex SHA256 the downloaded archive must match.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Path to <c>llama-server</c> inside the extracted archive (Windows archives nest under <c>build/bin/</c>).</summary>
    public required string ServerRelativePath { get; init; }

    /// <summary>
    ///     The companion CUDA-runtime archive name, set ONLY on the CUDA pins (Windows x64, Linux x64) and <see langword="null" /> on
    ///     every other pin, which has no second archive to fetch.
    /// </summary>
    /// <remarks>
    ///     llama.cpp ships the CUDA runtime libraries (<c>cudart</c>, <c>cublas</c>, <c>cublasLt</c>: DLLs on Windows,
    ///     <c>.so</c> files on Linux) in a SEPARATE archive from the main build; without them next to
    ///     <c>llama-server</c> the ggml-cuda backend fails to load and the server silently runs CPU-only.
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
///     <c>https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{asset}</c>. Linux x64 pins upstream's
///     <c>ubuntu-cuda-12.8-x64</c> prebuilt; Linux arm64 has no CUDA prebuilt, so its NVIDIA hosts select Vulkan (<see cref="Implementation.GpuVariantSelector" />).
/// </remarks>
public static class LlamaCppReleasePins
{
    /// <summary>The recommended-pinned llama.cpp release tag.</summary>
    public const string PinnedTag = "b12014";

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
    public const string PinnedCudaSourceCommitSha = "aa588a3463357ee990b314f41dda242869130704";

    /// <summary>Backend-neutral alias for the exact source commit behind <see cref="PinnedTag" />.</summary>
    public const string PinnedSourceCommitSha = PinnedCudaSourceCommitSha;

    private const string WindowsServerPath = "build/bin/llama-server.exe";
    private const string UnixServerPath = "build/bin/llama-server";

    // Keyed by (os, arch, variant). Verified against the b12014 release-assets digest API on 2026-10-30.
    private static readonly IReadOnlyDictionary<PinKey, LlamaCppAssetPin> Pins =
        new Dictionary<PinKey, LlamaCppAssetPin>
        {
            // Windows x64 — the CUDA pin also carries its companion runtime archive; both digests come from the release-assets digest API. The cudart asset name is NOT
            // tag-prefixed upstream, and its digest is unchanged from b9692 through b12014, upstream shipping the same CUDA 12.4 runtime archive across those releases.
            [new PinKey(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda)] =
                new()
                {
                    AssetName = "llama-b12014-bin-win-cuda-12.4-x64.zip",
                    Sha256 = "a88f83ec5028a9af4256bdd033aa56bd3df2338b6165e90a92cf546f4f299659",
                    ServerRelativePath = WindowsServerPath,
                    CudartAssetName = "cudart-llama-bin-win-cuda-12.4-x64.zip",
                    CudartSha256 = "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6"
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, GpuVariant.Vulkan)] =
                new()
                {
                    AssetName = "llama-b12014-bin-win-vulkan-x64.zip",
                    Sha256 = "e3712df80ab21c81051f43ac6edd9f4c8faefb8f7f760d971a578a78d6bbbdac",
                    ServerRelativePath = WindowsServerPath
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b12014-bin-win-cpu-x64.zip",
                    Sha256 = "205f02ae89e1338381cd142a5b6aabc144524a3d6f310b40a6b7576e3357b6fa",
                    ServerRelativePath = WindowsServerPath
                },

            // Windows arm64 (CPU floor only)
            [new PinKey(OSPlatform.Windows, Architecture.Arm64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b12014-bin-win-cpu-arm64.zip",
                    Sha256 = "239b8461f54b9095bb30e0493c61b9337ef2f20f93b1b61934a43074c78db42a",
                    ServerRelativePath = WindowsServerPath
                },

            // Linux x64 — the CUDA pin carries its companion runtime archive like Windows does, but the Linux companion name IS tag-prefixed.
            [new PinKey(OSPlatform.Linux, Architecture.X64, GpuVariant.Cuda)] =
                new()
                {
                    AssetName = "llama-b12014-bin-ubuntu-cuda-12.8-x64.tar.gz",
                    Sha256 = "d1c5fd043805c78ed6a4a43ae4d54584ff144dbdaa106acf0c278c150b8d640b",
                    ServerRelativePath = UnixServerPath,
                    CudartAssetName = "cudart-llama-b12014-bin-ubuntu-cuda-12.8-x64.tar.gz",
                    CudartSha256 = "b8a469b60dbf9e39621b56f66af6ddd4618f110ab1eac9ff1c055eec52f5d864"
                },
            [new PinKey(OSPlatform.Linux, Architecture.X64, GpuVariant.Vulkan)] =
                new()
                {
                    AssetName = "llama-b12014-bin-ubuntu-vulkan-x64.tar.gz",
                    Sha256 = "023d546093443cbde73076621be1f466295be0c2c02495057d3c1c5647ae3b51",
                    ServerRelativePath = UnixServerPath
                },
            [new PinKey(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b12014-bin-ubuntu-x64.tar.gz",
                    Sha256 = "18b72a55f5fafc4d5327197e5e6af9e280ca9b1ddcd25546cb3a810d9ec1a51d",
                    ServerRelativePath = UnixServerPath
                },

            // Linux arm64
            [new PinKey(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Vulkan)] =
                new()
                {
                    AssetName = "llama-b12014-bin-ubuntu-vulkan-arm64.tar.gz",
                    Sha256 = "0b29ecb7301297075cd891fccd62ec9d053b31d112210305f0b0f30562d90918",
                    ServerRelativePath = UnixServerPath
                },
            [new PinKey(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b12014-bin-ubuntu-arm64.tar.gz",
                    Sha256 = "fe83c90f65e9bd4725825dd97276512257cad74a5c5ccd7272a6cb1f27d17597",
                    ServerRelativePath = UnixServerPath
                },

            // macOS (CPU floor; llama.cpp uses Metal at runtime within the universal build)
            [new PinKey(OSPlatform.OSX, Architecture.Arm64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b12014-bin-macos-arm64.tar.gz",
                    Sha256 = "f787dac1e0f25fe103172a889d20498aee974147c077d30cc144671b0ad04ad8",
                    ServerRelativePath = UnixServerPath
                },
            [new PinKey(OSPlatform.OSX, Architecture.X64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b12014-bin-macos-x64.tar.gz",
                    Sha256 = "fc40ffb6f5a5c1bbbcfd5e7703dd11a44f8e65c86642e18e4f1890070491360f",
                    ServerRelativePath = UnixServerPath
                }
        };

    /// <summary>Builds the absolute download URL for a named asset in the given release tag.</summary>
    public static Uri DownloadUri(string tag, string assetName)
    {
        return new Uri($"https://github.com/ggml-org/llama.cpp/releases/download/{tag}/{assetName}");
    }

    /// <summary>
    ///     Derives the companion CUDA-runtime archive name from a CUDA main asset name, or
    ///     <see langword="null" /> for any name that is not one.
    /// </summary>
    /// <remarks>
    ///     Windows: <c>llama-{tag}-bin-win-cuda-{ver}-x64.zip</c> pairs with <c>cudart-llama-bin-win-cuda-{ver}-x64.zip</c>, NOT
    ///     tag-prefixed. Linux: <c>llama-{tag}-bin-ubuntu-cuda-{ver}-{arch}.tar.gz</c> pairs with the whole name prefixed by
    ///     <c>cudart-</c>. Returning null for anything else keeps a second archive on the CUDA acquisition paths alone.
    /// </remarks>
    public static string? DeriveCudartAssetName(string? mainAssetName)
    {
        if (string.IsNullOrWhiteSpace(mainAssetName))
        {
            return null;
        }

        const string prefix = "llama-";
        if (mainAssetName.StartsWith(prefix, StringComparison.Ordinal)
            && mainAssetName.Contains("-bin-ubuntu-cuda-", StringComparison.Ordinal)
            && mainAssetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return $"cudart-{mainAssetName}";
        }

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
    ///     CAUTION: a GPU-variant request whose (os, arch, variant) has no pinned prebuilt — arm64 CUDA — returns the CPU
    ///     floor pin here, a non-null CPU archive, and serving that as a GPU-variant binary would mislabel a CPU build
    ///     the supervisor then emits GPU placement flags against. A caller acquiring a GPU variant must use
    ///     <see cref="ResolveExactOrNull" /> and treat null as "no prebuilt", never fall through to this floor.
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
    ///     archive, so an arm64 CUDA request — which has no pinned prebuilt — resolves to null and the binary manager
    ///     fails with the sanitized "no prebuilt" error instead of serving a CPU build stamped as CUDA.
    /// </remarks>
    public static LlamaCppAssetPin? ResolveExactOrNull(OSPlatform os, Architecture arch, GpuVariant variant)
    {
        return Pins.TryGetValue(new PinKey(os, arch, variant), out var pin) ? pin : null;
    }

    /// <summary>Whether a genuine prebuilt is pinned for EXACTLY this (os, arch, variant); the variant selector keys NVIDIA → CUDA on it.</summary>
    public static bool HasPin(OSPlatform os, Architecture arch, GpuVariant variant) =>
        Pins.ContainsKey(new PinKey(os, arch, variant));

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
        return variant == GpuVariant.Cpu ? Resolve(os, arch, variant) : ResolveExactOrNull(os, arch, variant);
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
