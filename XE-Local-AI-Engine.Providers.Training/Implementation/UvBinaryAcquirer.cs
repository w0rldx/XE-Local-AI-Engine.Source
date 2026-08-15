namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

/// <summary>
///     Acquires the pinned <c>uv</c> release as a managed binary: download → SHA-256 verify → atomic extract into a
///     version-keyed directory. Mirrors <c>LlamaCppBinaryManager</c>'s acquisition pipeline, including the
///     extract-to-sibling-then-move step that stops a partial extract from masquerading as a warm cache.
/// </summary>
/// <remarks>
///     A cache hit short-circuits the whole thing, so a re-install on a box that already fetched uv performs no network
///     I/O. The digest is checked before anything is unpacked, never after — an archive that fails verification is never
///     written anywhere a later step could find it.
/// </remarks>
internal sealed class UvBinaryAcquirer(HttpClient httpClient)
{
    // uv's Linux tarball is ~20 MB; the ceiling only exists so a hostile or misconfigured host cannot stream forever.
    private const long MaxDownloadBytes = 512L * 1024 * 1024;

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    /// <summary>
    ///     Ensures the pinned uv executable exists under <paramref name="cacheRoot" /> and returns its absolute path.
    /// </summary>
    public async Task<string> EnsureUvAsync(string cacheRoot, Action<string> logSink, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(logSink);

        var versionDir = Path.Combine(cacheRoot, "uv", TrainingRuntimePins.UvVersion);
        var executable = Path.Combine(versionDir, TrainingRuntimePins.UvArchiveRootDirectory, TrainingRuntimePins.UvExecutableName);
        if (File.Exists(executable))
        {
            logSink($"Using the cached uv {TrainingRuntimePins.UvVersion}.");
            return executable;
        }

        var tempArchive = Path.Combine(Path.GetTempPath(), $"uv-{Guid.NewGuid():N}-{TrainingRuntimePins.UvAssetName}");
        try
        {
            logSink($"Downloading uv {TrainingRuntimePins.UvVersion}.");
            await DownloadToFileAsync(TrainingRuntimePins.UvDownloadUri(), tempArchive, ct).ConfigureAwait(false);

            if (!await HashMatchesAsync(tempArchive, TrainingRuntimePins.UvSha256, ct).ConfigureAwait(false))
            {
                throw new TrainingRuntimeException("The uv download failed integrity verification and was discarded.");
            }

            logSink("Verified the uv download digest.");
            ExtractTarGzAtomically(tempArchive, versionDir);
        }
        finally
        {
            TryDeleteFile(tempArchive);
        }

        if (!File.Exists(executable))
        {
            throw new TrainingRuntimeException("The uv archive did not contain the expected executable.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return executable;
    }

    private async Task DownloadToFileAsync(Uri url, string destination, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new TrainingRuntimeException("The uv release could not be downloaded. Check the network connection and try again.");
        }

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
                if (written > MaxDownloadBytes)
                {
                    throw new TrainingRuntimeException("The uv download exceeded the maximum allowed size.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch
        {
            TryDeleteFile(destination);
            throw;
        }
    }

    private static async Task<bool> HashMatchesAsync(string filePath, string expectedSha256, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    // Extract into a temp sibling then move into place, so an interrupted extract cannot be mistaken for a warm cache.
    private static void ExtractTarGzAtomically(string archivePath, string versionDir)
    {
        var stagingDir = $"{versionDir}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(stagingDir);
        try
        {
            using (var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gzip = new GZipStream(fileStream, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gzip, stagingDir, overwriteFiles: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(versionDir.TrimEnd(Path.DirectorySeparatorChar))!);
            if (Directory.Exists(versionDir))
            {
                Directory.Delete(versionDir, recursive: true);
            }

            Directory.Move(stagingDir, versionDir);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp download.
        }
    }
}
