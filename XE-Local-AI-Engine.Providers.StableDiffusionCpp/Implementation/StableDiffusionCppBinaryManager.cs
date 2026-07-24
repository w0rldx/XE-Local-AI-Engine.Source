namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Configuration;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Default <see cref="IStableDiffusionBinaryManager" />: resolves the pinned prebuilt asset for the host, downloads
///     it over HTTP, verifies its SHA256 against <see cref="StableDiffusionReleasePins" />, extracts it under a stable
///     cache directory, and returns the resolved <c>sd-server</c> path. Never source-builds. Mirrors
///     <c>LlamaCppBinaryManager</c>'s acquisition pipeline for the image runtime.
/// </summary>
/// <remarks>
///     <para>
///         Cache layout: <c>{cacheRoot}/stable-diffusion.cpp/{tag}/{backend}/</c> holds the extracted archive; a cached
///         binary is reused without re-download (offline path).
///     </para>
///     <para>
///         On SHA256 mismatch the partial download is discarded and retried <em>once</em>; a second mismatch surfaces a
///         sanitized <see cref="StableDiffusionRuntimeException" /> (no internal paths/URLs in the message).
///     </para>
/// </remarks>
public sealed class StableDiffusionCppBinaryManager : IStableDiffusionBinaryManager
{
    /// <summary>
    ///     Absolute hard ceiling on a single runtime download. A prebuilt sd-server asset (incl. the ~560 MB Windows
    ///     cudart companion) is well under this; the cap is a disk-exhaustion guard against a hostile/buggy server
    ///     streaming an unbounded body.
    /// </summary>
    private const long MaxDownloadBytes = 3L * 1024 * 1024 * 1024;

    private readonly string _activeTag;
    private readonly Architecture _arch;
    private readonly string _cacheRoot;
    private readonly HttpClient _httpClient;
    private readonly OSPlatform _os;
    private readonly StableDiffusionServerRuntimeOverrideOptions? _overrideOptions;

    /// <summary>
    ///     Creates a binary manager that downloads through <paramref name="httpClient" /> and caches under
    ///     <paramref name="cacheRoot" />. The optional <paramref name="overrideOptions" /> carries the operator
    ///     bring-your-own override; when active, <see cref="EnsureBinaryAsync" /> validates and serves the supplied binary
    ///     instead of acquiring one.
    /// </summary>
    public StableDiffusionCppBinaryManager(HttpClient httpClient,
        string? cacheRoot = null,
        string? activeTag = null,
        StableDiffusionServerRuntimeOverrideOptions? overrideOptions = null)
        : this(httpClient,
            cacheRoot ?? DefaultCacheRoot(),
            activeTag ?? StableDiffusionReleasePins.PinnedTag,
            CurrentOsPlatform(),
            RuntimeInformation.ProcessArchitecture,
            overrideOptions)
    {
    }

    /// <summary>Test seam: pins OS/arch so asset selection can be exercised on any host.</summary>
    internal StableDiffusionCppBinaryManager(HttpClient httpClient,
        string cacheRoot,
        string activeTag,
        OSPlatform os,
        Architecture arch,
        StableDiffusionServerRuntimeOverrideOptions? overrideOptions = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(activeTag);
        _cacheRoot = cacheRoot;
        _activeTag = activeTag;
        _os = os;
        _arch = arch;
        _overrideOptions = overrideOptions;
    }

    /// <inheritdoc />
    public async Task<SdBinary> EnsureBinaryAsync(SdGpuBackend backend, CancellationToken ct)
    {
        // Operator bring-your-own override: an active override short-circuits ALL acquisition (no download, no cache
        // write). The supplied binary is validated and served as the override's OWN backend — never the caller-passed
        // backend. A configured-but-broken override throws a sanitized failure rather than falling through to acquisition.
        if (_overrideOptions?.IsActive == true)
        {
            return ResolveOverrideBinary(_overrideOptions);
        }

        var pin = StableDiffusionReleasePins.Resolve(_os, _arch, backend)
                  ?? throw new StableDiffusionRuntimeException("No prebuilt stable-diffusion.cpp runtime is available for this operating system and CPU architecture.");

        var backendDir = Path.Combine(_cacheRoot, "stable-diffusion.cpp", _activeTag, BackendSlug(backend));

        // Offline / already-cached path: reuse a present binary without re-download.
        var cachedServer = ResolveServerPath(backendDir, pin);
        if (cachedServer is not null)
        {
            // Idempotent: tops up the cudart DLLs on a Windows-CUDA dir that is somehow missing them; a no-op otherwise.
            await EnsureCudartRuntimeAsync(pin, backend, backendDir, cachedServer, ct).ConfigureAwait(false);
            return new SdBinary(cachedServer, _activeTag, backend, IsPinnedFallback: true);
        }

        await DownloadVerifyExtractAsync(StableDiffusionReleasePins.DownloadUri(_activeTag, pin.AssetName), pin.AssetName, pin.Sha256, backendDir, ct).ConfigureAwait(false);

        var serverPath = ResolveServerPath(backendDir, pin)
                         ?? throw new StableDiffusionRuntimeException("The downloaded stable-diffusion.cpp runtime did not contain the expected server executable.");

        // Pair the CUDA runtime DLLs (pinned companion) before the binary is served — a CUDA build without its cudart
        // archive silently degrades to CPU-only. A cudart failure deletes the half-CUDA backend dir and throws.
        await EnsureCudartRuntimeAsync(pin, backend, backendDir, serverPath, ct).ConfigureAwait(false);

        return new SdBinary(serverPath, _activeTag, backend, IsPinnedFallback: true);
    }

