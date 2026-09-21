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
///     Pinned tag <c>b5130</c>, fetched from <c>https://github.com/ggml-org/whisper.cpp/releases/download/{tag}/{asset}</c>.
///     whisper.cpp ships release binaries ONLY on its nightly <c>b&lt;n&gt;</c> tags — the semantic <c>v1.9.x</c> tags carry zero
///     assets — and it publishes no <c>.sha256</c> sidecars, so digests come from the GitHub release-assets API <c>digest</c> field. It
///     ships NO prebuilt Linux CUDA asset either. See docs/wiki/24-audio-transcription.md
///     ("The pinned whisper.cpp prebuilt release table").
/// </remarks>
public static class WhisperCppReleasePins
{
    /// <summary>The pinned whisper.cpp nightly release tag every prebuilt asset below is taken from.</summary>
    public const string PinnedTag = "b5130";

    /// <summary>
    ///     The exact canonical source revision official managed builds check out — a <b>peeled commit SHA</b>, never a
    ///     tag object.
    /// </summary>
    /// <remarks>
    ///     <c>v1.9.4</c> is an ANNOTATED tag: the Git refs API returns the tag object
    ///     <c>7d75b14994ae7f59623e2471445e2355fe506ed2</c>, which is not a commit and which nothing can fetch or check
    ///     out. Dereferencing it yields this value, which is also <see cref="PinnedTag" />'s <c>target_commitish</c>.
    ///     The source build fetches this SHA directly and must never re-derive it from a tag name at build time.
    /// </remarks>
    public const string PinnedSourceCommitSha = "927cfce34f31707e17f2bff35c349632fb9e2c3a";

    // Both Windows archives nest the executable under Release/; the Linux tarball under its single top-level dir.
    private const string WindowsServerPath = "Release/whisper-server.exe";
    private const string LinuxServerPath = "whisper-bin-ubuntu-x64/whisper-server";

    // Keyed by (os, arch, backend). Verified against the b5130 release-assets digest API on 2026-09-11; the two
    // downloadable assets were additionally fetched and re-hashed locally, and both matched.
    private static readonly IReadOnlyDictionary<PinKey, WhisperAssetPin> Pins =
        new Dictionary<PinKey, WhisperAssetPin>
        {
            // Windows x64 — the cuBLAS build bundles its own CUDA runtime DLLs, so there is no companion archive.
            [new PinKey(OSPlatform.Windows, Architecture.X64, WhisperBackend.Cuda)] =
                new()
                {
                    AssetName = "whisper-cublas-12.4.0-bin-x64.zip",
                    Sha256 = "af520ddd034d985b55dfeea3e465ed93653ba2aee1a55e865033edc548c272a7",
                    ServerRelativePath = WindowsServerPath,
                    ArchiveKind = WhisperArchiveKind.Zip
                },
            [new PinKey(OSPlatform.Windows, Architecture.X64, WhisperBackend.Cpu)] =
                new()
                {
                    AssetName = "whisper-bin-x64.zip",
                    Sha256 = "f9ec6c52a2e949b62ab51fa21d0d497958f9e41c3010c157c4e42932d5316f3c",
                    ServerRelativePath = WindowsServerPath,
                    ArchiveKind = WhisperArchiveKind.Zip
                },

            // Linux x64 — CPU only upstream; the managed source build provides the CUDA lane.
            [new PinKey(OSPlatform.Linux, Architecture.X64, WhisperBackend.Cpu)] =
                new()
                {
                    AssetName = "whisper-bin-ubuntu-x64.tar.gz",
                    Sha256 = "53e7fd8b5764edad916b8848dd0af6abb1ff1d3b86c899e79c78652412536c32",
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
