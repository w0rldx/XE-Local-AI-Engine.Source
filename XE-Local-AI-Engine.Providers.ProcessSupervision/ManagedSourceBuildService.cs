namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision.Contracts;

/// <summary>
///     Linux-only, detached, single-flight source build of a runtime's managed server: clone, verify, configure, build,
///     smoke-test and adopt.
/// </summary>
/// <remarks>
///     The build runs on its own task and reports through a status snapshot, because it takes minutes and the operator's request must
///     not hold a connection open for it. It holds the activity gate's mutation reservation for its whole duration, so an eject or a job
///     cannot race the tree it is about to replace, and a start attempted while anything holds the runtime is refused as runtime-busy
///     instead. Every command goes through <see cref="ISourceCommandRunner" />, so tests drive it without compiling.
/// </remarks>
public abstract class ManagedSourceBuildService<TBackend, TDescriptor> : IDisposable
    where TBackend : struct, Enum
    where TDescriptor : class
{
    private const int MaxLogLines = 500;
    private const int PublishQueueCapacity = 128;

    private readonly IRuntimeActivityGate _activityGate;
    private readonly string[] _buildTargets;
    private readonly string _cacheRoot;
    private readonly bool _isLinux;
    private readonly ILogger _logger;
    private readonly IManagedSourceBuildSignal<TBackend> _managedSignal;
    private readonly string _managedRuntimeLabel;
    private readonly Channel<StatusChange> _publishQueue;
    private readonly Task _publisherTask;
    private readonly IManagedRuntimeStore<TBackend> _runtimeStore;
    private readonly string _runtimeName;
    private readonly string _serverName;
    private readonly string _serviceLabel;
    private readonly SemaphoreSlim _startGate = new(initialCount: 1, maxCount: 1);
    private readonly Lock _stateLock = new();
    private Task? _activeBuildTask;
    private CancellationTokenSource? _buildCts;
    private DateTimeOffset? _completedAtUtc;
    private TDescriptor? _currentBuild;
    private bool _isRunning;
    private bool _isStopping;
    private readonly List<string> _logLines = [];
    private long _logStartSequence;
    private long _nextLogSequence;
    private BuildPhase _phase;
    private string? _sanitizedError;
    private DateTimeOffset? _startedAtUtc;
    private readonly TimeProvider _timeProvider;

    /// <param name="runtimeName">The runtime's cache directory and log-line name, such as <c>whisper.cpp</c>.</param>
    /// <param name="serviceLabel">The runtime as the stopping refusal names it, such as <c>transcription runtime</c>.</param>
    /// <param name="managedRuntimeLabel">The runtime as the remove failures name it, such as <c>whisper.cpp runtime</c>.</param>
    /// <param name="serverName">The server executable's file name, such as <c>whisper-server</c>.</param>
    /// <param name="buildTargets">The cmake targets the build compiles.</param>
    protected ManagedSourceBuildService(string runtimeName,
        string serviceLabel,
        string managedRuntimeLabel,
        string serverName,
        string[] buildTargets,
        IManagedRuntimeStore<TBackend> runtimeStore,
        IManagedSourceBuildSignal<TBackend> managedSignal,
        IRuntimeActivityGate activityGate,
        ILogger logger,
        string cacheRoot,
        ISourceCommandRunner runner,
        TimeProvider timeProvider,
        bool? isLinux)
    {
        _runtimeName = runtimeName;
        _serviceLabel = serviceLabel;
        _managedRuntimeLabel = managedRuntimeLabel;
        _serverName = serverName;
        _buildTargets = buildTargets;
        _runtimeStore = runtimeStore ?? throw new ArgumentNullException(nameof(runtimeStore));
        _managedSignal = managedSignal ?? throw new ArgumentNullException(nameof(managedSignal));
        _activityGate = activityGate ?? throw new ArgumentNullException(nameof(activityGate));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _publishQueue = Channel.CreateBounded<StatusChange>(new BoundedChannelOptions(PublishQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,

            // A slow subscriber must never stall the build, and the newest state is the one worth keeping.
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _publisherTask = PublishLoopAsync();
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = cacheRoot;
        Runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _isLinux = isLinux ?? OperatingSystem.IsLinux();
    }

    /// <summary>Where a build has got to. Each runtime's public phase enum carries the same values and maps by cast.</summary>
    protected enum BuildPhase
    {
        Idle = 0,
        Cloning = 1,
        Verifying = 2,
        Configuring = 3,
        Building = 4,
        SmokeTesting = 5,
        Adopting = 6,
        Removing = 7,
        Completed = 8,
        Cancelled = 9,
        Failed = 10
    }

    /// <summary>Why a start did or did not begin a build. Each runtime's public outcome enum carries the same values.</summary>
    protected enum StartOutcome
    {
        Started = 0,
        AlreadyRunning = 1,
        InsufficientDisk = 2,
        MissingPrerequisites = 3,
        RuntimeBusy = 4
    }

    /// <summary>Why a remove did or did not delete the runtime. Each runtime's public outcome enum carries the same values.</summary>
    protected enum RemoveOutcome
    {
        Removed = 0,
        NotInstalled = 1,
        RuntimeBusy = 2
    }

    protected ISourceCommandRunner Runner { get; }

    protected string WorkRoot => Path.Combine(BuildRoot, ".work");

    /// <summary>The runtime's adoption, whose journal is reconciled before every start and at host start.</summary>
    protected abstract ManagedRuntimeAdoption<TBackend> Adoption { get; }

    private string BuildRoot => Path.Combine(_cacheRoot, _runtimeName, "source-build");
    private string MarkerPath => Path.Combine(WorkRoot, ".build-in-progress");
    private string RuntimeRoot => Path.Combine(_cacheRoot, _runtimeName, "managed");

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Requests cancellation of a running build. False when there is nothing to cancel.</summary>
    public bool Cancel()
    {
        lock (_stateLock)
        {
            if (!_isRunning || _buildCts is null)
            {
                return false;
            }

            _buildCts.Cancel();
            return true;
        }
    }

    /// <summary>Reconciles an interrupted adoption and republishes the managed-runtime signal.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Cancels and drains any in-flight build so the host can exit.</summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        Task? task;
        CancellationTokenSource? buildCts;
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                _isStopping = true;
                buildCts = _buildCts;
                task = _activeBuildTask;
            }
        }
        finally
        {
            _startGate.Release();
        }

        if (buildCts is not null)
        {
            try
            {
                await buildCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Build completion won the race; the captured task below is still authoritative.
            }
        }

        if (task is not null)
        {
            await task.WaitAsync(ct).ConfigureAwait(false);
        }

        _publishQueue.Writer.TryComplete();
        await _publisherTask.WaitAsync(ct).ConfigureAwait(false);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        try
        {
            _buildCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Build completion may dispose concurrently with host teardown.
        }

        _buildCts?.Dispose();
        _publishQueue.Writer.TryComplete();
        _startGate.Dispose();
    }

    /// <summary>
    ///     Admits and detaches a build. <paramref name="prepare" /> validates the request, runs <see cref="RecoverCoreAsync" />
    ///     and probes prerequisites, all under the start gate.
    /// </summary>
    protected async Task<TResult> StartCoreAsync<TReport, TResult>(Func<CancellationToken, Task<StartPlan<TReport>>> prepare,
        Func<StartOutcome, TReport?, RuntimeActivitySnapshot?, TResult> result,
        CancellationToken ct)
        where TReport : class
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(result);
        if (!_isLinux)
        {
            throw RuntimeError("In-app source builds are available on Linux only.");
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        TaskCompletionSource? startSignal = null;
        try
        {
            lock (_stateLock)
            {
                if (_isStopping)
                {
                    throw RuntimeError($"The {_serviceLabel} source-build service is stopping.");
                }

                if (_isRunning)
                {
                    return result(StartOutcome.AlreadyRunning, null, null);
                }
            }

            var plan = await prepare(ct).ConfigureAwait(false);
            if (!plan.CanBuild)
            {
                // Disk is separated from the rest so the endpoint can say "not enough space" rather than send the
                // operator hunting a missing compiler.
                return result(plan.InsufficientDisk ? StartOutcome.InsufficientDisk : StartOutcome.MissingPrerequisites, plan.Prerequisites, null);
            }

#pragma warning disable CA2000 // Ownership passes to the build task's finally, or to the catch below if the start fails.
            if (!_activityGate.TryAcquireMutationReservation(out var mutationReservation))
#pragma warning restore CA2000
            {
                return result(StartOutcome.RuntimeBusy, plan.Prerequisites, _activityGate.GetSnapshot());
            }

            try
            {
                var buildCts = new CancellationTokenSource();

                // The detached task waits on this signal so it cannot start — and cannot complete — before the state below describes it; otherwise a fast failure could publish a terminal phase that
                // the state write then overwrites with "running".
                startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var task = Task.Run(async () =>
                {
                    await startSignal.Task.ConfigureAwait(false);
                    BuildCompletion completion;
                    try
                    {
                        completion = await RunBuildAsync(plan, buildCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        mutationReservation.Dispose();
                    }

                    CompleteBuild(completion);
                }, CancellationToken.None);

                lock (_stateLock)
                {
                    _isRunning = true;
                    _phase = BuildPhase.Cloning;
                    _logLines.Clear();
                    _logStartSequence = 0;
                    _nextLogSequence = 0;
                    _sanitizedError = null;
                    _currentBuild = plan.Build;
                    _startedAtUtc = _timeProvider.GetUtcNow();
                    _completedAtUtc = null;
                    _buildCts?.Dispose();
                    _buildCts = buildCts;
                    _activeBuildTask = task;
                }
            }
            catch
            {
                mutationReservation.Dispose();
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }

        startSignal.SetResult();
        return result(StartOutcome.Started, null, null);
    }

    /// <summary>Deletes the adopted managed runtime and its record. The in-app recovery from a fail-closed tombstone.</summary>
    protected async Task<TResult> RemoveCoreAsync<TResult>(Func<RemoveOutcome, RuntimeActivitySnapshot?, TResult> result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_isRunning)
                {
                    return result(RemoveOutcome.RuntimeBusy, _activityGate.GetSnapshot());
                }
            }

            if (!_activityGate.TryAcquireMutationReservation(out var mutation))
            {
                return result(RemoveOutcome.RuntimeBusy, _activityGate.GetSnapshot());
            }

            await using var mutationScope = mutation;

            var installed = await _runtimeStore.ReadAsync(ct).ConfigureAwait(false);
            if (installed is null)
            {
                return result(RemoveOutcome.NotInstalled, null);
            }

            SetPhase(BuildPhase.Removing);
            DeleteManagedRuntime(installed);
            await _runtimeStore.DeleteAsync(ct).ConfigureAwait(false);
            _managedSignal.Clear();
            SetTerminal(BuildPhase.Completed, error: null);
            return result(RemoveOutcome.Removed, null);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>The current state with a copy of the retained log tail; never blocks on the build.</summary>
    protected StatusSnapshot ReadStatus()
    {
        lock (_stateLock)
        {
            return new StatusSnapshot
            {
                Phase = _phase,
                IsRunning = _isRunning,
                Terminal = IsTerminal(_phase),
                LogLines = [.. _logLines],
                LogStartSequence = _logStartSequence,
                SanitizedError = _sanitizedError,
                CurrentBuild = _currentBuild,
                StartedAtUtc = _startedAtUtc,
                CompletedAtUtc = _completedAtUtc
            };
        }
    }

    /// <summary>
    ///     Reconciles the adoption journal and an interrupted work tree, then republishes the managed-runtime signal.
    ///     A no-op while a build runs.
    /// </summary>
    protected async Task RecoverCoreAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_isRunning)
            {
                return;
            }
        }

        await Adoption.RecoverAsync(ct).ConfigureAwait(false);

        if (File.Exists(MarkerPath) || Directory.Exists(WorkRoot))
        {
            BestEffortFile.TryDeleteDirectory(WorkRoot);
            lock (_stateLock)
            {
                _phase = BuildPhase.Failed;
                _sanitizedError = "A previously interrupted source build was recovered and its temporary files were removed.";
                _completedAtUtc = _timeProvider.GetUtcNow();
            }
        }

        // Republishing the signal is the whole reason this runs at host start: the backend selector only trusts a managed runtime once something has set it, so without this an adopted GPU build
        // stops resolving after a restart and the node silently falls back.
        var installed = await _runtimeStore.ReadAsync(ct).ConfigureAwait(false);
        if (installed?.Validity == ManagedRuntimeValidity.Active)
        {
            _managedSignal.SetActive(installed.DesiredBackend);
        }
        else
        {
            _managedSignal.Clear();
        }
    }

    /// <summary>Runs one build command, logging its sanitized command line and output; a non-zero exit throws.</summary>
    protected async Task<SourceCommandResult> RunRequiredAsync(string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        bool captureOutput,
        CancellationToken ct)
    {
        // Sanitized like every child output line: cmake's -S/-B arguments are absolute paths under the cache root,
        // which in production sits inside the operator's home directory.
        AppendLog(SanitizeLogLine($"> {Path.GetFileName(fileName)} {string.Join(' ', arguments)}"));
        var result = await Runner.RunAsync(fileName,
                                     arguments,
                                     workingDirectory,
                                     line => AppendLog(SanitizeLogLine(line)),
                                     timeout,
                                     captureOutput,
                                     ct)
                                 .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw RuntimeError($"The source-build command '{Path.GetFileName(fileName)}' failed.");
        }

        return result;
    }

    /// <summary>How a normalized request's revision is chosen.</summary>
    protected static ManagedSourceRevisionMode ResolveRevisionMode(ManagedSourceSelection source, string? commit)
    {
        var revisionMode = source == ManagedSourceSelection.Official
            ? ManagedSourceRevisionMode.EnginePinned
            : ManagedSourceRevisionMode.ExplicitCommit;
        if (source == ManagedSourceSelection.Custom && commit is null)
        {
            revisionMode = ManagedSourceRevisionMode.DefaultBranch;
        }

        return revisionMode;
    }

    /// <summary>The directory name a backend's managed builds live under.</summary>
    protected abstract string BackendDirectoryName(TBackend backend);

    /// <summary>The runtime's own display-safe exception carrying <paramref name="message" />.</summary>
    protected abstract Exception RuntimeError(string message);

    /// <summary>The build with the commit its checkout actually landed on.</summary>
    protected abstract TDescriptor WithResolvedCommit(TDescriptor build, string resolvedCommit);

    /// <summary>The cmake configure arguments, after whatever backend probing the runtime needs.</summary>
    protected abstract Task<IReadOnlyList<string>> ConfigureArgumentsAsync(TDescriptor build, string sourceDir, string buildDir, CancellationToken ct);

    /// <summary>Proves the built tree is what was asked for, before it is smoke-tested and adopted.</summary>
    protected abstract Task ValidateBuildAsync(TDescriptor build, string buildDir, string serverPath, CancellationToken ct);

    protected abstract Task AdoptAsync(TDescriptor build, string buildDir, string serverPath, CancellationToken ct);

    protected abstract Task PublishStatusAsync(StatusChange change, CancellationToken ct);

    private async Task<BuildCompletion> RunBuildAsync<TReport>(StartPlan<TReport> plan, CancellationToken ct)
        where TReport : class
    {
        var descriptor = plan.Build;
        try
        {
            PrepareWorkTree();
            await File.WriteAllTextAsync(MarkerPath, plan.BuildId.ToString("D"), ct).ConfigureAwait(false);
            var sourceDir = Path.Combine(WorkRoot, "source");
            var buildDir = Path.Combine(WorkRoot, "build");

            SetPhase(BuildPhase.Cloning);
            var requestedCommit = plan.CommitToFetch;
            await RunRequiredAsync("git", GitArguments("init", sourceDir), WorkRoot, SourceBuildPolicy.ShortCommandTimeout, captureOutput: false, ct)
                .ConfigureAwait(false);
            await RunRequiredAsync("git",
                    GitArguments("remote", "add", "origin", plan.Repository),
                    sourceDir,
                    SourceBuildPolicy.ShortCommandTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);
            await RunRequiredAsync("git",
                    GitArguments("fetch", "--depth=1", "--no-tags", "--no-recurse-submodules", "origin", requestedCommit ?? "HEAD"),
                    sourceDir,
                    SourceBuildPolicy.CloneTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);

            SetPhase(BuildPhase.Verifying);
            await RunRequiredAsync("git",
                    GitArguments("checkout", "--detach", "FETCH_HEAD"),
                    sourceDir,
                    SourceBuildPolicy.ShortCommandTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);
            var resolvedCommit = (await RunRequiredAsync("git",
                    GitArguments("rev-parse", "HEAD"),
                    sourceDir,
                    SourceBuildPolicy.ShortCommandTimeout,
                    captureOutput: true,
                    ct)
                .ConfigureAwait(false)).StandardOutput.Trim();

            // The engine-pinned revision is a PEELED commit SHA, never a tag object: fetching a tag object's SHA has nothing to land on. Asserting the checkout resolved to exactly what was asked for
            // is what catches a pin that silently drifted.
            if (resolvedCommit.Length != 40
                || !resolvedCommit.All(Uri.IsHexDigit)
                || (requestedCommit is not null && !string.Equals(resolvedCommit, requestedCommit, StringComparison.OrdinalIgnoreCase)))
            {
                throw RuntimeError("The source checkout did not resolve to the requested exact commit.");
            }

            await RunRequiredAsync("git",
                    GitArguments("submodule", "update", "--init", "--recursive"),
                    sourceDir,
                    SourceBuildPolicy.CloneTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);

            descriptor = WithResolvedCommit(descriptor, Convert.ToHexStringLower(Convert.FromHexString(resolvedCommit)));
            lock (_stateLock)
            {
                _currentBuild = descriptor;
            }

            SetPhase(BuildPhase.Configuring);
            var cmakeArgs = await ConfigureArgumentsAsync(descriptor, sourceDir, buildDir, ct).ConfigureAwait(false);
            await RunRequiredAsync("cmake", cmakeArgs, WorkRoot, SourceBuildPolicy.ConfigureTimeout, captureOutput: false, ct).ConfigureAwait(false);

            SetPhase(BuildPhase.Building);
            var buildJobs = Math.Max(1, Math.Min(Environment.ProcessorCount, SourceBuildPolicy.MaxBuildJobs));
            await RunRequiredAsync("cmake",
                    [
                        "--build", buildDir,
                        "--target", .. _buildTargets,
                        "--config", "Release",
                        "--parallel", buildJobs.ToString(CultureInfo.InvariantCulture)
                    ],
                    WorkRoot,
                    SourceBuildPolicy.BuildTimeout,
                    captureOutput: false,
                    ct)
                .ConfigureAwait(false);

            var serverPath = FindServer(buildDir)
                             ?? throw RuntimeError($"The {_runtimeName} build did not produce {_serverName}.");
            EnsureExecutable(serverPath);
            await ValidateBuildAsync(descriptor, buildDir, serverPath, ct).ConfigureAwait(false);

            SetPhase(BuildPhase.SmokeTesting);
            await RunRequiredAsync(serverPath, ["--help"], Path.GetDirectoryName(serverPath)!, SourceBuildPolicy.SmokeTimeout, captureOutput: false, ct)
                .ConfigureAwait(false);

            SetPhase(BuildPhase.Adopting);
            await AdoptAsync(descriptor, buildDir, serverPath, ct).ConfigureAwait(false);
            return new BuildCompletion
            {
                Phase = BuildPhase.Completed,
                Error = null
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new BuildCompletion
            {
                Phase = BuildPhase.Cancelled,
                Error = null
            };
        }
        catch (TimeoutException exception)
        {
            _logger.LogWarning(exception, "{RuntimeName} source build timed out.", _runtimeName);
            return new BuildCompletion
            {
                Phase = BuildPhase.Failed,
                Error = $"A {_runtimeName} source-build command timed out. Review the sanitized build log."
            };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "{RuntimeName} source build failed.", _runtimeName);
            return new BuildCompletion
            {
                Phase = BuildPhase.Failed,
                Error = $"The {_runtimeName} source build failed. Review the sanitized build log."
            };
        }
        finally
        {
            BestEffortFile.TryDeleteDirectory(WorkRoot);
        }
    }

    private void AppendLog(string line)
    {
        StatusChange change;
        lock (_stateLock)
        {
            _logLines.Add(line);
            _nextLogSequence++;
            if (_logLines.Count > MaxLogLines)
            {
                _logLines.RemoveAt(0);
                _logStartSequence++;
            }

            change = CreateChangeUnderLock([line], _nextLogSequence - 1);
        }

        QueuePublish(change);
    }

    private void SetPhase(BuildPhase phase)
    {
        StatusChange change;
        lock (_stateLock)
        {
            _phase = phase;
            change = CreateChangeUnderLock([], _nextLogSequence);
        }

        QueuePublish(change);
    }

    private void SetTerminal(BuildPhase phase, string? error)
    {
        StatusChange change;
        lock (_stateLock)
        {
            _phase = phase;
            _sanitizedError = error;
            _completedAtUtc = _timeProvider.GetUtcNow();
            change = CreateChangeUnderLock([], _nextLogSequence);
        }

        QueuePublish(change);
    }

    private void CompleteBuild(BuildCompletion completion)
    {
        StatusChange change;
        lock (_stateLock)
        {
            _phase = completion.Phase;
            _isRunning = false;
            _sanitizedError = completion.Error;
            _completedAtUtc = _timeProvider.GetUtcNow();
            _buildCts?.Dispose();
            _buildCts = null;
            change = CreateChangeUnderLock([], _nextLogSequence);
        }

        QueuePublish(change);
    }

    private StatusChange CreateChangeUnderLock(IReadOnlyList<string> appended, long startSequence)
    {
        return new StatusChange
        {
            Phase = _phase,
            AppendedLogLines = appended,
            AppendedLogStartSequence = startSequence,
            Terminal = IsTerminal(_phase),
            SanitizedError = _sanitizedError,
            CurrentBuild = _currentBuild
        };
    }

    private void QueuePublish(StatusChange change)
    {
        if (!_publishQueue.Writer.TryWrite(change))
        {
            _logger.LogDebug("Dropped a {RuntimeName} source-build status event because the publisher is stopping.", _runtimeName);
        }
    }

    private async Task PublishLoopAsync()
    {
        await foreach (var change in _publishQueue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                await PublishStatusAsync(change, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to publish {RuntimeName} source-build status.", _runtimeName);
            }
        }
    }

    private void PrepareWorkTree()
    {
        BestEffortFile.TryDeleteDirectory(WorkRoot);
        SourceProcessHardening.CreateOwnerOnlyDirectory(BuildRoot);
        SourceProcessHardening.CreateOwnerOnlyDirectory(WorkRoot);
    }

    private void DeleteManagedRuntime(ManagedRuntimeState<TBackend> installed)
    {
        var installRoot = GetManagedInstallRoot(installed);
        if (Directory.Exists(installRoot))
        {
            Directory.Delete(installRoot, recursive: true);
        }

        if (Directory.Exists(installRoot))
        {
            throw RuntimeError($"The managed {_managedRuntimeLabel} could not be removed.");
        }
    }

    // Derived from the record's own commit and backend rather than from its recorded path, so a tampered or
    // tombstoned record can never point a recursive delete outside the managed cache.
    private string GetManagedInstallRoot(ManagedRuntimeState<TBackend> installed)
    {
        if (installed.SourceCommit.Length != 40 || !installed.SourceCommit.All(Uri.IsHexDigit))
        {
            throw RuntimeError($"The recorded managed {_managedRuntimeLabel} commit is invalid.");
        }

        var root = Path.GetFullPath(RuntimeRoot);
        var installRoot = Path.GetFullPath(Path.Combine(root, BackendDirectoryName(installed.DesiredBackend), installed.SourceCommit));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!installRoot.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw RuntimeError($"The recorded managed {_managedRuntimeLabel} path is outside the managed cache.");
        }

        return installRoot;
    }

    private string? FindServer(string buildDir)
    {
        return Directory.Exists(buildDir)
            ? Directory.EnumerateFiles(buildDir, _serverName, SearchOption.AllDirectories).FirstOrDefault()
            : null;
    }

    private static bool IsTerminal(BuildPhase phase)
    {
        return phase is BuildPhase.Completed or BuildPhase.Cancelled or BuildPhase.Failed;
    }

    private static void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute);
        }
    }

    private static string SanitizeLogLine(string line)
    {
        var value = line.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~", StringComparison.Ordinal);
        return value.Length <= 1000 ? value : value[..1000];
    }

    private static IReadOnlyList<string> GitArguments(params string[] arguments)
    {
        return
        [
            "-c", "protocol.allow=never",
            "-c", "protocol.https.allow=always",
            "-c", "credential.helper=",
            "-c", "core.askPass=",
            .. arguments
        ];
    }

    /// <summary>A validated request, its prerequisite report and the build it would run.</summary>
    protected sealed class StartPlan<TReport>
        where TReport : class
    {
        public required TReport Prerequisites { get; init; }

        public required bool CanBuild { get; init; }

        /// <summary>Whether the free-disk row is what blocks the build.</summary>
        public required bool InsufficientDisk { get; init; }

        public required TDescriptor Build { get; init; }

        public required Guid BuildId { get; init; }

        public required string Repository { get; init; }

        /// <summary>The exact commit to fetch (the engine pin for an official build), or null for the default branch.</summary>
        public required string? CommitToFetch { get; init; }
    }

    /// <summary>A poll-shaped snapshot of the running or last-finished build.</summary>
    protected sealed class StatusSnapshot
    {
        public required BuildPhase Phase { get; init; }

        public required bool IsRunning { get; init; }

        public required bool Terminal { get; init; }

        public required IReadOnlyList<string> LogLines { get; init; }

        public required long LogStartSequence { get; init; }

        public required string? SanitizedError { get; init; }

        public required TDescriptor? CurrentBuild { get; init; }

        public required DateTimeOffset? StartedAtUtc { get; init; }

        public required DateTimeOffset? CompletedAtUtc { get; init; }
    }

    /// <summary>One change in the build's state: the phase now, and only the log lines appended since the last change.</summary>
    protected sealed class StatusChange
    {
        public required BuildPhase Phase { get; init; }

        public required IReadOnlyList<string> AppendedLogLines { get; init; }

        public required long AppendedLogStartSequence { get; init; }

        public required bool Terminal { get; init; }

        public required string? SanitizedError { get; init; }

        public required TDescriptor? CurrentBuild { get; init; }
    }

    private sealed record BuildCompletion
    {
        public required BuildPhase Phase { get; init; }

        public required string? Error { get; init; }
    }
}
