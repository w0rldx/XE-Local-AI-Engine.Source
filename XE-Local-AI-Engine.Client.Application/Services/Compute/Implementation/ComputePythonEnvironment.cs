namespace XE_Local_AI_Engine.Client.Services.Compute.Implementation;

using System.Security.Cryptography;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.ManagedPython;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Python;
using XE_Local_AI_Engine.Providers.Python.Contracts;
using XE_Local_AI_Engine.Providers.Python.Implementation;

/// <summary>
///     Provisions and caches the compute tool's uv-managed Python venv (numpy / scipy / sympy, from the committed
///     <c>tools/compute/pyproject.toml</c> + <c>uv.lock</c>).
/// </summary>
/// <remarks>
///     Provisions through the shared <c>Providers.Python</c> pipeline and toolchain store the training runtime uses; only the
///     lockfile and the venv root differ. Single-flight across instances AND hosts (an OS file lock on the root), cached for
///     the process lifetime, and invalidated by the environment identity plus the store location, not the interpreter's existence.
/// </remarks>
internal sealed class ComputePythonEnvironment : IComputePythonEnvironment, IDisposable
{
    private const string ProjectFileName = "pyproject.toml";
    private const string LockfileName = "uv.lock";
    private const string StateFileName = "installed-compute-runtime.json";
    private const string LegacyStateFileName = "installed-compute-lock.sha256";
    private const string ProvisionLockFileName = ".provision.lock";

    /// <summary>The profile id the status surface reports this environment under.</summary>
    internal const string ProfileId = "compute";

    /// <summary>Bump when the environment's shape changes without its lockfile changing; a mismatch rebuilds the venv.</summary>
    private const int ProfileRevision = 1;

    /// <summary>
    ///     The script scratch directory of the PRE-JAIL layout.
    /// </summary>
    /// <remarks>
    ///     It sat beside the venv under the compute cache root, which is space the jail-occupancy watchdog never walked
    ///     and which one call could read out of the next. Both holes are closed — the scratch is inside the
    ///     per-invocation jail — but a box that ran an older build still has the directory, with whatever those calls
    ///     left in it, so it is swept once before the tool can run.
    /// </remarks>
    private const string LegacyScratchDirectoryName = "scratch";

    /// <summary>The name the shipped compute project files are linked under in the publish output (see the Client csproj).</summary>
    private const string PublishedScriptsDirectoryName = "compute-scripts";

    /// <summary>The repo-relative source of the same files, used by dev and test runs.</summary>
    private const string RepositoryScriptsRelativePath = "tools/compute";

    // Generous next to the closure's real cost (~10s warm cache, ~60s cold on a slow link), because the alternative to
    // waiting is a provision that is killed halfway and re-run from scratch on the next call.
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(10);

    // An operator's remove waits this long for another host's provision before answering busy.
    private static readonly TimeSpan RemoveLockWait = TimeSpan.FromSeconds(10);

    private readonly UvBinaryAcquirer _acquirer;
    private readonly string _cacheRoot;
    private readonly ILogger<ComputePythonEnvironment> _logger;
    private readonly IPythonToolRunner _processRunner;
    private readonly SemaphoreSlim _provisionGate = new(1, 1);
    private readonly string _scriptsDirectory;
    private readonly ManagedPythonToolchain _toolchain;
    private readonly CancellationTokenSource _disposeCts = new();

    // In memory only: a restart forgets a failed provision, and the next status reads the disk again.
    private string? _lastFailure;
    private Task _repairTask = Task.CompletedTask;
    private int _disposed;

    // Executions in flight, and whether a remove owns the venv; both guarded by _leaseLock. In-process only: a run_python on
    // another host sharing this root is not seen, so a remove there can still pull the venv from under it.
    private readonly Lock _leaseLock = new();
    private int _executionLeases;
    private volatile bool _removing;
    private ComputePythonRuntime? _runtime;

