namespace XE_Local_AI_Engine.Providers.Python.Implementation;

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Acquires the pinned <c>uv</c> release as a managed binary: download, SHA-256 verify, atomic extract into a
///     version-keyed directory.
/// </summary>
/// <remarks>
///     Mirrors <c>LlamaCppBinaryManager</c>'s pipeline, including the extract-to-sibling-then-move step that stops a
///     partial extract masquerading as a warm cache. The digest is checked BEFORE anything is unpacked, never after.
///     Public because it is the SHARED uv acquisition for every uv-managed venv the engine provisions, not one
///     runtime's private detail. See docs/wiki/18-training.md ("The scrubbed environments, and the uv pipeline the
///     compute tool shares").
/// </remarks>
public sealed class UvBinaryAcquirer
{
    // uv's archives are ~20-45 MB; the ceiling only exists so a hostile or misconfigured host cannot stream forever.
    private const long MaxDownloadBytes = 512L * 1024 * 1024;

    // A holder downloads and extracts under the lock; generous next to a ~20 MB fetch on a slow link.
    private static readonly TimeSpan AcquireLockTimeout = TimeSpan.FromMinutes(15);

    // Null in production: resolved per call, so constructing the acquirer on an unsupported platform does not throw.
    private readonly ManagedPythonUvAsset? _asset;
    private readonly HttpClient _httpClient;

    public UvBinaryAcquirer(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
    }

    /// <summary>Test seam: pins the asset (layout and digest) a served archive must match, so a synthetic archive can pass verification.</summary>
    internal UvBinaryAcquirer(HttpClient httpClient, ManagedPythonUvAsset asset)
        : this(httpClient)
    {
        _asset = asset ?? throw new ArgumentNullException(nameof(asset));
    }

    /// <summary>Test seam: this platform's asset with the digest a synthetic archive carries.</summary>
    internal UvBinaryAcquirer(HttpClient httpClient, string expectedSha256)
        : this(httpClient, ManagedPythonPins.Current with { Sha256 = ThrowIfBlank(expectedSha256) })
    {
    }