    /// <summary>
    ///     Validates and serves the operator bring-your-own <c>sd-server</c>. Operator-trust channel (env var only): the
    ///     path is checked to be a present regular file — full smoke/GPU validation happens when the runtime spawns it.
    ///     A missing/invalid path throws a sanitized failure rather than silently degrading to acquisition.
    /// </summary>
    private static SdBinary ResolveOverrideBinary(StableDiffusionServerRuntimeOverrideOptions overrideOptions)
    {
        var serverPath = overrideOptions.ServerPath;
        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
        {
            throw new StableDiffusionRuntimeException("The configured bring-your-own stable-diffusion.cpp server path does not point to an existing file.");
        }

        return new SdBinary(Path.GetFullPath(serverPath), "byo", overrideOptions.Backend, IsPinnedFallback: false);
    }

    /// <summary>
    ///     Pairs the Windows-CUDA runtime DLLs (<c>cudart64_*.dll</c>, <c>cublas64_*.dll</c>, …) next to
    ///     <c>sd-server.exe</c>. stable-diffusion.cpp ships these in a SEPARATE archive from the main CUDA build; without
    ///     them the CUDA backend fails to load and the server silently runs CPU-only. No-op for every non-Windows-CUDA
    ///     acquisition. Idempotent: if the DLLs already sit next to the server nothing is downloaded.
    /// </summary>
    private async Task EnsureCudartRuntimeAsync(StableDiffusionAssetPin pin, SdGpuBackend backend, string backendDir, string serverPath, CancellationToken ct)
    {
        if (backend != SdGpuBackend.Cuda || _os != OSPlatform.Windows)
        {
            return;
        }

        var serverDir = Path.GetDirectoryName(serverPath);
        if (string.IsNullOrEmpty(serverDir))
        {
            throw new StableDiffusionRuntimeException("The stable-diffusion.cpp CUDA runtime could not be installed (server directory is unresolved).");
        }

        if (CudartRuntimePresent(serverDir))
        {
            return;
        }

        if (pin.CudartAssetName is not { Length: > 0 } cudartName || pin.CudartSha256 is not { Length: > 0 } cudartDigest)
        {
            throw new StableDiffusionRuntimeException("The pinned stable-diffusion.cpp CUDA runtime is missing its companion runtime archive metadata.");
        }

        try
        {
            await DownloadVerifyFlattenCudartAsync(StableDiffusionReleasePins.DownloadUri(_activeTag, cudartName), cudartName, cudartDigest, serverDir, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A half-CUDA dir (main archive extracted, runtime DLLs missing) must not survive to look like a valid CUDA
            // install on a later resolve — discard it so the next acquisition re-runs the complete pairing.
            TryDeleteDirectory(backendDir);
            throw;
        }

        if (!CudartRuntimePresent(serverDir))
        {
            TryDeleteDirectory(backendDir);
            throw new StableDiffusionRuntimeException("The stable-diffusion.cpp CUDA runtime archive did not contain the expected runtime libraries.");
        }
    }

    private static bool CudartRuntimePresent(string serverDir)
    {
        return Directory.Exists(serverDir)
               && Directory.EnumerateFiles(serverDir, "cudart64_*.dll", SearchOption.TopDirectoryOnly).Any();
    }

    private async Task DownloadVerifyFlattenCudartAsync(Uri url, string assetName, string expectedSha256, string serverDir, CancellationToken ct)
    {
        var firstError = await TryDownloadVerifyFlattenCudartAsync(url, assetName, expectedSha256, serverDir, ct).ConfigureAwait(false);
        if (firstError is null)
        {
            return;
        }

        var secondError = await TryDownloadVerifyFlattenCudartAsync(url, assetName, expectedSha256, serverDir, ct).ConfigureAwait(false);
        if (secondError is null)
        {
            return;
        }

        throw new StableDiffusionRuntimeException("The stable-diffusion.cpp CUDA runtime archive could not be downloaded or failed integrity verification after a retry.", secondError);
    }

    private async Task<Exception?> TryDownloadVerifyFlattenCudartAsync(Uri url, string assetName, string expectedSha256, string serverDir, CancellationToken ct)
    {
        var tempArchive = Path.Combine(Path.GetTempPath(), $"sdcpp-cudart-{Guid.NewGuid():N}-{Path.GetFileName(assetName)}");
        var stagingDir = Path.Combine(Path.GetTempPath(), $"sdcpp-cudart-{Guid.NewGuid():N}");
        try
        {
            await DownloadToFileAsync(url, tempArchive, ct).ConfigureAwait(false);

            if (!await HashMatchesAsync(tempArchive, expectedSha256, ct).ConfigureAwait(false))
            {
                return new StableDiffusionRuntimeException("The stable-diffusion.cpp CUDA runtime archive failed integrity verification.");
            }

            Directory.CreateDirectory(stagingDir);
            await ZipFile.ExtractToDirectoryAsync(tempArchive, stagingDir, ct).ConfigureAwait(false);
            FlattenDllsInto(stagingDir, serverDir);
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
            TryDeleteDirectory(stagingDir);
        }
    }

    private static void FlattenDllsInto(string sourceRoot, string serverDir)
    {
        Directory.CreateDirectory(serverDir);
        foreach (var dll in Directory.EnumerateFiles(sourceRoot, "*.dll", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(serverDir, Path.GetFileName(dll));
            File.Copy(dll, destination, overwrite: true);
        }
    }

    /// <summary>
    ///     Locates the <c>sd-server</c> executable inside an extracted backend directory. The pinned relative path (a
    ///     bare file name at the archive root) is tried first; a recursive fall-back search by file name tolerates an
    ///     upstream layout change. Returns <see langword="null" /> when no executable of that name exists.
    /// </summary>
    private static string? ResolveServerPath(string backendDir, StableDiffusionAssetPin pin)
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
    ///     Shared download → SHA256-verify → atomic-extract pipeline against the pinned hash. A transient failure or a
    ///     hash mismatch is discarded and retried exactly once, then surfaced sanitized.
    /// </summary>
    private async Task DownloadVerifyExtractAsync(Uri url, string assetName, string expectedSha256, string backendDir, CancellationToken ct)
    {
        var firstError = await TryDownloadVerifyExtractAsync(url, assetName, expectedSha256, backendDir, ct).ConfigureAwait(false);
        if (firstError is null)
        {
            return;
        }

        var secondError = await TryDownloadVerifyExtractAsync(url, assetName, expectedSha256, backendDir, ct).ConfigureAwait(false);
        if (secondError is null)
        {
            return;
        }

        throw new StableDiffusionRuntimeException("The stable-diffusion.cpp runtime could not be downloaded or failed integrity verification after a retry. "
                                                  + "Check the network connection and try again.",
            secondError);
    }

    private async Task<Exception?> TryDownloadVerifyExtractAsync(Uri url, string assetName, string expectedSha256, string backendDir, CancellationToken ct)
    {
        // Defense-in-depth: strip any directory component before it composes a temp path so a name can never traverse.
        var tempArchive = Path.Combine(Path.GetTempPath(), $"sdcpp-{Guid.NewGuid():N}-{Path.GetFileName(assetName)}");
        try
        {
            await DownloadToFileAsync(url, tempArchive, ct).ConfigureAwait(false);

            if (!await HashMatchesAsync(tempArchive, expectedSha256, ct).ConfigureAwait(false))
            {
                return new StableDiffusionRuntimeException("The stable-diffusion.cpp runtime download failed integrity verification.");
            }

            ExtractArchive(tempArchive, backendDir);
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
                    throw new StableDiffusionRuntimeException("The stable-diffusion.cpp runtime download exceeded the maximum allowed size.");
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
        var actual = Convert.ToHexStringLower(hash);
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractArchive(string archivePath, string backendDir)
    {
        // Every stable-diffusion.cpp asset is a .zip (unlike llama.cpp's tar.gz on Linux). Extract into a temp sibling
        // then atomically move into place so a partial extract can't masquerade as a cached install.
        var stagingDir = $"{backendDir}.{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(stagingDir);
        try
        {
            ZipFile.ExtractToDirectory(archivePath, stagingDir);

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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a failed install; never mask the original failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a failed install; never mask the original failure.
        }
    }

    private static string BackendSlug(SdGpuBackend backend)
    {
        return backend switch
        {
            SdGpuBackend.Cuda => "cuda",
            SdGpuBackend.Vulkan => "vulkan",
            _ => "cpu"
        };
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine");
    }

    /// <summary>
    ///     The directory every acquired stable-diffusion.cpp runtime is cached under for the default app-data root
    ///     (<c>{cacheRoot}/stable-diffusion.cpp</c>, the same layout <see cref="EnsureBinaryAsync" /> writes its backend
    ///     dirs into). Exposed so the startup orphan reaper matches ONLY <c>sd-server</c> binaries this app acquired,
    ///     never an unrelated install.
    /// </summary>
    internal static string DefaultStableDiffusionBinariesRoot()
    {
        return Path.Combine(DefaultCacheRoot(), "stable-diffusion.cpp");
    }

    private static OSPlatform CurrentOsPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return OSPlatform.Windows;
        }

        if (OperatingSystem.IsMacOS())
        {
            return OSPlatform.OSX;
        }

        return OSPlatform.Linux;
    }
}
