namespace XE_Local_AI_Engine.Client.Services.Compute.Implementation;

using XE_Local_AI_Engine.Client.Services.ManagedPython;
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
    /// <summary>The profile id the status surface reports this environment under.</summary>
    internal const string ProfileId = "compute";

    // Generous next to the closure's real cost (~10s warm cache, ~60s cold on a slow link), because the alternative to
    // waiting is a provision that is killed halfway and re-run from scratch on the next call.
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    // An operator's remove waits this long for another host's provision before answering busy.
    private static readonly TimeSpan RemoveLockWait = TimeSpan.FromSeconds(10);

    private readonly UvBinaryAcquirer _acquirer;
    private readonly ComputeRuntimeDirectory _directory;
    private readonly ILogger<ComputePythonEnvironment> _logger;
    private readonly IPythonToolRunner _processRunner;
    private readonly SemaphoreSlim _provisionGate = new(1, 1);
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
            ComputeRuntimeDirectory.DefaultCacheRoot(),
            ComputeRuntimeDirectory.ResolveScriptsDirectory(),
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
        _directory = new ComputeRuntimeDirectory(cacheRoot, scriptsDirectory, _toolchain);
    }

    /// <summary>The background provision the last repair started, for deterministic tests.</summary>
    internal Task PendingRepair => Volatile.Read(ref _repairTask);

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
            return await ProvisionUnderGateAsync(rebuild: false, cancellationToken);
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

        // A failed rebuild that left the previous venv in service reads Ready with the failure as its reason, as Training's does.
        if (Volatile.Read(ref _lastFailure) is { } failure)
        {
            return Volatile.Read(ref _runtime) is null
                ? Status(ManagedPythonEnvironmentState.Failed, failure)
                : await ServedStatusAsync(failure, cancellationToken);
        }

        var expected = await _directory.TryComputeExpectedIdentityAsync(cancellationToken);
        if (expected is null)
        {
            return Status(ManagedPythonEnvironmentState.Failed, "The pinned compute runtime lockfile is missing from this installation.");
        }

        var installed = await _directory.ReadInstalledStateAsync(cancellationToken);
        if (installed is null)
        {
            if (File.Exists(_directory.LegacyStatePath))
            {
                return Status(ManagedPythonEnvironmentState.UpdateRequired, "The compute runtime was provisioned by an older build and is rebuilt on next use.");
            }

            // Another host mid-sync looks exactly like an interrupted or absent one, except that it holds the provision lock.
            if (await _directory.IsProvisionLockHeldElsewhereAsync())
            {
                return Status(ManagedPythonEnvironmentState.Provisioning, "Another process is provisioning the compute runtime.");
            }

            var recordUnreadable = File.Exists(_directory.StatePath);
            if (!recordUnreadable && !Directory.Exists(_directory.VenvRoot))
            {
                return Status(ManagedPythonEnvironmentState.NotProvisioned, reason: null);
            }

            return Status(ManagedPythonEnvironmentState.RepairRequired,
                recordUnreadable ? "The compute runtime's state record is unreadable." : "An interrupted provision left an incomplete compute runtime.");
        }

        if (!File.Exists(_directory.InterpreterPath))
        {
            return Status(ManagedPythonEnvironmentState.RepairRequired, "The compute runtime's Python interpreter is missing.", installed.Identity);
        }

        var mismatches = _directory.MismatchesAgainst(installed, expected);
        return mismatches.Count > 0
            ? Status(ManagedPythonEnvironmentState.UpdateRequired, "The compute runtime is out of date and is rebuilt on next use.", installed.Identity, mismatches)
            : Status(ManagedPythonEnvironmentState.Ready, reason: null, installed.Identity);
    }

    /// <summary>What a failed rebuild left in service; a missing lockfile or record still reads Ready, since the venv is what runs.</summary>
    private async Task<ManagedPythonEnvironmentStatus> ServedStatusAsync(string failure, CancellationToken cancellationToken)
    {
        var installed = await _directory.ReadInstalledStateAsync(cancellationToken);
        var expected = await _directory.TryComputeExpectedIdentityAsync(cancellationToken);
        return Status(ManagedPythonEnvironmentState.Ready,
            $"{failure} The previous compute runtime stays in service.",
            installed?.Identity,
            installed is not null && expected is not null ? _directory.MismatchesAgainst(installed, expected) : null);
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
    ///     Rebuilds the environment in the background through the staged swap, so a failed repair leaves the previous venv in
    ///     service. The gate stays held throughout: status reads <c>Provisioning</c> and a concurrent <c>run_python</c> waits.
    /// </summary>
    internal async Task<ManagedPythonActionResult> RepairAsync(CancellationToken cancellationToken)
    {
        if (!await _provisionGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return Busy();
        }

        if (!TryDropRuntimeUnlessLeased())
        {
            _provisionGate.Release();
            return Leased();
        }

        var disposing = _disposeCts.Token;
        Volatile.Write(ref _repairTask, Task.Run(async () =>
        {
            try
            {
                _ = await ProvisionUnderGateAsync(rebuild: true, disposing);
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

    private static ManagedPythonActionResult Busy()
    {
        return new ManagedPythonActionResult
        {
            Outcome = ManagedPythonActionOutcome.Busy,
            Message = "The compute runtime is being provisioned. Try again when it finishes."
        };
    }

    private static ManagedPythonActionResult Leased()
    {
        return new ManagedPythonActionResult
        {
            Outcome = ManagedPythonActionOutcome.Busy,
            Message = "A run_python call is using the compute runtime. Try again when it finishes."
        };
    }

    /// <summary>
    ///     One critical section with <see cref="AcquireExecutionLease" />: a lease taken before it is seen here, and one taken
    ///     after it finds no cached runtime and queues on the gate the caller (a remove or repair) holds.
    /// </summary>
    private bool TryDropRuntimeUnlessLeased()
    {
        lock (_leaseLock)
        {
            if (_executionLeases > 0)
            {
                return false;
            }

            Volatile.Write(ref _runtime, null);
            return true;
        }
    }

    private async Task<ManagedPythonActionResult> RemoveUnderGateAsync(CancellationToken cancellationToken)
    {
        if (!TryDropRuntimeUnlessLeased())
        {
            return Leased();
        }

        _removing = true;
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
        if (!Directory.Exists(_directory.CacheRoot))
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
            provisionLock = await ManagedPythonToolchain.AcquireExclusiveLockAsync(_directory.ProvisionLockPath,
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
                _directory.DeleteVenvTrees();
                File.Delete(_directory.StatePath);
                File.Delete(_directory.LegacyStatePath);
                ComputeRuntimeDirectory.TryDeleteDirectory(_directory.WorkDirectory);
                // Nothing points into the pre-shared-store toolchain once the venv is gone.
                _ = ManagedPythonToolchain.TryDeleteLegacyToolchain(_directory.CacheRoot);
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
    private async Task<ComputePythonRuntime> ProvisionUnderGateAsync(bool rebuild, CancellationToken cancellationToken)
    {
        if (!rebuild && TakeCachedRuntime() is { } cached)
        {
            return cached;
        }

        try
        {
            var resolved = await ProvisionAsync(rebuild, cancellationToken);
            Volatile.Write(ref _lastFailure, null);
            Volatile.Write(ref _runtime, resolved);
            return resolved;
        }
        catch (ComputeEnvironmentException exception)
        {
            if (await ServePreviousAsync(exception.Message) is { } previous)
            {
                return previous;
            }

            throw;
        }
        catch (Exception exception) when (IsProvisioningFailure(exception, cancellationToken))
        {
            // Cold start downloads the digest-pinned uv and spawns it, so this boundary can surface HttpRequestException, IOException, ManagedPythonException
            // and an HTTP-timeout TaskCanceledException, none of which ComputeToolGateway converts: unwrapped they fault the whole invocation instead of returning the model-safe rejection.
            _logger.LogWarning(exception, "Provisioning the compute Python runtime failed.");
            const string Message = "The pinned compute runtime could not be provisioned on this node.";
            if (await ServePreviousAsync(Message) is { } previous)
            {
                return previous;
            }

            throw new ComputeEnvironmentException(Message, exception);
        }
    }

    /// <summary>
    ///     Records a failed provision and, when the venv the staged swap left in place still has its record and its CPython,
    ///     caches and returns it: an offline node keeps a working, if outdated, runtime until a restart or repair retries.
    /// </summary>
    private async Task<ComputePythonRuntime?> ServePreviousAsync(string failure)
    {
        Volatile.Write(ref _lastFailure, failure);
        var installed = await _directory.ReadInstalledStateAsync(CancellationToken.None);
        if (installed is null || !RunsOnStore(_directory.InterpreterPath, installed.PythonInstallDirectory))
        {
            return null;
        }

        ComputeRuntimeDirectory.SetTreeWritable(_directory.VenvDirectory, writable: false);
        _logger.LogWarning("Rebuilding the compute Python runtime failed ({Failure}); the previous runtime stays in service.", failure);
        var previous = new ComputePythonRuntime
        {
            InterpreterPath = _directory.InterpreterPath,
            ReadOnlyTrees = [_directory.VenvRoot, installed.PythonInstallDirectory]
        };
        Volatile.Write(ref _runtime, previous);
        return previous;
    }

    /// <summary>
    ///     True when <paramref name="interpreter" /> finally resolves to an existing file under <paramref name="pythonInstallDirectory" />,
    ///     the tree the jail binds. <c>File.Exists</c> alone answers true for a dangling link.
    /// </summary>
    private static bool RunsOnStore(string interpreter, string pythonInstallDirectory)
    {
        try
        {
            var link = new FileInfo(interpreter);
            var target = link.ResolveLinkTarget(returnFinalTarget: true) ?? link;
            return target.Exists
                   && target.FullName.StartsWith(Path.TrimEndingDirectorySeparator(pythonInstallDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
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

    private async Task<ComputePythonRuntime> ProvisionAsync(bool rebuild, CancellationToken cancellationToken)
    {
        if (!File.Exists(_directory.ProjectPath) || !File.Exists(_directory.LockfilePath))
        {
            throw new ComputeEnvironmentException("The pinned compute runtime lockfile is missing from this installation.");
        }

        ComputeRuntimeDirectory.CreateOwnerOnlyDirectory(_directory.CacheRoot);
        await using var provisionLock = await AcquireProvisionLockAsync(_directory.ProvisionLockPath, cancellationToken);

        // Before anything can run: an older build's scratch directory is state a new call must not inherit, and this is the
        // last moment at which nothing has been offered yet. Warm and cold path both reach here, at most once per process.
        SweepLegacyScratch();
        _directory.Recover();

        var expected = await _directory.ComputeExpectedIdentityAsync(cancellationToken);
        var venvDirectory = _directory.VenvDirectory;
        var interpreter = _directory.InterpreterPath;
        var installed = await _directory.ReadInstalledStateAsync(cancellationToken);
        if (!rebuild && File.Exists(interpreter) && installed is not null && _directory.MismatchesAgainst(installed, expected).Count == 0)
        {
            // Re-applied on the warm path too: a venv provisioned by an older build, or left writable by an interrupted run,
            // would otherwise stay writable for the life of the process. At most once per process — the runtime is cached above it.
            ComputeRuntimeDirectory.SetTreeWritable(venvDirectory, writable: false);
            return Adopt(interpreter, _directory.VenvRoot);
        }

        _logger.LogInformation("Provisioning the compute Python runtime from the pinned lockfile.");
        var staging = _directory.StagingDirectory;
        try
        {
            await SyncStagingAsync(staging, cancellationToken);
        }
        catch
        {
            ComputeRuntimeDirectory.TryDeleteTree(staging);
            throw;
        }

        // Only the proven tree is swapped in, and the record lands with it, so a half-finished sync is never a warm cache.
        await _directory.SwapAsync(new ComputeRuntimeDirectory.InstalledState
        {
            Identity = expected,
            PythonInstallDirectory = _toolchain.PythonInstallDirectory
        });
        File.Delete(_directory.LegacyStatePath);
        ComputeRuntimeDirectory.TryDeleteDirectory(_directory.WorkDirectory);
        ComputeRuntimeDirectory.SetTreeWritable(venvDirectory, writable: false);
        return Adopt(interpreter, _directory.VenvRoot);
    }

    /// <summary>
    ///     Syncs the pinned closure into a fresh staging project and imports it once. The live venv is not touched, so any
    ///     failure here, offline ones included, leaves it in service.
    /// </summary>
    private async Task SyncStagingAsync(string staging, CancellationToken cancellationToken)
    {
        var workDirectory = _directory.WorkDirectory;
        var isolatedHome = Path.Combine(workDirectory, ".home");
        var isolatedTmp = Path.Combine(workDirectory, ".tmp");
        ComputeRuntimeDirectory.CreateOwnerOnlyDirectory(workDirectory);
        ComputeRuntimeDirectory.CreateOwnerOnlyDirectory(isolatedHome);
        ComputeRuntimeDirectory.CreateOwnerOnlyDirectory(isolatedTmp);

        var uv = await _acquirer.EnsureUvAsync(_toolchain.Root, LogLine, cancellationToken);

        // uv resolves beside the pyproject it is pointed at, so the committed pair is copied in. A fresh project, because uv
        // sync keeps an existing venv's pyvenv.cfg home, which may name a CPython root the jail no longer binds.
        if (Directory.Exists(staging))
        {
            // Recover's best-effort delete failed; reusing the tree would keep its stale .venv home.
            throw new ComputeEnvironmentException("A leftover compute runtime staging tree could not be removed.");
        }

        ComputeRuntimeDirectory.CreateOwnerOnlyDirectory(staging);
        File.Copy(_directory.ProjectPath, Path.Combine(staging, ComputeRuntimeDirectory.ProjectFileName), overwrite: true);
        File.Copy(_directory.LockfilePath, Path.Combine(staging, ComputeRuntimeDirectory.LockfileName), overwrite: true);

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
            ["sync", "--locked", "--project", staging],
            environment,
            staging,
            LogLine,
            SyncTimeout,
            cancellationToken);
        if (syncExit != 0)
        {
            throw new ComputeEnvironmentException("Installing the pinned compute runtime packages failed.");
        }

        var stagedInterpreter = ManagedPythonToolchain.VenvInterpreterPath(Path.Combine(staging, ".venv"));
        if (!File.Exists(stagedInterpreter))
        {
            throw new ComputeEnvironmentException("The provisioned compute runtime did not contain a Python interpreter.");
        }

        // A sync that exits 0 over a closure that cannot import must not replace a working venv.
        var probeExit = await _processRunner.RunAsync(stagedInterpreter,
            ["-I", "-c", "import numpy, scipy, sympy"],
            environment,
            staging,
            LogLine,
            ProbeTimeout,
            cancellationToken);
        if (probeExit != 0)
        {
            throw new ComputeEnvironmentException("The provisioned compute runtime failed its import check.");
        }
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
        if (!ManagedPythonToolchain.TryDeleteLegacyToolchain(_directory.CacheRoot))
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
    ///     Removes the pre-jail scratch directory if this machine still has one.
    /// </summary>
    /// <remarks>
    ///     Logged at Information because it is a one-off migration an operator may want to see explained, and
    ///     best-effort because a compute runtime that works is worth more than a directory nothing writes to any more:
    ///     a failure leaves stale files nothing reads rather than blocking the tool.
    /// </remarks>
    private void SweepLegacyScratch()
    {
        var legacy = _directory.LegacyScratchDirectory;
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

    private void LogLine(string line)
    {
        _logger.LogDebug("compute runtime provision: {Line}", line);
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
}
