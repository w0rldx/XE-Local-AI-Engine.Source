namespace XE_Local_AI_Engine.Providers.WhisperCpp;

using System.Runtime.InteropServices;

/// <summary>The archive container a pinned whisper.cpp asset ships in.</summary>
/// <remarks>
///     Unlike stable-diffusion.cpp, whisper.cpp does NOT ship one container for every host: Windows assets are
///     <c>.zip</c> and the Linux asset is <c>.tar.gz</c>, so the binary manager cannot assume zip and dispatches on this.
/// </remarks>
public enum WhisperArchiveKind
{
    Zip = 0,
    TarGz = 1
}

/// <summary>
///     A single pinned, hash-verified whisper.cpp prebuilt asset (one OS/arch/backend combination).
/// </summary>
public sealed class WhisperAssetPin
{
    /// <summary>The release asset file name (for example <c>whisper-bin-x64.zip</c>).</summary>
    public required string AssetName { get; init; }

    /// <summary>Lowercase hex SHA256 the downloaded archive must match.</summary>
    public required string Sha256 { get; init; }

    /// <summary>
    ///     Path to <c>whisper-server</c> inside the extracted archive. The Windows archives nest everything under
    ///     <c>Release/</c>; the Linux tarball has a single top-level <c>whisper-bin-ubuntu-x64/</c> directory.
    /// </summary>
    public required string ServerRelativePath { get; init; }

    /// <summary>The container the asset ships in, which selects the extraction path.</summary>
    public required WhisperArchiveKind ArchiveKind { get; init; }
}

/// <summary>
///     Verified, pinned whisper.cpp prebuilt-release table — the acquisition source for
///     <see cref="Implementation.WhisperCppBinaryManager" /> when no managed source-built runtime is selected and no
///     bring-your-own override is active.
/// </summary>
/// <remarks>
///     Pinned tag <c>b5454</c>, fetched from <c>https://github.com/ggml-org/whisper.cpp/releases/download/{tag}/{asset}</c>.
///     whisper.cpp ships release binaries ONLY on its nightly <c>b&lt;n&gt;</c> tags — the semantic <c>v1.9.x</c> tags carry zero
///     assets — and it publishes no <c>.sha256</c> sidecars, so digests come from the GitHub release-assets API <c>digest</c> field. It
///     ships NO prebuilt Linux CUDA asset either. See docs/wiki/24-audio-transcription.md
///     ("The pinned whisper.cpp prebuilt release table").
/// </remarks>
public static class WhisperCppReleasePins
{
    /// <summary>The pinned whisper.cpp nightly release tag every prebuilt asset below is taken from.</summary>
    public const string PinnedTag = "b5454";

    /// <summary>
    ///     The exact canonical source revision official managed builds check out — a <b>peeled commit SHA</b>, never a
    ///     tag object.
    /// </summary>
    /// <remarks>
    ///     <c>v1.9.5</c> is an ANNOTATED tag: the Git refs API returns the tag object
    ///     <c>d2a764c648779feb36d44363f212a29b00aad1af</c>, which is not a commit and which nothing can fetch or check
    ///     out. Dereferencing it yields this value, which is also <see cref="PinnedTag" />'s <c>target_commitish</c>.
    ///     The source build fetches this SHA directly and must never re-derive it from a tag name at build time.
    /// </remarks>
    public const string PinnedSourceCommitSha = "d1be6fde11ac6e0407606b4e42fe72d34add8037";

    // Both Windows archives nest the executable under Release/; the Linux tarball under its single top-level dir.
    private const string WindowsServerPath = "Release/whisper-server.exe";
    private const string LinuxServerPath = "whisper-bin-ubuntu-x64/whisper-server";

    // Keyed by (os, arch, backend). Verified against the b5454 release-assets digest API on 2026-10-30; the Linux tarball
    // was additionally fetched and re-hashed locally and matched, and every archive's server path was listed remotely.
    private static readonly IReadOnlyDictionary<PinKey, WhisperAssetPin> Pins =
        new Dictionary<PinKey, WhisperAssetPin>
        {
            // Windows x64 — the CUDA build bundles its own CUDA runtime DLLs, so there is no companion archive. Upstream
            // renamed this asset from whisper-cublas-12.4.0-bin-x64.zip after b5130; the layout is unchanged.
            [new PinKey(OSPlatform.Windows, Architecture.X64, WhisperBackend.Cuda)] =
                new()
                {
                    AssetName = "whisper-bin-win-cuda-12.4.0-x64.zip",
                    Sha256 = "afef0b881c500958921c3f5523b50e59ee2ec9b6f5cbd25b324c51ed308a957a",
                    ServerRelativePath = WindowsServerPath,
                    ArchiveKind = WhisperArchiveKind.Zip
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, WhisperBackend.Cpu)] =
                new()
                {
                    AssetName = "whisper-bin-x64.zip",
                    Sha256 = "6ba69e3482d7826214f90a6a9c84ca07782aec1e1d0c6a7c30c994fd5d816ccb",
                    ServerRelativePath = WindowsServerPath,
                    ArchiveKind = WhisperArchiveKind.Zip
                },

            // Linux x64 — CPU only upstream; the managed source build provides the CUDA lane.
            [new PinKey(OSPlatform.Linux, Architecture.X64, WhisperBackend.Cpu)] =
                new()
                {
                    AssetName = "whisper-bin-ubuntu-x64.tar.gz",
                    Sha256 = "a72becf15d7917f990f6313867a52638b82b7f9ef237fb0c980dac56a135781c",
                    ServerRelativePath = LinuxServerPath,
                    ArchiveKind = WhisperArchiveKind.TarGz
                }
        };

    /// <summary>Builds the absolute download URL for a named asset in the given release tag.</summary>
    public static Uri DownloadUri(string tag, string assetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetName);
        return new Uri($"https://github.com/ggml-org/whisper.cpp/releases/download/{tag}/{assetName}");
    }

    /// <summary>
    ///     Resolves the pinned asset for the given OS/arch/backend, falling back to the CPU floor when no GPU prebuilt
    ///     exists for the host. Returns <see langword="null" /> only when even the CPU floor is unavailable.
    /// </summary>
    public static WhisperAssetPin? Resolve(OSPlatform os, Architecture arch, WhisperBackend backend)
    {
        if (Pins.TryGetValue(new PinKey(os, arch, backend), out var pin))
        {
            return pin;
        }

        // Fall back to the CPU floor for the host OS/arch — the Linux CUDA case, which upstream does not ship.
        return Pins.TryGetValue(new PinKey(os, arch, WhisperBackend.Cpu), out var cpuPin) ? cpuPin : null;
    }

    /// <summary>
    ///     Resolves only the exact OS/arch/backend asset. Unlike <see cref="Resolve" />, a missing GPU prebuilt never
    ///     substitutes the CPU floor, so resolved bytes can never contradict an explicitly requested GPU backend.
    /// </summary>
    public static WhisperAssetPin? ResolveExact(OSPlatform os, Architecture arch, WhisperBackend backend)
    {
        return Pins.TryGetValue(new PinKey(os, arch, backend), out var pin) ? pin : null;
    }

    /// <summary>Every pinned asset, for the invariant tests that assert digest shape across the whole table.</summary>
    internal static IEnumerable<WhisperAssetPin> All => Pins.Values;

    /// <summary>The host shape one prebuilt asset is pinned for.</summary>
    [StructLayout(LayoutKind.Auto)]
    private readonly record struct PinKey(OSPlatform Os, Architecture Arch, WhisperBackend Backend);
}
