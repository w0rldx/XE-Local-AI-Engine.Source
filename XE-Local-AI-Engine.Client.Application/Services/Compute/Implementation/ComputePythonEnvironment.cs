namespace XE_Local_AI_Engine.Client.Services.Compute.Implementation;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Training;
using XE_Local_AI_Engine.Providers.Training.Implementation;

/// <summary>
///     Provisions and caches the compute tool's uv-managed Python venv (numpy / scipy / sympy, from the committed
///     <c>tools/compute/pyproject.toml</c> + <c>uv.lock</c>).
/// </summary>
/// <remarks>
///     <para>
///         The acquisition pipeline is the training runtime's, reused rather than reimplemented: the same digest-pinned
///         uv binary (<see cref="UvBinaryAcquirer" />), the same scrubbed uv environment, and the same tree-killed
///         subprocess spawn. Only the lockfile and the cache root differ — which is the whole point of a second closure,
///         since this one carries no torch and provisions in seconds rather than minutes.
///     </para>
///     <para>
///         Provisioning is single-flight and its result is cached for the process lifetime, so a research loop calling
///         the tool ten times in a turn pays the check once. The cached answer is invalidated by the LOCKFILE DIGEST,
///         not merely by the interpreter's existence: a lockfile bump in a new build must re-sync rather than keep
///         serving the previous closure, and comparing digests is the only way to notice that without re-running uv.
///     </para>
/// </remarks>
internal sealed class ComputePythonEnvironment : IComputePythonEnvironment, IDisposable
{
    private const string ProjectFileName = "pyproject.toml";
    private const string LockfileName = "uv.lock";
    private const string StateFileName = "installed-compute-lock.sha256";

    /// <summary>The name the shipped compute project files are linked under in the publish output (see the Client csproj).</summary>
    private const string PublishedScriptsDirectoryName = "compute-scripts";

    /// <summary>The repo-relative source of the same files, used by dev and test runs.</summary>
    private const string RepositoryScriptsRelativePath = "tools/compute";

    // Generous next to the closure's real cost (~10s warm cache, ~60s cold on a slow link), because the alternative to
    // waiting is a provision that is killed halfway and re-run from scratch on the next call.
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(10);

    private readonly UvBinaryAcquirer _acquirer;
    private readonly string _cacheRoot;
    private readonly ILogger<ComputePythonEnvironment> _logger;
    private readonly ITrainingProcessRunner _processRunner;
    private readonly SemaphoreSlim _provisionGate = new(1, 1);
    private readonly string _scriptsDirectory;

    private string? _interpreterPath;

    public ComputePythonEnvironment(HttpClient httpClient, ILogger<ComputePythonEnvironment> logger)
        : this(new UvBinaryAcquirer(httpClient), new LinuxTrainingProcessRunner(), logger, DefaultCacheRoot(), ResolveScriptsDirectory())
    {
    }

