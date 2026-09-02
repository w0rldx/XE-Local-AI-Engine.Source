namespace XE_Local_AI_Engine.Providers.Training.Implementation;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Default <see cref="ITrainingRuntimeService" />. Orchestrates a single-flight, cancellable, background provision
///     of the uv-managed Python training runtime and adopts the result. Modelled on
///     <c>LlamaCppSourceBuildService</c>: a serialized start transaction, a detached worker, a bounded log ring streamed
///     to a hub, and a staged→atomic adopt that parks the previous runtime in a backup so a failed re-provision never
///     loses a working one.
/// </summary>
/// <remarks>
///     <para>
///         Every subprocess runs under a scrubbed, allow-listed environment in an owner-only (0700) work directory
///         inside the cache root — never <c>/tmp</c>. The install is strictly lockfile-driven (<c>uv sync --locked</c>):
///         if the committed <c>uv.lock</c> does not match <c>pyproject.toml</c>, uv fails rather than resolving
///         something new, which is the whole point of ADR 0005's "no floating resolves".
///     </para>
///     <para>
///         <strong>The adopt is one rollback boundary spanning the directory swap AND the state write</strong>, and the
///         backup is deleted only once both have succeeded. Splitting them loses a working runtime: a cancellation
///         between the swap and the write leaves the new venv active and the previous one parked in a backup that the
///         next install's <see cref="Recover" /> deletes as garbage. For the same reason, a failure that leaves a
///         previous runtime intact terminalizes as <see cref="TrainingRuntimePhase.Ready" /> carrying the failure as the
///         sanitized error — <c>Failed</c> is what the training and export gates read, so reporting it would retire a
///         runtime that still works.
///     </para>
/// </remarks>
public sealed class TrainingRuntimeService : ITrainingRuntimeService, IDisposable
{
    // The number of streamed log lines retained for the status GET (the hub streams every line live).
    private const int LogRingCapacity = 400;

    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(10);

    private readonly UvBinaryAcquirer _acquirer;
    private readonly string _cacheRoot;
    private readonly string _homeDirectory;
    private readonly ILogger<TrainingRuntimeService> _logger;
    private readonly ITrainingRuntimeEventPublisher _publisher;
    private readonly ITrainingRuntimePrerequisiteProbe _prerequisiteProbe;
    private readonly ITrainingProcessRunner _processRunner;
    private readonly string _scriptsDirectory;
    private readonly InstalledTrainingRuntimeStore _stateStore;
    private readonly Lock _publishLock = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Lock _stateLock = new();

    private Task? _activeTask;
    private CancellationTokenSource? _cts;
    private DateTimeOffset? _completedAtUtc;
    private InstalledTrainingRuntimeState? _installed;
    private bool _isRunning;
    private List<string> _logLines = [];
    private long _logStartSequence;
    private long _nextLogSequence;
    private TrainingRuntimePhase _phase;
    private Task _publishTail = Task.CompletedTask;
    private string? _sanitizedError;
    private DateTimeOffset? _startedAtUtc;

    public TrainingRuntimeService(ITrainingRuntimePrerequisiteProbe prerequisiteProbe,
        ITrainingRuntimeEventPublisher publisher,
        HttpClient httpClient,
        ILogger<TrainingRuntimeService> logger)
        : this(prerequisiteProbe,
            publisher,
            new UvBinaryAcquirer(httpClient),
            new LinuxTrainingProcessRunner(),
            logger,
            TrainingRuntimeLayout.DefaultCacheRoot(),
            TrainingRuntimeLayout.ResolveScriptsDirectory())
    {
    }

