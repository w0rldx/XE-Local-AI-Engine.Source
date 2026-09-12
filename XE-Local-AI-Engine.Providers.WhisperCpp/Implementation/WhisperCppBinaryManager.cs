namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     Default <see cref="IWhisperCppBinaryManager" />: validates and resolves the selected managed runtime or, when
///     none is selected, downloads the exact pinned prebuilt asset for the host and backend, verifies its SHA256
///     against <see cref="WhisperCppReleasePins" />, extracts it under a stable cache directory, and returns the
///     resolved <c>whisper-server</c> path.
/// </summary>
/// <remarks>
///     <para>
///         Cache layout: <c>{cacheRoot}/whisper.cpp/{tag}/{backend}/</c> holds the extracted archive; a cached binary
///         is reused without re-download, which is what makes the offline path work.
///     </para>
///     <para>
///         On a SHA256 mismatch the partial download is discarded and retried <em>once</em>; a second mismatch
///         surfaces a sanitized <see cref="WhisperRuntimeException" /> carrying no internal path or URL.
///     </para>
///     <para>
///         Unlike the stable-diffusion.cpp manager this one pairs no companion CUDA-runtime archive: whisper.cpp's
///         cuBLAS zip bundles its own <c>cudart</c>, <c>cublas</c> and <c>nvrtc</c> DLLs. It does, however, dispatch on
///         archive kind, because Windows ships zip and Linux ships tar.gz.
///     </para>
/// </remarks>
public sealed class WhisperCppBinaryManager : IWhisperCppBinaryManager
{
    /// <summary>
    ///     Absolute hard ceiling on a single runtime download. The largest pinned asset is the 674 MB cuBLAS zip, so
    ///     this is a disk-exhaustion guard against a hostile or buggy server streaming an unbounded body, not a limit
    ///     any honest asset approaches.
    /// </summary>
    private const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    private readonly string _activeTag;
    private readonly Architecture _arch;
    private readonly WhisperAssetPin? _assetPinOverride;
    private readonly string _cacheRoot;
    private readonly HttpClient _httpClient;
    private readonly IWhisperInstalledRuntimeStore? _installedRuntimeStore;
    private readonly IWhisperManagedSourceBuildSignal? _managedSourceSignal;
    private readonly OSPlatform _os;
    private readonly WhisperServerRuntimeOverrideOptions? _overrideOptions;

    /// <summary>
    ///     Creates a binary manager that downloads through <paramref name="httpClient" /> and caches under
    ///     <paramref name="cacheRoot" />. When <paramref name="overrideOptions" /> is active,
    ///     <see cref="EnsureBinaryAsync" /> validates and serves the operator's binary instead of acquiring one.
    /// </summary>
    public WhisperCppBinaryManager(HttpClient httpClient,
        string? cacheRoot = null,
        string? activeTag = null,
        WhisperServerRuntimeOverrideOptions? overrideOptions = null,
        IWhisperInstalledRuntimeStore? installedRuntimeStore = null,
        IWhisperManagedSourceBuildSignal? managedSourceSignal = null)
        : this(httpClient,
            cacheRoot ?? DefaultCacheRoot(),
            activeTag ?? WhisperCppReleasePins.PinnedTag,
            CurrentOsPlatform(),
            RuntimeInformation.ProcessArchitecture,
            overrideOptions,
            installedRuntimeStore,
            managedSourceSignal)
    {
    }

