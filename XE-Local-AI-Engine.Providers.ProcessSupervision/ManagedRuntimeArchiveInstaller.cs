namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Downloads a pinned runtime archive, verifies its SHA256, extracts it into a staging sibling and publishes it through
///     <see cref="StagedDirectoryPublish" />; a failed attempt is discarded and retried exactly once.
/// </summary>
/// <remarks>
///     Every failure surfaces through the provider's exception factory with a message that names only the runtime, never a path or
///     URL. llama.cpp keeps its own downloader: its stall watchdog and expected-size check differ on purpose.
/// </remarks>
public sealed class ManagedRuntimeArchiveInstaller
{
    private readonly Func<string, Exception?, Exception> _createException;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly long _maxDownloadBytes;
    private readonly string _runtimeName;
    private readonly string _tempFilePrefix;

    /// <summary>Creates an installer for one runtime; <paramref name="runtimeName" /> is the display name every message carries.</summary>
    /// <param name="maxDownloadBytes">A disk-exhaustion guard against a hostile or buggy server streaming an unbounded body.</param>
    /// <param name="createException">Builds the provider's sanitized exception from a message and an optional cause.</param>
    public ManagedRuntimeArchiveInstaller(HttpClient httpClient,
        string runtimeName,
        string tempFilePrefix,
        long maxDownloadBytes,
        Func<string, Exception?, Exception> createException,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempFilePrefix);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDownloadBytes);
        ArgumentNullException.ThrowIfNull(createException);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _runtimeName = runtimeName;
        _tempFilePrefix = tempFilePrefix;
        _maxDownloadBytes = maxDownloadBytes;
        _createException = createException;
        _logger = logger;
    }

    /// <summary>Installs the archive at <paramref name="url" /> as <paramref name="targetDir" />, retrying a failed attempt once.</summary>
    public async Task InstallAsync(Uri url, string assetName, string expectedSha256, ManagedRuntimeArchiveKind archiveKind, string targetDir, CancellationToken ct)
    {
        var firstError = await DownloadVerifyExtractOrErrorAsync(url, assetName, expectedSha256, archiveKind, targetDir, ct).ConfigureAwait(false);
        if (firstError is null)
        {
            return;
        }

        var secondError = await DownloadVerifyExtractOrErrorAsync(url, assetName, expectedSha256, archiveKind, targetDir, ct).ConfigureAwait(false);
        if (secondError is null)
        {
            return;
        }

        throw _createException($"The {_runtimeName} runtime could not be downloaded or failed integrity verification after a retry. "
                               + "Check the network connection and try again.",
            secondError);
    }

    /// <summary>Streams <paramref name="url" /> to <paramref name="destination" /> under the size cap; a failed download leaves no file.</summary>
    public async Task DownloadToFileAsync(Uri url, string destination, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        try
        {
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > _maxDownloadBytes)
                {
                    throw _createException($"The {_runtimeName} runtime download exceeded the maximum allowed size.", null);
                }

                await target.WriteAsync(buffer.AsMemory(start: 0, read), ct).ConfigureAwait(false);
            }
        }
        catch
        {
            BestEffortFile.TryDeleteFile(destination);
            throw;
        }
    }

    private async Task<Exception?> DownloadVerifyExtractOrErrorAsync(Uri url,
        string assetName,
        string expectedSha256,
        ManagedRuntimeArchiveKind archiveKind,
        string targetDir,
        CancellationToken ct)
    {
        // Defense in depth: strip any directory component before composing a temp path, so an asset name can never traverse.
        var tempArchive = Path.Combine(Path.GetTempPath(), $"{_tempFilePrefix}-{Guid.NewGuid():N}-{Path.GetFileName(assetName)}");
        try
        {
            await DownloadToFileAsync(url, tempArchive, ct).ConfigureAwait(false);

            if (!await FileSha256.MatchesAsync(tempArchive, expectedSha256, ct).ConfigureAwait(false))
            {
                return _createException($"The {_runtimeName} runtime download failed integrity verification.", null);
            }

            await ExtractAndPublishAsync(tempArchive, archiveKind, targetDir, ct).ConfigureAwait(false);
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
            BestEffortFile.TryDeleteFile(tempArchive);
        }
    }

    /// <summary>Extracts into a temp sibling, then publishes it, so neither a partial extract nor a failed publish replaces a cached install.</summary>
    private async Task ExtractAndPublishAsync(string archivePath, ManagedRuntimeArchiveKind archiveKind, string targetDir, CancellationToken ct)
    {
        var stagingDir = $"{targetDir}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(stagingDir);
        try
        {
            if (archiveKind == ManagedRuntimeArchiveKind.TarGz)
            {
                await using var compressed = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var decompressed = new GZipStream(compressed, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(decompressed, stagingDir, overwriteFiles: true, ct).ConfigureAwait(false);
            }
            else
            {
                await ZipFile.ExtractToDirectoryAsync(archivePath, stagingDir, ct).ConfigureAwait(false);
            }

            await StagedDirectoryPublish.PublishAsync(stagingDir, targetDir, _logger, ct).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
    }
}
