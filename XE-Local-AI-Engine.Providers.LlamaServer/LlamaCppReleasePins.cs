namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Runtime.InteropServices;

/// <summary>
///     A single pinned, hash-verified llama.cpp prebuilt asset (one OS/arch/variant combination).
/// </summary>
public sealed class LlamaCppAssetPin
{
    /// <summary>The release asset file name (for example <c>llama-b11803-bin-win-vulkan-x64.zip</c>).</summary>
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
    public const string PinnedTag = "b11803";

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
    public const string PinnedCudaSourceCommitSha = "a37cc46104eb50900d5ab4a9ba02a0abdac039db";

    /// <summary>Backend-neutral alias for the exact source commit behind <see cref="PinnedTag" />.</summary>
    public const string PinnedSourceCommitSha = PinnedCudaSourceCommitSha;

    private const string WindowsServerPath = "build/bin/llama-server.exe";
    private const string UnixServerPath = "build/bin/llama-server";

    // Keyed by (os, arch, variant). Verified against the b11803 release-assets digest API on 2026-10-22.
    private static readonly IReadOnlyDictionary<PinKey, LlamaCppAssetPin> Pins =
        new Dictionary<PinKey, LlamaCppAssetPin>
        {
            // Windows x64 — the CUDA pin also carries its companion runtime archive; both digests come from the release-assets digest API. The cudart asset name is NOT
            // tag-prefixed upstream, and its digest is unchanged from b9692 through b11803, upstream shipping the same CUDA 12.4 runtime archive across those releases.
            [new PinKey(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda)] =
                new()
                {
                    AssetName = "llama-b11803-bin-win-cuda-12.4-x64.zip",
                    Sha256 = "3a9724665d7b1036d2e7f9e1cd2ef869f534bd3a1f7c11972e1eaeb223d9ec07",
                    ServerRelativePath = WindowsServerPath,
                    CudartAssetName = "cudart-llama-bin-win-cuda-12.4-x64.zip",
                    CudartSha256 = "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6"
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, GpuVariant.Vulkan)] =
                new()
                {
                    AssetName = "llama-b11803-bin-win-vulkan-x64.zip",
                    Sha256 = "01d38d95c7cb22eb615c2ea3269c9e1623d64b663b9df72c71de3ab1fec72716",
                    ServerRelativePath = WindowsServerPath
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11803-bin-win-cpu-x64.zip",
                    Sha256 = "68a11f1f0ab78587bfc42071d2dbd4aa1b621357ddc375c1ee25a24e76d27954",
                    ServerRelativePath = WindowsServerPath
                },

            // Windows arm64 (CPU floor only)
            [new PinKey(OSPlatform.Windows, Architecture.Arm64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11803-bin-win-cpu-arm64.zip",
                    Sha256 = "315288ba2131fb18bb6776f9a1234de1d37d83c43ada9db7b54b761f783dd6ec",
                    ServerRelativePath = WindowsServerPath
                },

            // Linux x64 — the CUDA pin carries its companion runtime archive like Windows does, but the Linux companion name IS tag-prefixed.
            [new PinKey(OSPlatform.Linux, Architecture.X64, GpuVariant.Cuda)] =
                new()
                {
                    AssetName = "llama-b11803-bin-ubuntu-cuda-12.8-x64.tar.gz",
                    Sha256 = "4594be0afdddcfdf15e82f4fbd6805c60c399eaa62b181432ccaf3de87ef36d0",
                    ServerRelativePath = UnixServerPath,
                    CudartAssetName = "cudart-llama-b11803-bin-ubuntu-cuda-12.8-x64.tar.gz",
                    CudartSha256 = "38af6ae05c2d7313a577e1dafc5527950b16ad62193e2a0e7c278b504ea5d7e4"
                },
            [new PinKey(OSPlatform.Linux, Architecture.X64, GpuVariant.Vulkan)] =
                new()
                {
                    AssetName = "llama-b11803-bin-ubuntu-vulkan-x64.tar.gz",
                    Sha256 = "dac1fc5070dd9f737f97fce15965070a3f9b7d586c66186b509604a3a7ac731c",
                    ServerRelativePath = UnixServerPath
                },
            [new PinKey(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11803-bin-ubuntu-x64.tar.gz",
                    Sha256 = "2c718373086c9eda54d1e994cd5197aa079a4247e3dc4ec9edee8254cee08746",
                    ServerRelativePath = UnixServerPath
                },

            // Linux arm64
            [new PinKey(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Vulkan)] =
                new()
                {
                    AssetName = "llama-b11803-bin-ubuntu-vulkan-arm64.tar.gz",
                    Sha256 = "d7995c61c81c566a829adef73f48005ef3be5436df5421df5350fd41f48c891b",
                    ServerRelativePath = UnixServerPath
                },
            [new PinKey(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11803-bin-ubuntu-arm64.tar.gz",
                    Sha256 = "d15bfa2ececaa7c9d825af7d3fe8f87aaaf8fd246206ba0d9c850c62f900a1a7",
                    ServerRelativePath = UnixServerPath
                },

            // macOS (CPU floor; llama.cpp uses Metal at runtime within the universal build)
            [new PinKey(OSPlatform.OSX, Architecture.Arm64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11803-bin-macos-arm64.tar.gz",
                    Sha256 = "742ad51ee1bcf2606b5875d3a45824f8e6fd9000dc4d7488107448e0d40ddd12",
                    ServerRelativePath = UnixServerPath
                },
            [new PinKey(OSPlatform.OSX, Architecture.X64, GpuVariant.Cpu)] =
                new()
                {
                    AssetName = "llama-b11803-bin-macos-x64.tar.gz",
                    Sha256 = "9c85965695aa70cef61f89b7f584419fd788e07d184247c6cb32a9b9b8744e24",
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
    ///     archive, so an arm64 CUDA request — which has no pinned prebuilt — resolves to null and the binary manager
    ///     fails with the sanitized "no prebuilt" error instead of serving a CPU build stamped as CUDA.
    /// </remarks>
    public static LlamaCppAssetPin? TryResolveExact(OSPlatform os, Architecture arch, GpuVariant variant)
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