    internal TrainingRuntimeService(ITrainingRuntimePrerequisiteProbe prerequisiteProbe,
        ITrainingRuntimeEventPublisher publisher,
        UvBinaryAcquirer acquirer,
        ITrainingProcessRunner processRunner,
        ILogger<TrainingRuntimeService> logger,
        string cacheRoot,
        string scriptsDirectory)
    {
        _prerequisiteProbe = prerequisiteProbe ?? throw new ArgumentNullException(nameof(prerequisiteProbe));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _acquirer = acquirer ?? throw new ArgumentNullException(nameof(acquirer));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptsDirectory);
        _cacheRoot = cacheRoot;
        _scriptsDirectory = scriptsDirectory;
        _stateStore = new InstalledTrainingRuntimeStore(TrainingRuntimeLayout.StatePath(cacheRoot));
        _homeDirectory = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        _installed = _stateStore.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        _phase = _installed is not null ? TrainingRuntimePhase.Ready : TrainingRuntimePhase.Idle;
    }

    private string WorkDirectory => Path.Combine(_cacheRoot, ".work");

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            cts = _cts;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A completed install may dispose its token concurrently with application teardown.
        }

        cts?.Dispose();
        _startGate.Dispose();
    }

    /// <inheritdoc />
    public async Task<TrainingRuntimeInstallResult> InstallAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new TrainingRuntimeException("The Python training runtime is available on Linux only.");
        }

        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        TaskCompletionSource? startSignal = null;
        try
        {
            lock (_stateLock)
            {
                if (_isRunning)
                {
                    return new TrainingRuntimeInstallResult(TrainingRuntimeInstallOutcome.AlreadyRunning);
                }
            }

            // Reconciliation and prerequisite validation are part of the serialized start transaction: a losing caller
            // must not delete the winning install's work tree or repeat the probe.
            Recover();
            var report = await _prerequisiteProbe.ProbeAsync(ct).ConfigureAwait(false);
            if (!report.CanInstall)
            {
                var outcome = report.Items.Any(static item =>
                    string.Equals(item.Key, TrainingRuntimePrerequisiteKeys.FreeDisk, StringComparison.Ordinal) && !item.Satisfied)
                    ? TrainingRuntimeInstallOutcome.InsufficientDisk
                    : TrainingRuntimeInstallOutcome.MissingPrerequisites;
                return new TrainingRuntimeInstallResult(outcome, report);
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
            startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var installTask = Task.Run(async () =>
            {
                await startSignal.Task.ConfigureAwait(false);
                // InstallAsync rejects non-Linux callers before probing. This local guard carries that invariant into
                // the detached lambda for the platform analyzer.
                if (OperatingSystem.IsLinux())
                {
                    await RunInstallAsync(cts.Token).ConfigureAwait(false);
                }
            }, CancellationToken.None);

            lock (_stateLock)
            {
                _isRunning = true;
                _phase = TrainingRuntimePhase.AcquiringUv;
                _logLines = [];
                _logStartSequence = 0;
                _nextLogSequence = 0;
                _sanitizedError = null;
                _startedAtUtc = DateTimeOffset.UtcNow;
                _completedAtUtc = null;
                _cts?.Dispose();
                _cts = cts;
                _activeTask = installTask;
            }
        }
        finally
        {
            _startGate.Release();
        }

        // Release the start transaction before letting the detached install touch the work tree.
        startSignal.SetResult();
        return new TrainingRuntimeInstallResult(TrainingRuntimeInstallOutcome.Started);
    }

    /// <inheritdoc />
    public TrainingRuntimeStatus GetStatus()
    {
        lock (_stateLock)
        {
            return new TrainingRuntimeStatus(_phase,
                _isRunning,
                Terminal: _phase is TrainingRuntimePhase.Ready or TrainingRuntimePhase.Failed or TrainingRuntimePhase.Idle,
                LogLines: [.. _logLines],
                _logStartSequence,
                _sanitizedError,
                _installed,
                _startedAtUtc,
                _completedAtUtc);
        }
    }

    /// <inheritdoc />
    public bool Cancel()
    {
        lock (_stateLock)
        {
            if (!_isRunning || _cts is null)
            {
                return false;
            }

            try
            {
                _cts.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(CancellationToken ct)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                if (_isRunning)
                {
                    return false;
                }

                _phase = TrainingRuntimePhase.Removing;
            }

            await PublishPhaseAsync(TrainingRuntimePhase.Removing, terminal: false, sanitizedError: null).ConfigureAwait(false);
            DeleteDirectoryRequired(TrainingRuntimeLayout.VenvRoot(_cacheRoot));
            TryDeleteDirectory(WorkDirectory);
            _stateStore.Delete();

            lock (_stateLock)
            {
                _installed = null;
                _phase = TrainingRuntimePhase.Idle;
                _sanitizedError = null;
                _completedAtUtc = DateTimeOffset.UtcNow;
            }

            await PublishPhaseAsync(TrainingRuntimePhase.Idle, terminal: true, sanitizedError: null).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <inheritdoc />
    public string? ResolveInterpreterPath()
    {
        lock (_stateLock)
        {
            if (_installed is null)
            {
                return null;
            }
        }

        var interpreter = TrainingRuntimeLayout.InterpreterPath(TrainingRuntimeLayout.ActiveVenv(_cacheRoot));
        return File.Exists(interpreter) ? interpreter : null;
    }

    /// <summary>Waits for an in-flight install to finish, for deterministic teardown and tests.</summary>
    internal async Task DrainAsync(CancellationToken ct)
    {
        Task? active;
        lock (_stateLock)
        {
            active = _activeTask;
        }

        if (active is not null)
        {
            await active.WaitAsync(ct).ConfigureAwait(false);
        }

        await FlushPublisherAsync().WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Drops leftovers from an interrupted install. A <c>.staging</c> venv is by definition unadopted, and the work
    ///     tree holds nothing durable, so both are removed unconditionally; a <c>.backup</c> present without an
    ///     <c>active</c> means the swap died between the two moves, so the backup is restored rather than discarded.
    /// </summary>
    internal void Recover()
    {
        TryDeleteDirectory(WorkDirectory);
        TryDeleteDirectory(TrainingRuntimeLayout.StagingVenv(_cacheRoot));

        var active = TrainingRuntimeLayout.ActiveVenv(_cacheRoot);
        var backup = TrainingRuntimeLayout.BackupVenv(_cacheRoot);
        if (!Directory.Exists(backup))
        {
            return;
        }

        if (Directory.Exists(active))
        {
            TryDeleteDirectory(backup);
            return;
        }

        Directory.Move(backup, active);
    }

    [SupportedOSPlatform("linux")]
    private async Task RunInstallAsync(CancellationToken ct)
    {
        var workDir = WorkDirectory;
        var staging = TrainingRuntimeLayout.StagingVenv(_cacheRoot);
        var active = TrainingRuntimeLayout.ActiveVenv(_cacheRoot);
        var backup = TrainingRuntimeLayout.BackupVenv(_cacheRoot);

        try
        {
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(staging);
            TryDeleteDirectory(backup);
            CreateOwnerOnlyDirectory(_cacheRoot);
            CreateOwnerOnlyDirectory(workDir);

            var isolatedHome = Path.Combine(workDir, ".home");
            var isolatedTmp = Path.Combine(workDir, ".tmp");
            CreateOwnerOnlyDirectory(isolatedHome);
            CreateOwnerOnlyDirectory(isolatedTmp);
            var environment = TrainingRuntimeEnvironment.BuildUvEnvironment(isolatedHome,
                isolatedTmp,
                Path.Combine(_cacheRoot, "uv-cache"),
                Path.Combine(_cacheRoot, "pythons"));

            // 1. Acquire the pinned uv (download → digest verify → atomic extract; a cache hit skips the network).
            SetPhase(TrainingRuntimePhase.AcquiringUv);
            var uv = await _acquirer.EnsureUvAsync(_cacheRoot, AppendLog, ct).ConfigureAwait(false);

            // 2. Stage the project files. uv resolves the environment beside the pyproject it is pointed at, so the
            //    committed pair is copied into staging rather than the shipped (read-only) scripts directory being used
            //    as a working tree.
            SetPhase(TrainingRuntimePhase.ProvisioningPython);
            CreateOwnerOnlyDirectory(TrainingRuntimeLayout.VenvRoot(_cacheRoot));
            CreateOwnerOnlyDirectory(staging);
            var lockfileSha = await StageProjectFilesAsync(staging, ct).ConfigureAwait(false);

            // 3. Install strictly from the lockfile. --locked makes uv fail rather than re-resolve when the lockfile
            //    and pyproject.toml disagree, which is what makes this reproducible instead of merely repeatable.
            SetPhase(TrainingRuntimePhase.InstallingPackages);
            var syncExit = await _processRunner.RunAsync(uv,
                ["sync", "--locked", "--project", staging],
                environment,
                staging,
                AppendLog,
                SyncTimeout,
                ct).ConfigureAwait(false);
            if (syncExit != 0)
            {
                throw new TrainingRuntimeException("Installing the pinned training runtime packages failed. See the install log for the failing step.");
            }

            // 4. Verify the staged environment before adopting anything.
            SetPhase(TrainingRuntimePhase.Verifying);
            var probeReport = await RunProbeAsync(staging, ct).ConfigureAwait(false);

            // 5. Adopt. The rollback boundary covers the directory swap AND the state write, because the two together
            //    are what makes a runtime adopted: a failure between them leaves the new venv active, the previous one
            //    parked in a backup the next Recover() deletes, and a state record describing neither.
            var previousState = ReadInstalledState();
            var hadPrevious = Directory.Exists(active);
            var parked = false;
            var swapped = false;
            try
            {
                if (hadPrevious)
                {
                    Directory.Move(active, backup);
                    parked = true;
                }

                Directory.Move(staging, active);
                swapped = true;

                var state = new InstalledTrainingRuntimeState(TrainingRuntimePins.UvVersion,
                    TrainingRuntimePins.UvSha256,
                    probeReport.PythonVersion ?? "unknown",
                    lockfileSha,
                    probeReport.ContractVersion,
                    DateTimeOffset.UtcNow,
                    probeReport.TorchVersion,
                    probeReport.UnslothVersion,
                    probeReport.DeviceName);
                await _stateStore.WriteAsync(state, ct).ConfigureAwait(false);

                // Only now is the previous runtime genuinely superseded, so only now may the backup go.
                TryDeleteDirectory(backup);
                TryDeleteDirectory(workDir);
                AppendLog($"Training runtime ready on Python {state.PythonVersion} with torch {state.TorchVersion}.");
                await SetTerminalAsync(TrainingRuntimePhase.Ready, sanitizedError: null, state).ConfigureAwait(false);
            }
            catch
            {
                await RollbackAdoptAsync(parked, swapped, previousState, staging, active, backup).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(staging);
            await TerminalizeFailureAsync("The training runtime install was cancelled.").ConfigureAwait(false);
        }
        catch (TrainingRuntimeException exception)
        {
            _logger.LogWarning(exception, "The training runtime install failed.");
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(staging);
            await TerminalizeFailureAsync(exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The training runtime install failed unexpectedly.");
            TryDeleteDirectory(workDir);
            TryDeleteDirectory(staging);
            await TerminalizeFailureAsync("The training runtime install failed unexpectedly.").ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Undoes as much of the adopt as actually happened, in reverse: drop the staging tree, drop the half-adopted
    ///     new runtime, restore the parked previous one, and put its state record back. The state restore is
    ///     unconditional once the swap succeeded because <see cref="InstalledTrainingRuntimeStore.WriteAsync" /> is
    ///     atomic (temp file + move) and there is no way to tell a write that never landed from one that did.
    ///     Best-effort throughout: the caller rethrows the original failure, and a backup left behind by a failed
    ///     restore is picked up by <see cref="Recover" /> on the next install.
    /// </summary>
    private async Task RollbackAdoptAsync(bool parked,
        bool swapped,
        InstalledTrainingRuntimeState? previousState,
        string staging,
        string active,
        string backup)
    {
        try
        {
            TryDeleteDirectory(staging);
            if (swapped)
            {
                TryDeleteDirectory(active);
            }

            if (parked)
            {
                Directory.Move(backup, active);
            }

            if (!swapped)
            {
                return;
            }

            if (previousState is not null)
            {
                // Not ct: the rollback of a cancelled install must still run to completion.
                await _stateStore.WriteAsync(previousState, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                _stateStore.Delete();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Rolling the training runtime adopt back failed; the previous runtime may need a reinstall.");
        }
    }

    /// <summary>
    ///     Terminalizes a failed install. A reprovision that left the previous runtime intact ends <c>Ready</c> with the
    ///     failure carried in the sanitized error: training and export gate on <see cref="TrainingRuntimePhase.Ready" />,
    ///     so reporting <c>Failed</c> there would take a perfectly good runtime out of service until some later install
    ///     happened to succeed. Only a failure with no surviving runtime ends <c>Failed</c>.
    /// </summary>
    private async Task TerminalizeFailureAsync(string sanitizedError)
    {
        var surviving = ReadInstalledState();
        if (surviving is not null && Directory.Exists(TrainingRuntimeLayout.ActiveVenv(_cacheRoot)))
        {
            await SetTerminalAsync(TrainingRuntimePhase.Ready, sanitizedError, surviving).ConfigureAwait(false);
            return;
        }

        await SetTerminalAsync(TrainingRuntimePhase.Failed, sanitizedError, installed: null).ConfigureAwait(false);
    }

    private InstalledTrainingRuntimeState? ReadInstalledState()
    {
        lock (_stateLock)
        {
            return _installed;
        }
    }

    /// <summary>
    ///     Copies the committed <c>pyproject.toml</c> + <c>uv.lock</c> into the staging directory and returns the
    ///     lockfile digest, which is recorded so a later release can tell whether the installed environment predates a
    ///     lockfile bump.
    /// </summary>
    private async Task<string> StageProjectFilesAsync(string staging, CancellationToken ct)
    {
        var project = Path.Combine(_scriptsDirectory, TrainingRuntimeLayout.ProjectFileName);
        var lockfile = Path.Combine(_scriptsDirectory, TrainingRuntimeLayout.LockfileName);
        if (!File.Exists(project) || !File.Exists(lockfile))
        {
            throw new TrainingRuntimeException("The pinned training runtime lockfile is missing from this installation.");
        }

        File.Copy(project, Path.Combine(staging, TrainingRuntimeLayout.ProjectFileName), overwrite: true);
        File.Copy(lockfile, Path.Combine(staging, TrainingRuntimeLayout.LockfileName), overwrite: true);

        await using var stream = new FileStream(lockfile, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    [SupportedOSPlatform("linux")]
    private async Task<TrainingRuntimeProbeReport> RunProbeAsync(string venvDirectory, CancellationToken ct)
    {
        var interpreter = TrainingRuntimeLayout.InterpreterPath(venvDirectory);
        if (!File.Exists(interpreter))
        {
            throw new TrainingRuntimeException("The provisioned training runtime did not contain a Python interpreter.");
        }

        var probeScript = Path.Combine(_scriptsDirectory, TrainingRuntimeLayout.ProbeScriptName);
        if (!File.Exists(probeScript))
        {
            throw new TrainingRuntimeException("The training runtime verification script is missing from this installation.");
        }

        var captured = new List<string>();
        var exitCode = await _processRunner.RunAsync(interpreter,
            [probeScript],
            // The staged venv directory is owner-only (0700), so it is a safe HOME for torch's compilation caches.
            TrainingRuntimeEnvironment.BuildProbeEnvironment(venvDirectory),
            venvDirectory,
            line =>
            {
                captured.Add(line);
                AppendLog(line);
            },
            ProbeTimeout,
            ct).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new TrainingRuntimeException("Verifying the provisioned training runtime failed.");
        }

        var report = TrainingRuntimeProbeParser.TryParse(captured)
                     ?? throw new TrainingRuntimeException("The training runtime verification produced no usable result.");

        if (report.ContractVersion != TrainingRuntimePins.ProbeContractVersion)
        {
            throw new TrainingRuntimeException("The training runtime scripts are a different version than this application expects.");
        }

        if (report.Errors.Count > 0)
        {
            // Name the failing package but not its traceback: the message is a user-facing sanitized error, and the
            // full text is already in the streamed log.
            var packages = string.Join(", ", report.Errors.Keys.Order(StringComparer.Ordinal));
            throw new TrainingRuntimeException($"The provisioned training runtime could not load: {packages}.");
        }

        if (!report.CudaAvailable)
        {
            throw new TrainingRuntimeException("The provisioned training runtime cannot reach the GPU. Check the NVIDIA driver and try again.");
        }

        return report;
    }

    private void SetPhase(TrainingRuntimePhase phase)
    {
        lock (_stateLock)
        {
            _phase = phase;
            _ = QueuePublish(new TrainingRuntimeStatusHubEvent(phase.ToString(),
                [],
                _nextLogSequence,
                Terminal: false,
                SanitizedError: null));
        }
    }

    /// <summary>
    ///     Records the resting state. <paramref name="installed" /> is written through verbatim, null included: the
    ///     caller has already decided whether a runtime survived, and a status that kept advertising one that did not
    ///     would be a lie the UI has no way to see through.
    /// </summary>
    private async Task SetTerminalAsync(TrainingRuntimePhase phase, string? sanitizedError, InstalledTrainingRuntimeState? installed)
    {
        Task publish;
        lock (_stateLock)
        {
            _phase = phase;
            _isRunning = false;
            _sanitizedError = sanitizedError;
            _completedAtUtc = DateTimeOffset.UtcNow;
            _installed = installed;

            publish = QueuePublish(new TrainingRuntimeStatusHubEvent(phase.ToString(),
                [],
                _nextLogSequence,
                Terminal: true,
                sanitizedError));
        }

        await publish.ConfigureAwait(false);
    }

    private Task PublishPhaseAsync(TrainingRuntimePhase phase, bool terminal, string? sanitizedError)
    {
        lock (_stateLock)
        {
            return QueuePublish(new TrainingRuntimeStatusHubEvent(phase.ToString(), [], _nextLogSequence, terminal, sanitizedError));
        }
    }

    // The streaming log sink: redact the cache-root/HOME prefix, retain a bounded ring for the status GET, and push the
    // line live to the hub. Thread-safe (both process pipes call this concurrently).
    internal void AppendLog(string line)
    {
        var redacted = Redact(line);
        lock (_stateLock)
        {
            var appendedSequence = _nextLogSequence++;
            _logLines.Add(redacted);
            if (_logLines.Count > LogRingCapacity)
            {
                _logLines.RemoveRange(0, _logLines.Count - LogRingCapacity);
            }

            _logStartSequence = _nextLogSequence - _logLines.Count;
            _ = QueuePublish(new TrainingRuntimeStatusHubEvent(_phase.ToString(),
                [redacted],
                appendedSequence,
                Terminal: false,
                SanitizedError: null));
        }
    }

    private string Redact(string line)
    {
        var result = line;
        if (!string.IsNullOrEmpty(_cacheRoot))
        {
            result = result.Replace(_cacheRoot, "<cache>", StringComparison.Ordinal);
        }

        if (!string.IsNullOrEmpty(_homeDirectory))
        {
            result = result.Replace(_homeDirectory, "<home>", StringComparison.Ordinal);
        }

        return result;
    }

    internal Task FlushPublisherAsync()
    {
        lock (_publishLock)
        {
            return _publishTail;
        }
    }

    private Task QueuePublish(TrainingRuntimeStatusHubEvent statusEvent)
    {
        lock (_publishLock)
        {
            _publishTail = PublishObservedAsync(_publishTail, statusEvent);
            return _publishTail;
        }
    }

    private async Task PublishObservedAsync(Task previous, TrainingRuntimeStatusHubEvent statusEvent)
    {
        // Process stdout/stderr callbacks must never run publisher work inline; yield before joining the serialized tail.
        await Task.Yield();
        await previous.ConfigureAwait(false);
        try
        {
            await _publisher.PublishStatusAsync(statusEvent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Publishing a training-runtime status event failed (non-fatal).");
        }
    }

    private static void CreateOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
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
            // Best-effort cleanup of a partial install tree.
        }
    }

    private static void DeleteDirectoryRequired(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new TrainingRuntimeException("The installed training runtime could not be removed. Close anything using it and try again.",
                exception);
        }
    }
}