    /// <summary>Test seam: pins the cache root, the project-files directory, and the subprocess runner.</summary>
    internal ComputePythonEnvironment(UvBinaryAcquirer acquirer,
        ITrainingProcessRunner processRunner,
        ILogger<ComputePythonEnvironment> logger,
        string cacheRoot,
        string scriptsDirectory)
    {
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsDirectory);
        _cacheRoot = cacheRoot;
        _scriptsDirectory = scriptsDirectory;
    }

    public void Dispose()
    {
        _provisionGate.Dispose();
    }

    public async Task<string> GetInterpreterPathAsync(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _interpreterPath);
        if (cached is not null)
        {
            return cached;
        }

        // The uv binary pin and the process runner are both Linux-x64, and the lockfile resolves for that platform
        // alone, so there is nothing to provision elsewhere. Refused with a plain sentence rather than left to fail
        // deeper as a missing-file error naming a path the model must not see.
        if (!OperatingSystem.IsLinux())
        {
            throw new ComputeEnvironmentException("The Python compute tool is available on Linux only.");
        }

        await _provisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = Volatile.Read(ref _interpreterPath);
            if (cached is not null)
            {
                return cached;
            }

            var resolved = await ProvisionAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _interpreterPath, resolved);
            return resolved;
        }
        finally
        {
            _provisionGate.Release();
        }
    }

    private async Task<string> ProvisionAsync(CancellationToken cancellationToken)
    {
        var project = Path.Combine(_scriptsDirectory, ProjectFileName);
        var lockfile = Path.Combine(_scriptsDirectory, LockfileName);
        if (!File.Exists(project) || !File.Exists(lockfile))
        {
            throw new ComputeEnvironmentException("The pinned compute runtime lockfile is missing from this installation.");
        }

        var lockfileSha = await ComputeFileShaAsync(lockfile, cancellationToken).ConfigureAwait(false);
        var venvDirectory = Path.Combine(_cacheRoot, "venv");
        var interpreter = Path.Combine(venvDirectory, ".venv", "bin", "python");
        var statePath = Path.Combine(_cacheRoot, StateFileName);
        if (File.Exists(interpreter) && MatchesInstalledLock(statePath, lockfileSha))
        {
            return interpreter;
        }

        _logger.LogInformation("Provisioning the compute Python runtime from the pinned lockfile.");

        var workDirectory = Path.Combine(_cacheRoot, ".work");
        var isolatedHome = Path.Combine(workDirectory, ".home");
        var isolatedTmp = Path.Combine(workDirectory, ".tmp");
        CreateOwnerOnlyDirectory(_cacheRoot);
        CreateOwnerOnlyDirectory(workDirectory);
        CreateOwnerOnlyDirectory(isolatedHome);
        CreateOwnerOnlyDirectory(isolatedTmp);
        CreateOwnerOnlyDirectory(venvDirectory);

        var uv = await _acquirer.EnsureUvAsync(_cacheRoot, LogLine, cancellationToken).ConfigureAwait(false);

        // uv resolves the environment beside the pyproject it is pointed at, so the committed pair is copied into the
        // venv directory rather than the shipped (read-only) scripts directory being used as a working tree.
        File.Copy(project, Path.Combine(venvDirectory, ProjectFileName), overwrite: true);
        File.Copy(lockfile, Path.Combine(venvDirectory, LockfileName), overwrite: true);

        // --locked makes uv fail rather than re-resolve when the lockfile and pyproject.toml disagree, which is what
        // makes this reproducible instead of merely repeatable.
        var syncExit = await _processRunner.RunAsync(uv,
            ["sync", "--locked", "--project", venvDirectory],
            TrainingRuntimeEnvironment.BuildUvEnvironment(isolatedHome,
                isolatedTmp,
                Path.Combine(_cacheRoot, "uv-cache"),
                Path.Combine(_cacheRoot, "pythons")),
            venvDirectory,
            LogLine,
            SyncTimeout,
            cancellationToken).ConfigureAwait(false);
        if (syncExit != 0)
        {
            throw new ComputeEnvironmentException("Installing the pinned compute runtime packages failed.");
        }

        if (!File.Exists(interpreter))
        {
            throw new ComputeEnvironmentException("The provisioned compute runtime did not contain a Python interpreter.");
        }

        // Written only after the interpreter is proven present, so a half-finished sync is never mistaken for a warm
        // cache on the next call.
        await File.WriteAllTextAsync(statePath, lockfileSha, cancellationToken).ConfigureAwait(false);
        TryDeleteDirectory(workDirectory);
        return interpreter;
    }

    private void LogLine(string line)
    {
        _logger.LogDebug("compute runtime provision: {Line}", line);
    }

    private static bool MatchesInstalledLock(string statePath, string lockfileSha)
    {
        try
        {
            return File.Exists(statePath)
                   && string.Equals(File.ReadAllText(statePath).Trim(), lockfileSha, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable marker is treated as absent: re-syncing an already-correct venv is cheap and idempotent,
            // whereas trusting a marker we could not read would serve a closure nothing verified.
            return false;
        }
    }

    private static async Task<string> ComputeFileShaAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void CreateOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
            // Best-effort sweep of the provision scratch directory.
        }
    }

    /// <summary>
    ///     The machine-global compute cache root, under the same base the llama.cpp binaries and the training runtime
    ///     use so one provision serves every node profile on the box and the existing uninstaller sweep already reaches it.
    /// </summary>
    private static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine",
            "compute-runtime");
    }

    /// <summary>
    ///     Resolves the directory holding <c>pyproject.toml</c> / <c>uv.lock</c>. The published app carries them beside
    ///     the executable; a dev or test run reads them straight out of the working tree, which is why the repo path is a
    ///     fallback rather than the only answer — the repo root is outside the publish glob and does not exist in a
    ///     shipped install. Mirrors <c>TrainingRuntimeLayout.ResolveScriptsDirectory</c>.
    /// </summary>
    private static string ResolveScriptsDirectory()
    {
        var published = Path.Combine(AppContext.BaseDirectory, PublishedScriptsDirectoryName);
        if (File.Exists(Path.Combine(published, LockfileName)))
        {
            return published;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RepositoryScriptsRelativePath);
            if (File.Exists(Path.Combine(candidate, LockfileName)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        // Nothing found: return the published path so the missing-lockfile refusal names the location a shipped install
        // would actually use, rather than inventing one.
        return published;
    }
}
