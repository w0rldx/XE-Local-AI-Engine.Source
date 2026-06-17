namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

/// <summary>
///     Default <see cref="ILlamaCppBinaryManager" />: resolves the pinned prebuilt asset for the host, downloads it
///     over HTTP, verifies its SHA256 against <see cref="LlamaCppReleasePins" />, extracts it under a stable cache
///     directory, and returns the resolved <c>llama-server</c> path. Never source-builds.
/// </summary>
/// <remarks>
///     <para>
///         Cache layout: <c>{cacheRoot}/llama.cpp/{tag}/{variant}/</c> holds the extracted archive; a hash-valid
///         cached binary is reused without re-download (offline path). A user-selected upgrade is cached under its own
///         <c>{tag}</c> directory, so the recommended-pinned fallback is never deleted by an upgrade.
///     </para>
///     <para>
///         On SHA256 mismatch the partial download is discarded and retried <em>once</em>; a second mismatch surfaces
///         a sanitized <see cref="LlamaRuntimeException" /> (no internal paths/URLs in the message).
///     </para>
/// </remarks>
public sealed class LlamaCppBinaryManager : ILlamaCppBinaryManager
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheRoot;
    private readonly string _activeTag;
    private readonly OSPlatform _os;
    private readonly Architecture _arch;

    /// <summary>
    ///     Creates a binary manager that downloads through <paramref name="httpClient" /> and caches under
    ///     <paramref name="cacheRoot" />. <paramref name="activeTag" /> selects the recommended-pinned release by
    ///     default; pass a different tag to model a user-selected upgrade (the pinned tag's cache is never touched).
    /// </summary>
    public LlamaCppBinaryManager(HttpClient httpClient, string? cacheRoot = null, string? activeTag = null)
        : this(
            httpClient,
            cacheRoot ?? DefaultCacheRoot(),
            activeTag ?? LlamaCppReleasePins.PinnedTag,
            CurrentOsPlatform(),
            RuntimeInformation.ProcessArchitecture)
    {
    }

    /// <summary>Test seam: pins OS/arch so asset selection can be exercised on any host.</summary>
    internal LlamaCppBinaryManager(HttpClient httpClient, string cacheRoot, string activeTag, OSPlatform os, Architecture arch)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeTag);
        _cacheRoot = cacheRoot;
        _activeTag = activeTag;
        _os = os;
        _arch = arch;
    }

    /// <inheritdoc />
    public async Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct)
    {
        var pin = LlamaCppReleasePins.Resolve(_os, _arch, variant)
            ?? throw new LlamaRuntimeException(
                "No prebuilt llama.cpp runtime is available for this operating system and CPU architecture.");

        var isPinnedFallback = string.Equals(_activeTag, LlamaCppReleasePins.PinnedTag, StringComparison.Ordinal);
        var variantDir = Path.Combine(_cacheRoot, "llama.cpp", _activeTag, VariantSlug(variant));
        var serverPath = Path.GetFullPath(Path.Combine(variantDir, pin.ServerRelativePath));

        // Offline / already-cached path: reuse a present binary without re-download.
        if (File.Exists(serverPath))
        {
            return new LlamaBinary(serverPath, _activeTag, variant, isPinnedFallback);
        }

        await DownloadVerifyExtractAsync(pin, variantDir, ct).ConfigureAwait(false);

        if (!File.Exists(serverPath))
        {
            throw new LlamaRuntimeException(
                "The downloaded llama.cpp runtime did not contain the expected server executable.");
        }

        return new LlamaBinary(serverPath, _activeTag, variant, isPinnedFallback);
    }

    private async Task DownloadVerifyExtractAsync(LlamaCppAssetPin pin, string variantDir, CancellationToken ct)
    {
        var url = LlamaCppReleasePins.DownloadUri(_activeTag, pin.AssetName);

        // First attempt. A transient failure or a hash mismatch is discarded and retried exactly once.
        var firstError = await TryDownloadVerifyExtractAsync(url, pin, variantDir, ct).ConfigureAwait(false);
        if (firstError is null)
        {
            return;
        }

        var secondError = await TryDownloadVerifyExtractAsync(url, pin, variantDir, ct).ConfigureAwait(false);
        if (secondError is null)
        {
            return;
        }

        throw new LlamaRuntimeException(
            "The llama.cpp runtime could not be downloaded or failed integrity verification after a retry. "
            + "Check the network connection and try again.",
            secondError);
    }

    /// <summary>
    ///     Runs one download → SHA256 verify → extract pass. Returns <see langword="null" /> on success, or the
    ///     non-fatal failure cause to drive a single retry. Cancellation propagates rather than being swallowed.
    /// </summary>
    private async Task<Exception?> TryDownloadVerifyExtractAsync(Uri url, LlamaCppAssetPin pin, string variantDir, CancellationToken ct)
    {
        var tempArchive = Path.Combine(Path.GetTempPath(), $"llamacpp-{Guid.NewGuid():N}-{pin.AssetName}");
        try
        {
            await DownloadToFileAsync(url, tempArchive, ct).ConfigureAwait(false);

            if (!await HashMatchesAsync(tempArchive, pin.Sha256, ct).ConfigureAwait(false))
            {
                return new LlamaRuntimeException("The llama.cpp runtime download failed integrity verification.");
            }

            ExtractArchive(tempArchive, pin.AssetName, variantDir);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
        finally
        {
            TryDeleteFile(tempArchive);
        }
    }

    private async Task DownloadToFileAsync(Uri url, string destination, CancellationToken ct)
    {
        using var response = await _httpClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    private static async Task<bool> HashMatchesAsync(string filePath, string expectedSha256, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var actual = Convert.ToHexStringLower(hash);
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractArchive(string archivePath, string assetName, string variantDir)
    {
        // Extract into a temp sibling then atomically move into place so a partial extract can't masquerade as cached.
        var stagingDir = $"{variantDir}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(stagingDir);
        try
        {
            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(archivePath, stagingDir);
            }
            else if (assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                ExtractTarGz(archivePath, stagingDir);
            }
            else
            {
                throw new LlamaRuntimeException("The llama.cpp runtime archive format is not supported.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(variantDir.TrimEnd(Path.DirectorySeparatorChar))!);
            if (Directory.Exists(variantDir))
            {
                Directory.Delete(variantDir, recursive: true);
            }

            Directory.Move(stagingDir, variantDir);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
    }

    private static void ExtractTarGz(string archivePath, string destination)
    {
        using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp download; ignore.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp download; ignore.
        }
    }

    private static string VariantSlug(GpuVariant variant) => variant switch
    {
        GpuVariant.Cuda => "cuda",
        GpuVariant.Vulkan => "vulkan",
        _ => "cpu"
    };

    private static string DefaultCacheRoot() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine");

    private static OSPlatform CurrentOsPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return OSPlatform.Windows;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return OSPlatform.OSX;
        }

        return OSPlatform.Linux;
    }
}