    /// <summary>
    ///     Ensures the pinned uv executable exists under <paramref name="cacheRoot" /> and returns its absolute path.
    /// </summary>
    public async Task<string> EnsureUvAsync(string cacheRoot, Action<string> logSink, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentNullException.ThrowIfNull(logSink);

        ManagedPythonToolchain.EnsureWindowsPathBudget(cacheRoot);
        var asset = _asset ?? ManagedPythonPins.Current;
        var versionDir = Path.Combine(cacheRoot, "uv", ManagedPythonPins.UvVersion);
        var executable = asset.ExecutablePath(cacheRoot);
        if (File.Exists(executable))
        {
            logSink($"Using the cached uv {ManagedPythonPins.UvVersion}.");
            return executable;
        }

        // Every host and feature acquires into one store, so check, rename-aside and move happen under one cross-process
        // lock; re-checked inside it, because a winner may have finished while this caller waited.
        await using var acquireLock = await ManagedPythonToolchain.AcquireExclusiveLockAsync(Path.Combine(cacheRoot, "uv", ".acquire.lock"),
            AcquireLockTimeout,
            ct).ConfigureAwait(false);
        if (File.Exists(executable))
        {
            logSink($"Using the uv {ManagedPythonPins.UvVersion} another process just installed.");
            return executable;
        }

        SweepAbandonedStaging(Path.Combine(cacheRoot, "uv"));

        var tempArchive = Path.Combine(Path.GetTempPath(), $"uv-{Guid.NewGuid():N}-{asset.AssetName}");
        try
        {
            logSink($"Downloading uv {ManagedPythonPins.UvVersion}.");
            await DownloadToFileAsync(asset.DownloadUri(), tempArchive, ct).ConfigureAwait(false);

            if (!await HashMatchesAsync(tempArchive, asset.Sha256, ct).ConfigureAwait(false))
            {
                throw new ManagedPythonException("The uv download failed integrity verification and was discarded.");
            }

            logSink("Verified the uv download digest.");
            await ExtractAtomicallyAsync(tempArchive, asset.IsZip, versionDir, executable, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(tempArchive);
        }

        if (!File.Exists(executable))
        {
            throw new ManagedPythonException("The uv archive did not contain the expected executable.");
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return executable;
    }

    /// <summary>Removes the <c>.tmp</c> staging and <c>.stale</c> trees a crashed acquisition left behind.</summary>
    /// <remarks>
    ///     Only called under the acquire lock, so no live acquirer owns them. Complete version directories and the lock file
    ///     never match. Best-effort: a leftover that will not delete is retried on the next cold acquisition.
    /// </remarks>
    private static void SweepAbandonedStaging(string uvStore)
    {
        string[] leftovers;
        try
        {
            leftovers = [.. Directory.GetDirectories(uvStore, "*.tmp"), .. Directory.GetDirectories(uvStore, "*.stale")];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var leftover in leftovers)
        {
            TryDeleteDirectory(leftover);
        }
    }

    private static string ThrowIfBlank(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value;
    }

    private async Task DownloadToFileAsync(Uri url, string destination, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ManagedPythonException("The uv release could not be downloaded. Check the network connection and try again.");
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
                    throw new ManagedPythonException("The uv download exceeded the maximum allowed size.");
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

    /// <summary>
    ///     Extracts into a temp sibling then moves it into place, so an interrupted extract is never mistaken for a warm cache.
    /// </summary>
    /// <remarks>
    ///     Runs under the acquire lock. The fallback for an acquirer outside it (an older build on the same store) still
    ///     holds: a complete target is adopted, never replaced — replacing it pulled uv out from under a caller that had just
    ///     been handed its path (the cold-start race). Only a target without the executable is renamed aside, then deleted.
    /// </remarks>
    private static async Task ExtractAtomicallyAsync(string archivePath, bool isZip, string versionDir, string executable, CancellationToken ct)
    {
        var stagingDir = $"{versionDir}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(stagingDir);
        try
        {
            if (isZip)
            {
                await ExtractZipAsync(archivePath, stagingDir, ct).ConfigureAwait(false);
            }
            else
            {
                await using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, stagingDir, overwriteFiles: true, ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(versionDir.TrimEnd(Path.DirectorySeparatorChar))!);
            if (Directory.Exists(versionDir) && !File.Exists(executable))
            {
                // rename(2) is atomic, so a concurrent acquirer sees either the broken tree or none, never half of one.
                var stale = $"{versionDir}.{Guid.NewGuid():N}.stale";
                TryMoveDirectory(versionDir, stale);
                TryDeleteDirectory(stale);
            }

            if (!TryMoveDirectory(stagingDir, versionDir) && !File.Exists(executable))
            {
                throw new ManagedPythonException("The uv download could not be installed into the runtime cache.");
            }
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    /// <summary>Extracts a zip entry by entry, refusing the whole archive if any entry resolves outside <paramref name="stagingDir" /> (zip-slip).</summary>
    private static async Task ExtractZipAsync(string archivePath, string stagingDir, CancellationToken ct)
    {
        await using var archive = await ZipFile.OpenReadAsync(archivePath, ct).ConfigureAwait(false);
        var root = Path.GetFullPath(stagingDir);

        // Backslashes normalized first, so "..\x" is a traversal on every OS. IsUnderRoot also refuses an entry resolving
        // to the root itself ("./"): none of uv's entries is the root, so such an archive is not the one we pinned.
        if (archive.Entries.Any(entry => !PathContainment.IsUnderRoot(Destination(root, entry), root)))
        {
            throw new ManagedPythonException("The uv archive contained an entry outside its install directory and was discarded.");
        }

        foreach (var entry in archive.Entries)
        {
            var destination = Destination(root, entry);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await entry.ExtractToFileAsync(destination, overwrite: true, ct).ConfigureAwait(false);
        }
    }

    private static string Destination(string root, ZipArchiveEntry entry)
    {
        return Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('\\', '/')));
    }

    // False when the target already exists, which on a shared store means another acquirer won the race.
    private static bool TryMoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a staging or stale tree; a leftover sibling is never read as the cache.
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
