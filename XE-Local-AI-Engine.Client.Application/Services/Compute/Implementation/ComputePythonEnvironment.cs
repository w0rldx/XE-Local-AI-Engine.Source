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

    /// <summary>
    ///     The script scratch directory of the PRE-JAIL layout. It sat beside the venv under the compute cache root,
    ///     which is space the jail-occupancy watchdog never walked and which one call could read out of the next. Both
    ///     holes are closed — the scratch is inside the per-invocation jail now — but a box that ran an older build
    ///     still has the directory, with whatever those calls left in it. It is swept, once, before the tool can run.
    /// </summary>
    private const string LegacyScratchDirectoryName = "scratch";

    /// <summary>The uv-managed CPython root, below the cache root. Named once: the provision writes it and the isolated sandbox binds it.</summary>
    private const string ManagedPythonDirectoryName = "pythons";

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

    private ComputePythonRuntime? _runtime;

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

    public async Task<ComputePythonRuntime> GetRuntimeAsync(CancellationToken cancellationToken = default)
    {
        var cached = Volatile.Read(ref _runtime);
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
            cached = Volatile.Read(ref _runtime);
            if (cached is not null)
            {
                return cached;
            }

            var resolved = await ProvisionAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _runtime, resolved);
            return resolved;
        }
        catch (Exception exception) when (IsProvisioningFailure(exception, cancellationToken))
        {
            // Cold start reaches out to the network (the digest-pinned uv download) and then spawns uv itself, so this
            // boundary can surface HttpRequestException, IOException, TrainingRuntimeException and an HTTP-timeout
            // TaskCanceledException — none of which ComputeToolGateway converts. Left unwrapped they fault the whole
            // tool invocation instead of returning the model-safe rejection every other provisioning failure returns.
            // The inner exception is preserved (and logged here) so the operator-facing detail is not lost with it.
            _logger.LogWarning(exception, "Provisioning the compute Python runtime failed.");
            throw new ComputeEnvironmentException("The pinned compute runtime could not be provisioned on this node.", exception);
        }
        finally
        {
            _provisionGate.Release();
        }
    }

    /// <summary>
    ///     True for a provisioning failure that must be converted into the model-safe
    ///     <see cref="ComputeEnvironmentException" />. A <see cref="ComputeEnvironmentException" /> is already in that
    ///     shape, and a cancellation the CALLER asked for is a real cancellation and must propagate — but a
    ///     cancellation nobody asked for is a download/sync timeout, which is exactly the expected cold-start failure
    ///     this boundary exists to convert.
    /// </summary>
    private static bool IsProvisioningFailure(Exception exception, CancellationToken cancellationToken)
    {
        return exception is not ComputeEnvironmentException
               && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);
    }

    private async Task<ComputePythonRuntime> ProvisionAsync(CancellationToken cancellationToken)
    {
        var project = Path.Combine(_scriptsDirectory, ProjectFileName);
        var lockfile = Path.Combine(_scriptsDirectory, LockfileName);
        if (!File.Exists(project) || !File.Exists(lockfile))
        {
            throw new ComputeEnvironmentException("The pinned compute runtime lockfile is missing from this installation.");
        }

        // Before anything can run: an older build's scratch directory is state a new call must not inherit, and this
        // is the last moment at which nothing has been offered yet. Warm and cold path both reach here, and the
        // cached runtime above means it happens at most once per process.
        SweepLegacyScratch();

        var lockfileSha = await ComputeFileShaAsync(lockfile, cancellationToken).ConfigureAwait(false);
        var venvDirectory = Path.Combine(_cacheRoot, "venv");
        var venvRoot = Path.Combine(venvDirectory, ".venv");
        var interpreter = Path.Combine(venvRoot, "bin", "python");
        var statePath = Path.Combine(_cacheRoot, StateFileName);
        if (File.Exists(interpreter) && MatchesInstalledLock(statePath, lockfileSha))
        {
            // Re-applied on the warm path too: a venv provisioned by an older build (or left writable by an
            // interrupted run) would otherwise stay writable for the life of the process. This runs at most once per
            // process — the runtime is cached above it.
            SetTreeWritable(venvDirectory, writable: false);
            return BuildRuntime(interpreter, venvRoot);
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

        // A re-provision has to write over a tree the previous one locked down.
        SetTreeWritable(venvDirectory, writable: true);

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
                Path.Combine(_cacheRoot, ManagedPythonDirectoryName)),
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
        SetTreeWritable(venvDirectory, writable: false);
        return BuildRuntime(interpreter, venvRoot);
    }

    /// <summary>
    ///     Names the interpreter and the two trees an isolated sandbox must bind for it to start.
    ///     <para>
    ///         Two, and exactly these two. The venv carries <c>pyvenv.cfg</c> and <c>site-packages</c>; its
    ///         <c>bin/python</c> is a symlink into the uv-managed CPython root, and every module the interpreter loads
    ///         before it reads its own configuration lives there — so binding the venv alone produces an interpreter
    ///         that cannot execute. What is deliberately NOT bound is the directory ABOVE them, the compute cache
    ///         root: it also holds the uv download cache, the uv binary, the lockfile state marker and (on an
    ///         un-swept box) the legacy scratch, none of which a script has any business reading, and all of which
    ///         binding the parent would have handed over for free.
    ///     </para>
    ///     <para>
    ///         The managed-CPython ROOT rather than the one installed version under it: uv addresses the install
    ///         through a version-alias symlink beside it (<c>cpython-3.13-…</c> → <c>cpython-3.13.15-…</c>), which the
    ///         venv's own interpreter symlink points at. Bind only the versioned directory and that alias resolves to
    ///         nothing inside the sandbox.
    ///     </para>
    /// </summary>
    private ComputePythonRuntime BuildRuntime(string interpreter, string venvRoot)
    {
        return new ComputePythonRuntime(interpreter, [venvRoot, Path.Combine(_cacheRoot, ManagedPythonDirectoryName)]);
    }

    /// <summary>
    ///     Removes the pre-jail scratch directory if this box still has one. Logged at Information because it is a
    ///     one-off migration an operator may want to see explained, and best-effort because a compute runtime that
    ///     works is worth more than a directory that is no longer written to — a failure leaves stale files nothing
    ///     reads rather than blocking the tool.
    /// </summary>
    private void SweepLegacyScratch()
    {
        var legacy = Path.Combine(_cacheRoot, LegacyScratchDirectoryName);
        if (!Directory.Exists(legacy))
        {
            return;
        }

        try
        {
            Directory.Delete(legacy, recursive: true);
            _logger.LogInformation(
                "Removed the compute runtime's legacy scratch directory: script scratch now lives inside the per-call jail, and the old one is neither metered by the jail disk ceiling nor discarded when a call ends.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception,
                "The compute runtime's legacy scratch directory could not be removed; it is no longer written to, but files an earlier build left there remain on disk.");
        }
    }

    /// <summary>
    ///     Clears (or restores) the write bits across the venv tree. Scripts run with this interpreter and can reach it
    ///     through <c>sys.executable</c>; a writable <c>site-packages</c> lets one call drop a module that every later
    ///     approved call imports, which turns a single approval into persistent code execution.
    ///     <para>
    ///         <b>This is defence in depth, and it is no longer the boundary.</b> The boundary is the read-only bind
    ///         mount: <c>run_python</c> runs under <see cref="XE_Local_AI_Engine.Client.Services.Sandbox.SandboxIsolationMode.Filesystem" />,
    ///         where this tree is bound <c>--ro-bind-fd</c> inside a mount namespace and an <c>os.chmod</c> followed by
    ///         a write answers <c>EROFS</c> no matter who owns the inode. The mode bits still matter for what happens
    ///         OUTSIDE that namespace — the engine's own processes, an operator's shell, and any future caller that
    ///         provisions without isolating — so they stay cleared. What they no longer have to carry is the promise:
    ///         before the mount layer existed a deliberate script could chmod the tree back, and the wiki said so.
    ///     </para>
    /// </summary>
    private static void SetTreeWritable(string root, bool writable)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(root))
        {
            return;
        }

        const UnixFileMode WriteBits = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Append(root))
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                File.SetUnixFileMode(path, writable ? mode | UnixFileMode.UserWrite : mode & ~WriteBits);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A dangling symlink or a file removed under the walk is not worth failing a provision over.
            }
        }
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
