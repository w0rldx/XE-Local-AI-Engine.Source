namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IConvertScriptProvisioner" />. Fetches the llama.cpp tree at the engine-pinned commit into an
///     owner-only work directory, copies out exactly the three paths a conversion needs, and swaps the result into a
///     commit-named directory with a single atomic move.
/// </summary>
/// <remarks>
///     <para>
///         Adoption is commit-addressed (<c>convert-scripts/{commit}/</c>), so re-pinning the runtime provisions a new
///         directory rather than mutating the one an in-flight export is reading from.
///     </para>
///     <para>
///         Only the three needed paths are copied. The rest of the fetched tree — the whole C++ source, tests, and
///         vendored dependencies — is discarded with the work directory, so an adopted tree is small and contains
///         nothing executable beyond the two scripts.
///     </para>
/// </remarks>
public sealed class ConvertScriptProvisioner : IConvertScriptProvisioner, IDisposable
{
    private const string GgufPyDirectoryName = "gguf-py";
    private const string HfToGgufScriptName = "convert_hf_to_gguf.py";
    private const string LoraToGgufScriptName = "convert_lora_to_gguf.py";

    private readonly string _cacheRoot;
    private readonly IConvertScriptSourceFetcher _fetcher;
    private readonly ILogger<ConvertScriptProvisioner> _logger;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);

    /// <summary>Creates a provisioner rooted at the shared managed-runtime cache.</summary>
    public ConvertScriptProvisioner(IConvertScriptSourceFetcher fetcher, ILogger<ConvertScriptProvisioner> logger)
        : this(fetcher, logger, DefaultCacheRoot())
    {
    }

    /// <summary>Test seam: pins the cache root.</summary>
    internal ConvertScriptProvisioner(IConvertScriptSourceFetcher fetcher,
        ILogger<ConvertScriptProvisioner> logger,
        string cacheRoot)
    {
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
    }

    private string ScriptsRoot => Path.Combine(_cacheRoot, "llama.cpp", "convert-scripts");

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    /// <inheritdoc />
    public ConvertScriptPaths? TryResolve()
    {
        return TryResolve(LlamaCppReleasePins.PinnedSourceCommitSha);
    }

    /// <inheritdoc />
    public async Task<ConvertScriptPaths> EnsureAsync(CancellationToken ct)
    {
        var commit = LlamaCppReleasePins.PinnedSourceCommitSha;
        if (TryResolve(commit) is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: a concurrent caller may have adopted while this one waited.
            if (TryResolve(commit) is { } adopted)
            {
                return adopted;
            }

            return await FetchAndAdoptAsync(commit, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    private ConvertScriptPaths? TryResolve(string commit)
    {
        var final = Path.Combine(ScriptsRoot, commit);
        var paths = new ConvertScriptPaths(Path.Combine(final, HfToGgufScriptName),
            Path.Combine(final, LoraToGgufScriptName),
            Path.Combine(final, GgufPyDirectoryName),
            commit);
        return File.Exists(paths.HfToGgufScriptPath)
               && File.Exists(paths.LoraToGgufScriptPath)
               && Directory.Exists(paths.GgufPyDirectory)
            ? paths
            : null;
    }

    private async Task<ConvertScriptPaths> FetchAndAdoptAsync(string commit, CancellationToken ct)
    {
        var workDirectory = Path.Combine(ScriptsRoot, ".work");
        var stagingDirectory = Path.Combine(ScriptsRoot, ".staging");
        var finalDirectory = Path.Combine(ScriptsRoot, commit);
        var fetchDirectory = Path.Combine(workDirectory, "llama.cpp");

        try
        {
            TryDeleteDirectory(workDirectory);
            TryDeleteDirectory(stagingDirectory);
            CreateOwnerOnlyDirectory(ScriptsRoot);
            CreateOwnerOnlyDirectory(workDirectory);
            CreateOwnerOnlyDirectory(fetchDirectory);

            var fetched = await _fetcher.FetchAsync(fetchDirectory, commit, ct).ConfigureAwait(false);
            if (!string.Equals(fetched, commit, StringComparison.OrdinalIgnoreCase))
            {
                throw new LlamaRuntimeException("The fetched llama.cpp conversion scripts did not match the pinned commit.");
            }

            CreateOwnerOnlyDirectory(stagingDirectory);
            CopyRequiredFile(fetchDirectory, stagingDirectory, HfToGgufScriptName);
            CopyRequiredFile(fetchDirectory, stagingDirectory, LoraToGgufScriptName);
            CopyRequiredDirectory(Path.Combine(fetchDirectory, GgufPyDirectoryName),
                Path.Combine(stagingDirectory, GgufPyDirectoryName));

            // Same-parent move → atomic: the commit directory appears complete or not at all, so a crash mid-copy can
            // never leave TryResolve reporting a half-populated tree as provisioned.
            TryDeleteDirectory(finalDirectory);
            Directory.Move(stagingDirectory, finalDirectory);
            _logger.LogInformation("Provisioned the llama.cpp conversion scripts at commit {Commit}.", commit);

            return TryResolve(commit)
                   ?? throw new LlamaRuntimeException("The provisioned llama.cpp conversion scripts are incomplete.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(exception, "Provisioning the llama.cpp conversion scripts failed.");
            throw new LlamaRuntimeException("The llama.cpp conversion scripts could not be provisioned.", exception);
        }
        finally
        {
            // Both are no-ops on success: the work tree is disposable and the staging directory has already been moved
            // out from under this path. On ANY failure they are what stops a partial tree from being seen as adopted.
            TryDeleteDirectory(workDirectory);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static void CopyRequiredFile(string sourceDirectory, string destinationDirectory, string fileName)
    {
        var source = Path.Combine(sourceDirectory, fileName);
        if (!File.Exists(source))
        {
            throw new LlamaRuntimeException($"The fetched llama.cpp source does not contain {fileName}.");
        }

        File.Copy(source, Path.Combine(destinationDirectory, fileName), overwrite: true);
    }

    private static void CopyRequiredDirectory(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            throw new LlamaRuntimeException($"The fetched llama.cpp source does not contain {Path.GetFileName(source)}.");
        }

        CreateOwnerOnlyDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            CreateOwnerOnlyDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private static void CreateOwnerOnlyDirectory(string path)
    {
        _ = Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
            // Best effort: a leftover work/staging directory is reclaimed by the next provisioning attempt.
        }
    }

    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine");
    }
}