    /// <summary>
    ///     Test seam: pins OS and architecture so asset selection can be exercised on any host, and optionally supplies
    ///     the asset pin itself.
    /// </summary>
    /// <remarks>
    ///     <paramref name="assetPinOverride" /> exists for exactly one thing: a test that must exercise the real
    ///     download-verify-extract pipeline has to serve an archive it built, and the production digests are the
    ///     digests of the real upstream assets, which no synthetic payload can match. Supplying the pin lets the test
    ///     keep the verification step ENABLED against its own payload rather than disabling it. Production never passes
    ///     this: <see cref="EnsureBinaryAsync" /> resolves from <see cref="WhisperCppReleasePins" />.
    /// </remarks>
    internal WhisperCppBinaryManager(HttpClient httpClient,
        string cacheRoot,
        string activeTag,
        OSPlatform os,
        Architecture arch,
        WhisperServerRuntimeOverrideOptions? overrideOptions = null,
        IWhisperInstalledRuntimeStore? installedRuntimeStore = null,
        IWhisperManagedSourceBuildSignal? managedSourceSignal = null,
        WhisperAssetPin? assetPinOverride = null)
    {
        _assetPinOverride = assetPinOverride;
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeTag);
        _cacheRoot = cacheRoot;
        _activeTag = activeTag;
        _os = os;
        _arch = arch;
        _overrideOptions = overrideOptions;
        _installedRuntimeStore = installedRuntimeStore;
        _managedSourceSignal = managedSourceSignal;
    }

    /// <inheritdoc />
    public async Task<WhisperBinary> EnsureBinaryAsync(WhisperBackend backend, CancellationToken ct)
    {
        // Operator bring-your-own override: an active override short-circuits ALL acquisition (no download, no cache
        // write). The supplied binary is validated and served as the override's OWN backend, never the caller's. A
        // configured-but-broken override throws sanitized rather than falling through to acquisition.
        if (_overrideOptions?.IsActive == true)
        {
            return ResolveOverrideBinary(_overrideOptions);
        }

        var installed = _installedRuntimeStore is null
            ? null
            : await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        if (installed is not null)
        {
            // An installed record is AUTHORITATIVE, including a tombstone. Falling back to a prebuilt here would serve
            // bytes that contradict what the operator selected and what the UI reports.
            if (installed.DesiredBackend != backend)
            {
                throw new WhisperRuntimeException("The selected managed whisper.cpp runtime backend is unavailable.");
            }

            if (installed.Validity != WhisperInstalledRuntimeValidity.Active)
            {
                throw new WhisperRuntimeException("The recorded managed whisper.cpp runtime is invalid and must be rebuilt or removed.");
            }

            var managed = await TryResolveManagedRuntimeAsync(installed, ct).ConfigureAwait(false);
            if (managed is not null)
            {
                return managed;
            }

            throw new WhisperRuntimeException("The recorded managed whisper.cpp runtime is unavailable or failed validation.");
        }

        // A CPU request may degrade to the host's CPU floor; a GPU request must resolve exactly, so acquired bytes can
        // never contradict the requested backend.
        var pin = _assetPinOverride
                  ?? (backend == WhisperBackend.Cpu
                      ? WhisperCppReleasePins.Resolve(_os, _arch, backend)
                      : WhisperCppReleasePins.ResolveExact(_os, _arch, backend))
                  ?? throw new WhisperRuntimeException("No prebuilt whisper.cpp runtime is available for this operating system and CPU architecture.");

        var backendDir = Path.Combine(_cacheRoot, "whisper.cpp", _activeTag, BackendSlug(backend));

        var cachedServer = ResolveServerPath(backendDir, pin);
        if (cachedServer is not null)
        {
            EnsureExecutable(cachedServer);
            return new WhisperBinary(cachedServer, _activeTag, backend, IsPinnedFallback: true);
        }

        await DownloadVerifyExtractAsync(WhisperCppReleasePins.DownloadUri(_activeTag, pin.AssetName),
            pin.AssetName,
            pin.Sha256,
            pin.ArchiveKind,
            backendDir,
            ct).ConfigureAwait(false);

        var serverPath = ResolveServerPath(backendDir, pin)
                         ?? throw new WhisperRuntimeException("The downloaded whisper.cpp runtime did not contain the expected server executable.");

        EnsureExecutable(serverPath);
        return new WhisperBinary(serverPath, _activeTag, backend, IsPinnedFallback: true);
    }

    /// <summary>
    ///     The directory every acquired whisper.cpp runtime is cached under for the default app-data root. Exposed so
    ///     the startup orphan reaper matches ONLY <c>whisper-server</c> binaries this app acquired, never an unrelated
    ///     install.
    /// </summary>
    internal static string DefaultWhisperBinariesRoot()
    {
        return Path.Combine(DefaultCacheRoot(), "whisper.cpp");
    }

    private async Task<WhisperBinary?> TryResolveManagedRuntimeAsync(WhisperInstalledRuntimeState state, CancellationToken ct)
    {
        if (state.SourceBuildPath is not { Length: > 0 } buildPath
            || state.ServerSha256 is not { Length: 64 } expectedSha
            || !expectedSha.All(Uri.IsHexDigit))
        {
            await TombstoneAsync(state, "The managed runtime record is incomplete.", ct).ConfigureAwait(false);
            return null;
        }

        try
        {
            var serverPath = Path.Combine(buildPath, OperatingSystem.IsWindows() ? "whisper-server.exe" : "whisper-server");
            EnsureManagedPathSecure(serverPath);
            if (!await HashMatchesAsync(serverPath, expectedSha, ct).ConfigureAwait(false))
            {
                await TombstoneAsync(state, "The managed runtime binary failed integrity verification.", ct).ConfigureAwait(false);
                return null;
            }

            _managedSourceSignal?.SetActive(state.DesiredBackend);
            return new WhisperBinary(serverPath, state.SourceCommit, state.DesiredBackend, IsPinnedFallback: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                             or UnauthorizedAccessException
                                             or ArgumentException
                                             or NotSupportedException
                                             or WhisperRuntimeException)
        {
            await TombstoneAsync(state, "The managed runtime path failed security validation.", ct).ConfigureAwait(false);
            return null;
        }
    }

    private async Task TombstoneAsync(WhisperInstalledRuntimeState state, string reason, CancellationToken ct)
    {
        if (_installedRuntimeStore is not null)
        {
            await _installedRuntimeStore.WriteAsync(state with
            {
                Validity = WhisperInstalledRuntimeValidity.Invalid,
                InvalidReason = reason,
                SourceBuildPath = null,
                ServerSha256 = null
            }, ct).ConfigureAwait(false);
        }

        // The tombstone stays authoritative and fail-closed in the store, but it must stop advertising an active
        // managed backend to the selector — otherwise the stale in-memory signal keeps steering selection toward a
        // runtime this method has just proven unusable.
        _managedSourceSignal?.Clear();
    }

    private void EnsureManagedPathSecure(string serverPath)
    {
        var root = Path.GetFullPath(_cacheRoot);
        var full = Path.GetFullPath(serverPath);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !File.Exists(full)
            || new FileInfo(full).LinkTarget is not null)
        {
            throw new WhisperRuntimeException("The managed whisper.cpp runtime path is invalid.");
        }

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var fileMode = File.GetUnixFileMode(full);
        if ((fileMode & UnixFileMode.OtherWrite) != UnixFileMode.None
            || (fileMode & UnixFileMode.UserExecute) == UnixFileMode.None)
        {
            throw new WhisperRuntimeException("The managed whisper.cpp runtime permissions are insecure.");
        }

        var directory = Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(directory) && directory.Length >= root.Length)
        {
            var info = new DirectoryInfo(directory);
            if (info.LinkTarget is not null
                || (File.GetUnixFileMode(directory) & UnixFileMode.OtherWrite) != UnixFileMode.None)
            {
                throw new WhisperRuntimeException("The managed whisper.cpp runtime path chain is insecure.");
            }

            if (string.Equals(directory, root, StringComparison.Ordinal))
            {
                break;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    /// <summary>
    ///     Validates and serves the operator bring-your-own <c>whisper-server</c>. Operator-trust channel: the path
    ///     must be an existing regular file and, off Windows, must carry the user-execute bit — a path that is present
    ///     but not runnable would otherwise surface later as an opaque spawn failure. A missing or unusable override
    ///     throws sanitized rather than silently degrading to acquisition.
    /// </summary>
    private static WhisperBinary ResolveOverrideBinary(WhisperServerRuntimeOverrideOptions overrideOptions)
    {
        var serverPath = overrideOptions.ServerPath;
        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
        {
            throw new WhisperRuntimeException("The configured bring-your-own whisper.cpp server path does not point to an existing file.");
        }

        var fullPath = Path.GetFullPath(serverPath);
        if (!OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(fullPath) & UnixFileMode.UserExecute) == UnixFileMode.None)
        {
            throw new WhisperRuntimeException("The configured bring-your-own whisper.cpp server path is not executable.");
        }

        return new WhisperBinary(fullPath, "byo", overrideOptions.Backend, IsPinnedFallback: false);
    }

    /// <summary>
    ///     Locates <c>whisper-server</c> inside an extracted backend directory. The pinned relative path is tried
    ///     first; a recursive search by file name tolerates an upstream layout change. <c>whisper-cli</c> may or may
    ///     not sit beside it, and its presence is never a precondition.
    /// </summary>
    private static string? ResolveServerPath(string backendDir, WhisperAssetPin pin)
    {
        var pinned = Path.GetFullPath(Path.Combine(backendDir, pin.ServerRelativePath));
        if (File.Exists(pinned))
        {
            return pinned;
        }

        if (!Directory.Exists(backendDir))
        {
            return null;
        }

        var serverFileName = Path.GetFileName(pin.ServerRelativePath);
        return Directory.EnumerateFiles(backendDir, serverFileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>
    ///     Shared download, SHA256-verify and atomic-extract pipeline against the pinned hash. A transient failure or a
    ///     hash mismatch is discarded and retried exactly once, then surfaced sanitized.
    /// </summary>
    private async Task DownloadVerifyExtractAsync(Uri url,
        string assetName,
        string expectedSha256,
        WhisperArchiveKind archiveKind,
        string backendDir,
        CancellationToken ct)
    {
        var firstError = await TryDownloadVerifyExtractAsync(url, assetName, expectedSha256, archiveKind, backendDir, ct).ConfigureAwait(false);
        if (firstError is null)
        {
            return;
        }

        var secondError = await TryDownloadVerifyExtractAsync(url, assetName, expectedSha256, archiveKind, backendDir, ct).ConfigureAwait(false);
        if (secondError is null)
        {
            return;
        }

        throw new WhisperRuntimeException(
            "The whisper.cpp runtime could not be downloaded or failed integrity verification after a retry. "
            + "Check the network connection and try again.",
            secondError);
    }

    private async Task<Exception?> TryDownloadVerifyExtractAsync(Uri url,
        string assetName,
        string expectedSha256,
        WhisperArchiveKind archiveKind,
        string backendDir,
        CancellationToken ct)
    {
        // Defense in depth: strip any directory component before composing a temp path, so an asset name can never traverse.
        var tempArchive = Path.Combine(Path.GetTempPath(), $"whispercpp-{Guid.NewGuid():N}-{Path.GetFileName(assetName)}");
        try
        {
            await DownloadToFileAsync(url, tempArchive, ct).ConfigureAwait(false);

            if (!await HashMatchesAsync(tempArchive, expectedSha256, ct).ConfigureAwait(false))
            {
                return new WhisperRuntimeException("The whisper.cpp runtime download failed integrity verification.");
            }

            await ExtractArchiveAsync(tempArchive, archiveKind, backendDir, ct).ConfigureAwait(false);
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
                if (written > MaxDownloadBytes)
                {
                    throw new WhisperRuntimeException("The whisper.cpp runtime download exceeded the maximum allowed size.");
                }

                await target.WriteAsync(buffer.AsMemory(start: 0, read), ct).ConfigureAwait(false);
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
        var actual = Convert.ToHexStringLower(hash);
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Extracts into a temp sibling then atomically moves it into place, so a partial extract can never masquerade
    ///     as a cached install. Dispatches on the archive kind because whisper.cpp ships zip on Windows and tar.gz on
    ///     Linux — unlike stable-diffusion.cpp, which is zip everywhere.
    /// </summary>
    private static async Task ExtractArchiveAsync(string archivePath, WhisperArchiveKind archiveKind, string backendDir, CancellationToken ct)
    {
        var stagingDir = $"{backendDir}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(stagingDir);
        try
        {
            switch (archiveKind)
            {
                case WhisperArchiveKind.Zip:
                    await ZipFile.ExtractToDirectoryAsync(archivePath, stagingDir, ct).ConfigureAwait(false);
                    break;
                case WhisperArchiveKind.TarGz:
                    await using (var compressed = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await using (var decompressed = new GZipStream(compressed, CompressionMode.Decompress))
                    {
                        await TarFile.ExtractToDirectoryAsync(decompressed, stagingDir, overwriteFiles: true, ct).ConfigureAwait(false);
                    }

                    break;
                default:
                    throw new WhisperRuntimeException("The pinned whisper.cpp asset declares an unknown archive format.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(backendDir.TrimEnd(Path.DirectorySeparatorChar))!);
            if (Directory.Exists(backendDir))
            {
                Directory.Delete(backendDir, recursive: true);
            }

            Directory.Move(stagingDir, backendDir);
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
        }
    }

    /// <summary>
    ///     Sets the user-execute bit off Windows. A tar.gz preserves modes, but a zip does not carry any, so the
    ///     Windows-built archives would extract a non-executable file on a Unix host. The call is cheap and idempotent.
    /// </summary>
    private static void EnsureExecutable(string serverPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(serverPath);
            if ((mode & UnixFileMode.UserExecute) == UnixFileMode.None)
            {
                File.SetUnixFileMode(serverPath, mode | UnixFileMode.UserExecute);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new WhisperRuntimeException("The acquired whisper.cpp runtime could not be made executable.", exception);
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
            // Best-effort cleanup of a temp download; never mask the original failure.
        }
    }

    private static string BackendSlug(WhisperBackend backend)
    {
        return backend == WhisperBackend.Cuda ? "cuda" : "cpu";
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XE-Local-AI-Engine");
    }

    private static OSPlatform CurrentOsPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return OSPlatform.Windows;
        }

        return OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
    }
}