    public ComputePythonEnvironment(HttpClient httpClient, ILogger<ComputePythonEnvironment> logger)
        : this(new UvBinaryAcquirer(httpClient),
            PythonToolRunner.ForCurrentPlatform(),
            logger,
            DefaultCacheRoot(),
            ResolveScriptsDirectory(),
            ManagedPythonToolchain.Default())
    {
    }

    /// <summary>Test seam: pins the cache root, the project-files directory, the toolchain store, and the subprocess runner.</summary>
    internal ComputePythonEnvironment(UvBinaryAcquirer acquirer,
        IPythonToolRunner processRunner,
        ILogger<ComputePythonEnvironment> logger,
        string cacheRoot,
        string scriptsDirectory,
        ManagedPythonToolchain toolchain)
    {
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsDirectory);
        _cacheRoot = cacheRoot;
        _scriptsDirectory = scriptsDirectory;
    }

    /// <summary>The background provision the last repair started, for deterministic tests.</summary>
    internal Task PendingRepair => Volatile.Read(ref _repairTask);

    private string VenvDirectory => Path.Combine(_cacheRoot, "venv");

    private string VenvRoot => Path.Combine(VenvDirectory, ".venv");

    private string InterpreterPath => ManagedPythonToolchain.VenvInterpreterPath(VenvRoot);

    private string StatePath => Path.Combine(_cacheRoot, StateFileName);

    public void Dispose()
    {
        // Registered under two service types, so the container disposes it twice.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _provisionGate.Dispose();
    }

    public IDisposable AcquireExecutionLease()
    {
        lock (_leaseLock)
        {
            _executionLeases++;
        }

        return new ExecutionLease(this);
    }

    public async Task<ComputePythonRuntime> GetRuntimeAsync(CancellationToken cancellationToken = default)
    {
        if (TakeCachedRuntime() is { } cached)
        {
            return cached;
        }

        // The uv binary pin and the process runner are both Linux-x64 and the lockfile resolves for that platform alone, so
        // there is nothing to provision elsewhere. Refused with a plain sentence rather than a deeper missing-file error naming a path the model must not see.
        if (!OperatingSystem.IsLinux())
        {
            throw new ComputeEnvironmentException("The Python compute tool is available on Linux only.");
        }

        await _provisionGate.WaitAsync(cancellationToken);
        try
        {
            return await ProvisionUnderGateAsync(cancellationToken);
        }
        finally
        {
            _provisionGate.Release();
        }
    }

    /// <summary>
    ///     Reads where the environment stands from the in-memory provision state and the files on disk. Never provisions,
    ///     never spawns. Platform, kill-switch and containment are the caller's to judge.
    /// </summary>
    internal async Task<ManagedPythonEnvironmentStatus> ReadStatusAsync(CancellationToken cancellationToken)
    {
        if (_provisionGate.CurrentCount == 0)
        {
            return Status(ManagedPythonEnvironmentState.Provisioning,
                _removing ? "The compute runtime is being removed." : "The compute runtime is being provisioned.");
        }

        if (Volatile.Read(ref _lastFailure) is { } failure)
        {
            return Status(ManagedPythonEnvironmentState.Failed, failure);
        }

        var expected = await ExpectedIdentityAsync(cancellationToken);
        if (expected is null)
        {
            return Status(ManagedPythonEnvironmentState.Failed, "The pinned compute runtime lockfile is missing from this installation.");
        }

        var installed = await ReadInstalledStateAsync(cancellationToken);
        if (installed is null)
        {
            if (File.Exists(Path.Combine(_cacheRoot, LegacyStateFileName)))
            {
                return Status(ManagedPythonEnvironmentState.UpdateRequired, "The compute runtime was provisioned by an older build and is rebuilt on next use.");
            }

            var recordUnreadable = File.Exists(StatePath);
            if (!recordUnreadable && !Directory.Exists(VenvRoot))
            {
                return Status(ManagedPythonEnvironmentState.NotProvisioned, reason: null);
            }

            // Another host mid-sync looks exactly like an interrupted one, except that it holds the provision lock.
            if (await IsProvisionLockHeldElsewhereAsync())
            {
                return Status(ManagedPythonEnvironmentState.Provisioning, "Another process is provisioning the compute runtime.");
            }

            return Status(ManagedPythonEnvironmentState.RepairRequired,
                recordUnreadable ? "The compute runtime's state record is unreadable." : "An interrupted provision left an incomplete compute runtime.");
        }

        if (!File.Exists(InterpreterPath))
        {
            return Status(ManagedPythonEnvironmentState.RepairRequired, "The compute runtime's Python interpreter is missing.", installed.Identity);
        }

        var mismatches = MismatchesAgainst(installed, expected);
        return mismatches.Count > 0
            ? Status(ManagedPythonEnvironmentState.UpdateRequired, "The compute runtime is out of date and is rebuilt on next use.", installed.Identity, mismatches)
            : Status(ManagedPythonEnvironmentState.Ready, reason: null, installed.Identity);
    }

    /// <summary>
    ///     Deletes the venv and its state record under the in-process gate and the cross-process provision lock; the
    ///     shared store is never touched. The next <see cref="GetRuntimeAsync" /> provisions from scratch.
    /// </summary>
    internal async Task<ManagedPythonActionResult> RemoveAsync(CancellationToken cancellationToken)
    {
        if (!await _provisionGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return Busy();
        }

        try
        {
            return await RemoveUnderGateAsync(cancellationToken);
        }
        finally
        {
            _provisionGate.Release();
        }
    }

    /// <summary>
    ///     Removes the environment, then provisions it again in the background. The gate stays held across both, so
    ///     status reads <c>Provisioning</c> throughout and a concurrent <c>run_python</c> waits for the fresh runtime.
    /// </summary>
    internal async Task<ManagedPythonActionResult> RepairAsync(CancellationToken cancellationToken)
    {
        if (!await _provisionGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return Busy();
        }

        ManagedPythonActionResult removed;
        try
        {
            removed = await RemoveUnderGateAsync(cancellationToken);
        }
        catch
        {
            _provisionGate.Release();
            throw;
        }

        if (removed.Outcome != ManagedPythonActionOutcome.Completed)
        {
            _provisionGate.Release();
            return removed;
        }

        var disposing = _disposeCts.Token;
        Volatile.Write(ref _repairTask, Task.Run(async () =>
        {
            try
            {
                _ = await ProvisionUnderGateAsync(disposing);
            }
            catch (Exception exception) when (exception is ComputeEnvironmentException or OperationCanceledException or ObjectDisposedException)
            {
                // A failure is already recorded for status; the other two are host shutdown.
            }
            finally
            {
                ReleaseUnlessDisposed();
            }
        }, CancellationToken.None));

        return new ManagedPythonActionResult
        {
            Outcome = ManagedPythonActionOutcome.Started
        };
    }

    /// <summary>
    ///     A non-blocking probe of <c>.provision.lock</c>: never creates it and releases it at once. Only called with the
    ///     in-process gate free, so a holder is another instance or host.
    /// </summary>
    private async Task<bool> IsProvisionLockHeldElsewhereAsync()
    {
        try
        {
            await using var probe = new FileStream(Path.Combine(_cacheRoot, ProvisionLockFileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ManagedPythonActionResult Busy()
    {
        return new ManagedPythonActionResult
        {
            Outcome = ManagedPythonActionOutcome.Busy,
            Message = "The compute runtime is being provisioned. Try again when it finishes."
        };
    }

    private async Task<ManagedPythonActionResult> RemoveUnderGateAsync(CancellationToken cancellationToken)
    {
        // One critical section with AcquireExecutionLease: a lease taken before it is seen here, and one taken after it
        // finds no cached runtime and queues on the gate this remove (or repair) holds.
        lock (_leaseLock)
        {
            if (_executionLeases > 0)
            {
                return new ManagedPythonActionResult
                {
                    Outcome = ManagedPythonActionOutcome.Busy,
                    Message = "A run_python call is using the compute runtime. Try again when it finishes."
                };
            }

            Volatile.Write(ref _runtime, null);
            _removing = true;
        }

        try
        {
            return await RemoveFilesAsync(cancellationToken);
        }
        finally
        {
            _removing = false;
        }
    }

    private async Task<ManagedPythonActionResult> RemoveFilesAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_cacheRoot))
        {
            Volatile.Write(ref _runtime, null);
            Volatile.Write(ref _lastFailure, null);
            return new ManagedPythonActionResult
            {
                Outcome = ManagedPythonActionOutcome.Completed
            };
        }

        FileStream provisionLock;
        try
        {
            provisionLock = await ManagedPythonToolchain.AcquireExclusiveLockAsync(Path.Combine(_cacheRoot, ProvisionLockFileName),
                RemoveLockWait,
                cancellationToken);
        }
        catch (ManagedPythonException)
        {
            return new ManagedPythonActionResult
            {
                Outcome = ManagedPythonActionOutcome.Busy,
                Message = "Another process is provisioning the compute runtime. Try again when it finishes."
            };
        }

        await using (provisionLock)
        {
            // Cleared first: from here on a caller must provision again rather than be handed a tree being deleted.
            Volatile.Write(ref _runtime, null);
            Volatile.Write(ref _lastFailure, null);
            try
            {
                SetTreeWritable(VenvDirectory, writable: true);
                if (Directory.Exists(VenvDirectory))
                {
                    Directory.Delete(VenvDirectory, recursive: true);
                }

                File.Delete(StatePath);
                File.Delete(Path.Combine(_cacheRoot, LegacyStateFileName));
                TryDeleteDirectory(Path.Combine(_cacheRoot, ".work"));
                // Nothing points into the pre-shared-store toolchain once the venv is gone.
                _ = ManagedPythonToolchain.TryDeleteLegacyToolchain(_cacheRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Removing the compute Python runtime failed.");
                return new ManagedPythonActionResult
                {
                    Outcome = ManagedPythonActionOutcome.Failed,
                    Message = "The compute runtime could not be removed completely. Check the node log."
                };
            }
        }

        _logger.LogInformation("Removed the compute Python runtime; the next run_python call provisions it again.");
        return new ManagedPythonActionResult
        {
            Outcome = ManagedPythonActionOutcome.Completed
        };
    }

    private void ReleaseUnlessDisposed()
    {
        try
        {
            _provisionGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // The host is shutting down under a background repair.
        }
    }

    /// <summary>
    ///     The cached runtime while its interpreter is still on disk; otherwise the cache is dropped, so a venv another host
    ///     removed or an operator deleted is provisioned again rather than handed out.
    /// </summary>
    private ComputePythonRuntime? TakeCachedRuntime()
    {
        var cached = Volatile.Read(ref _runtime);
        if (cached is null || File.Exists(cached.InterpreterPath))
        {
            return cached;
        }

        _ = Interlocked.CompareExchange(ref _runtime, null, cached);
        return null;
    }

    /// <summary>The provision body; the caller holds <see cref="_provisionGate" />. Records the outcome for the status read.</summary>
    private async Task<ComputePythonRuntime> ProvisionUnderGateAsync(CancellationToken cancellationToken)
    {
        if (TakeCachedRuntime() is { } cached)
        {
            return cached;
        }

        try
        {
            var resolved = await ProvisionAsync(cancellationToken);
            Volatile.Write(ref _lastFailure, null);
            Volatile.Write(ref _runtime, resolved);
            return resolved;
        }
        catch (ComputeEnvironmentException exception)
        {
            Volatile.Write(ref _lastFailure, exception.Message);
            throw;
        }
        catch (Exception exception) when (IsProvisioningFailure(exception, cancellationToken))
        {
            // Cold start downloads the digest-pinned uv and spawns it, so this boundary can surface HttpRequestException, IOException, ManagedPythonException
            // and an HTTP-timeout TaskCanceledException, none of which ComputeToolGateway converts: unwrapped they fault the whole invocation instead of returning the model-safe rejection.
            _logger.LogWarning(exception, "Provisioning the compute Python runtime failed.");
            const string Message = "The pinned compute runtime could not be provisioned on this node.";
            Volatile.Write(ref _lastFailure, Message);
            throw new ComputeEnvironmentException(Message, exception);
        }
    }

    /// <summary>
    ///     True for a provisioning failure that must be converted into the model-safe
    ///     <see cref="ComputeEnvironmentException" />.
    /// </summary>
    /// <remarks>
    ///     A <see cref="ComputeEnvironmentException" /> is already in that shape, and a cancellation the CALLER asked
    ///     for is a real cancellation and must propagate — but a cancellation nobody asked for is a download/sync
    ///     timeout, which is exactly the expected cold-start failure this boundary exists to convert.
    /// </remarks>
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

        CreateOwnerOnlyDirectory(_cacheRoot);
        await using var provisionLock = await AcquireProvisionLockAsync(Path.Combine(_cacheRoot, ProvisionLockFileName), cancellationToken);

        // Before anything can run: an older build's scratch directory is state a new call must not inherit, and this is the
        // last moment at which nothing has been offered yet. Warm and cold path both reach here, at most once per process.
        SweepLegacyScratch();

        var expected = BuildIdentity(await ComputeFileShaAsync(lockfile, cancellationToken));
        var venvDirectory = VenvDirectory;
        var venvRoot = VenvRoot;
        var interpreter = InterpreterPath;
        var installed = await ReadInstalledStateAsync(cancellationToken);
        if (File.Exists(interpreter) && installed is not null && MismatchesAgainst(installed, expected).Count == 0)
        {
            // Re-applied on the warm path too: a venv provisioned by an older build, or left writable by an interrupted run,
            // would otherwise stay writable for the life of the process. At most once per process — the runtime is cached above it.
            SetTreeWritable(venvDirectory, writable: false);
            return Adopt(interpreter, venvRoot);
        }

        _logger.LogInformation("Provisioning the compute Python runtime from the pinned lockfile.");

        var workDirectory = Path.Combine(_cacheRoot, ".work");
        var isolatedHome = Path.Combine(workDirectory, ".home");
        var isolatedTmp = Path.Combine(workDirectory, ".tmp");
        CreateOwnerOnlyDirectory(workDirectory);
        CreateOwnerOnlyDirectory(isolatedHome);
        CreateOwnerOnlyDirectory(isolatedTmp);
        CreateOwnerOnlyDirectory(venvDirectory);

        // Before the delete, so an offline node that cannot fetch uv keeps its old venv.
        var uv = await _acquirer.EnsureUvAsync(_toolchain.Root, LogLine, cancellationToken);

        // A re-provision has to write over a tree the previous one locked down. The old .venv goes entirely: uv sync keeps
        // an existing venv's pyvenv.cfg home and absolute bin/python symlink, which may name a CPython root the jail no longer binds.
        SetTreeWritable(venvDirectory, writable: true);
        if (Directory.Exists(venvRoot))
        {
            // ponytail: delete-then-sync; a sync that fails offline leaves no runtime until online. Staged swap (Training's) if that bites.
            Directory.Delete(venvRoot, recursive: true);
        }

        // uv resolves the environment beside the pyproject it is pointed at, so the committed pair is copied into the
        // venv directory rather than the shipped (read-only) scripts directory being used as a working tree.
        File.Copy(project, Path.Combine(venvDirectory, ProjectFileName), overwrite: true);
        File.Copy(lockfile, Path.Combine(venvDirectory, LockfileName), overwrite: true);

        var environment = ManagedPythonEnvironment.BuildUvEnvironment(isolatedHome,
            isolatedTmp,
            _toolchain.CacheDirectory,
            _toolchain.PythonInstallDirectory);
        // Copies, not hardlinks: SetTreeWritable strips write bits, and a hardlinked file shares its inode (and so its mode)
        // with the shared uv cache and every other venv linked from it, Training's included.
        environment["UV_LINK_MODE"] = "copy";

        // --locked makes uv fail rather than re-resolve when the lockfile and pyproject.toml disagree, which is what
        // makes this reproducible instead of merely repeatable.
        var syncExit = await _processRunner.RunAsync(uv,
            ["sync", "--locked", "--project", venvDirectory],
            environment,
            venvDirectory,
            LogLine,
            SyncTimeout,
            cancellationToken);
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
        await WriteInstalledStateAsync(new InstalledState
        {
            Identity = expected,
            PythonInstallDirectory = _toolchain.PythonInstallDirectory
        }, cancellationToken);
        File.Delete(Path.Combine(_cacheRoot, LegacyStateFileName));
        TryDeleteDirectory(workDirectory);
        SetTreeWritable(venvDirectory, writable: false);
        return Adopt(interpreter, venvRoot);
    }

    /// <summary>
    ///     Serializes provisioning of this root across instances and hosts: <c>FileShare.None</c> is an exclusive
    ///     <c>flock(2)</c> on Unix, released when the handle or the process dies.
    /// </summary>
    /// <remarks>
    ///     Without it, one provision's final <c>SetTreeWritable(false)</c> and work-tree delete ran under another's live
    ///     <c>uv sync</c>. A holder may keep it for a whole sync, so the wait is bounded by twice the sync timeout.
    ///     The lock file must never be deleted while hosts run (a new inode gives two holders); only the uninstaller removes it.
    /// </remarks>
    private static Task<FileStream> AcquireProvisionLockAsync(string path, CancellationToken cancellationToken)
    {
        return ManagedPythonToolchain.AcquireExclusiveLockAsync(path, SyncTimeout + SyncTimeout, cancellationToken);
    }

    /// <summary>The runtime for a proven venv, after dropping the pre-shared-store toolchain it no longer points into.</summary>
    private ComputePythonRuntime Adopt(string interpreter, string venvRoot)
    {
        if (!ManagedPythonToolchain.TryDeleteLegacyToolchain(_cacheRoot))
        {
            _logger.LogWarning("The compute runtime's legacy uv, pythons or uv-cache directory could not be removed; nothing uses it any more.");
        }

        return BuildRuntime(interpreter, venvRoot);
    }

    /// <summary>
    ///     Names the interpreter and the two trees an isolated sandbox must bind for it to start.
    /// </summary>
    /// <remarks>
    ///     Two, and exactly these two. The venv's <c>bin/python</c> is a symlink into the shared CPython root, where every module the
    ///     interpreter loads before reading its own configuration lives, so the venv alone cannot execute. The ROOT, not one install:
    ///     uv addresses it through a version-alias symlink (<c>cpython-3.13-…</c> → <c>cpython-3.13.15-…</c>) beside it.
    ///     Why never the store or cache root above them: docs/wiki/19-compute-tools.md, "2.4 The filesystem boundary".
    /// </remarks>
    private ComputePythonRuntime BuildRuntime(string interpreter, string venvRoot)
    {
        return new ComputePythonRuntime
        {
            InterpreterPath = interpreter,
            ReadOnlyTrees = [venvRoot, _toolchain.PythonInstallDirectory]
        };
    }

    /// <summary>
    ///     Removes the pre-jail scratch directory if this box still has one.
    /// </summary>
    /// <remarks>
    ///     Logged at Information because it is a one-off migration an operator may want to see explained, and
    ///     best-effort because a compute runtime that works is worth more than a directory nothing writes to any more:
    ///     a failure leaves stale files nothing reads rather than blocking the tool.
    /// </remarks>
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
    ///     Clears (or restores) the write bits across the venv tree.
    /// </summary>
    /// <remarks>
    ///     Scripts reach the interpreter through <c>sys.executable</c>, and a writable <c>site-packages</c> lets one call drop a module
    ///     every later approved call imports — a single approval turned into persistent code execution. <b>This is defence in depth and no
    ///     longer the boundary:</b> the boundary is the read-only bind mount under
    ///     <see cref="XE_Local_AI_Engine.Client.Services.Sandbox.SandboxIsolationMode.Filesystem" />, where an <c>os.chmod</c> and a write
    ///     both answer <c>EROFS</c>. The mode bits still cover OUTSIDE that namespace: the engine's own processes, an operator's shell.
    /// </remarks>
    private static void SetTreeWritable(string root, bool writable)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(root))
        {
            return;
        }

        const UnixFileMode WriteBits = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        var rootInfo = new DirectoryInfo(root);
        foreach (var entry in rootInfo.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Append(rootInfo))
        {
            try
            {
                // chmod follows a symlink, and bin/python links into the shared CPython store, which is not this tree's to lock.
                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                var mode = entry.UnixFileMode;
                entry.UnixFileMode = writable ? mode | UnixFileMode.UserWrite : mode & ~WriteBits;
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

    private static ManagedPythonEnvironmentIdentity BuildIdentity(string lockfileSha256)
    {
        return new ManagedPythonEnvironmentIdentity
        {
            ProfileId = ProfileId,
            PythonMinor = ManagedPythonPins.PythonMinor,
            LockfileSha256 = lockfileSha256,
            ProfileRevision = ProfileRevision,
            Rid = ManagedPythonPins.Current.Rid,
            UvVersion = ManagedPythonPins.UvVersion
        };
    }

    /// <summary>The identity mismatches, plus <c>toolchainStore</c> when the venv was built against another CPython root, which it keeps pointing into.</summary>
    private List<string> MismatchesAgainst(InstalledState installed, ManagedPythonEnvironmentIdentity expected)
    {
        var mismatches = installed.Identity.MismatchesAgainst(expected).ToList();
        if (!string.Equals(installed.PythonInstallDirectory, _toolchain.PythonInstallDirectory, StringComparison.Ordinal))
        {
            mismatches.Add("toolchainStore");
        }

        return mismatches;
    }

    private async Task<ManagedPythonEnvironmentIdentity?> ExpectedIdentityAsync(CancellationToken cancellationToken)
    {
        var lockfile = Path.Combine(_scriptsDirectory, LockfileName);
        try
        {
            return BuildIdentity(await ComputeFileShaAsync(lockfile, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<InstalledState?> ReadInstalledStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return null;
            }

            await using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<InstalledState>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable record is treated as absent: re-syncing an already-correct venv is cheap and idempotent,
            // whereas trusting a record we could not read would serve a closure nothing verified.
            return null;
        }
    }

    private async Task WriteInstalledStateAsync(InstalledState state, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(StatePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, state, cancellationToken: cancellationToken);
    }

    private static ManagedPythonEnvironmentStatus Status(ManagedPythonEnvironmentState state,
        string? reason,
        ManagedPythonEnvironmentIdentity? installed = null,
        IReadOnlyList<string>? mismatches = null)
    {
        return new ManagedPythonEnvironmentStatus
        {
            ProfileId = ProfileId,
            State = state,
            Reason = reason,
            Installed = installed,
            Mismatches = mismatches ?? []
        };
    }

    private static async Task<string> ComputeFileShaAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void CreateOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Inherits the per-user %LOCALAPPDATA% ACL; no ACL code of its own (ADR 0016).
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
        return Path.Combine(RuntimeCacheDirectory.Resolve(),
            "compute-runtime");
    }

    /// <summary>
    ///     Resolves the directory holding <c>pyproject.toml</c> / <c>uv.lock</c>.
    /// </summary>
    /// <remarks>
    ///     The published app carries them beside the executable and a dev or test run reads them out of the working
    ///     tree, which is why the repo path is a fallback rather than the only answer: the repo root is outside the
    ///     publish glob and does not exist in a shipped install. Mirrors
    ///     <c>TrainingRuntimeLayout.ResolveScriptsDirectory</c>.
    /// </remarks>
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

    /// <summary>One in-flight execution; disposing it more than once releases it once.</summary>
    private sealed class ExecutionLease : IDisposable
    {
        private ComputePythonEnvironment? _owner;

        public ExecutionLease(ComputePythonEnvironment owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } owner)
            {
                return;
            }

            lock (owner._leaseLock)
            {
                owner._executionLeases--;
            }
        }
    }

    /// <summary>The persisted record: the identity plus the store the venv links into.</summary>
    private sealed class InstalledState
    {
        public required ManagedPythonEnvironmentIdentity Identity { get; init; }

        public required string PythonInstallDirectory { get; init; }
    }
}
