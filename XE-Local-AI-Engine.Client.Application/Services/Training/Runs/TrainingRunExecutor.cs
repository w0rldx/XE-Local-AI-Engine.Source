namespace XE_Local_AI_Engine.Client.Services.Training.Runs;

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

public interface ITrainingRunExecutor
{
    Task ExecuteAsync(TrainingWorkClaim claim, CancellationToken stoppingToken);
}

/// <summary>
///     Drives one claimed run from <c>Preparing</c> to a terminal state: reserve capacity, verify the runtime, decrypt
///     the frozen dataset into owner-only scratch, spawn <c>train.py</c>, and follow its stdio protocol until it exits.
/// </summary>
/// <remarks>
///     <para>
///         The launch receipt is persisted immediately after the spawn — before any output is read — because that is
///         the only window in which a host crash could otherwise strand a trainer holding the whole GPU with nothing on
///         disk to identify it by.
///     </para>
///     <para>
///         Two independent bounds sit over the stream. The inactivity watchdog kills the process group when nothing
///         parseable arrives for its configured window: a trainer that is wedged on a CUDA call prints nothing at all,
///         and the heartbeat event exists precisely so a long silent phase can be told apart from a dead one. The
///         max-duration bound is the backstop for a configuration that is merely pathological rather than stuck. A
///         third bound sits after the stream: a trainer that closes its output and then never exits is killed rather
///         than waited on, because waiting holds the run's capacity reservation open indefinitely.
///     </para>
///     <para>
///         Cancellation is cooperative: the operator's cancel signals the process GROUP with SIGTERM, <c>train.py</c>
///         latches <c>should_training_stop</c>, finishes its step and exits with a distinct status, and the run is
///         recorded as <c>Cancelled</c>. Only the watchdog escalates to SIGKILL.
///     </para>
/// </remarks>
public sealed class TrainingRunExecutor(
    ITrainingRunStore store,
    ITrainingRunEventBuffer events,
    ITrainingOptionDefaultsCalculator defaults,
    ITrainingCapacityGate capacity,
    ITrainingRuntimeService runtime,
    ITrainingProcessSpawner spawner,
    TrainingRunWorkspace workspace,
    TrainingRunCancellationRegistry cancellations,
    INodeDataDirectory dataDirectory,
    IOptions<TrainingRunQueueOptions> options,
    TimeProvider timeProvider,
    ILogger<TrainingRunExecutor> logger) : ITrainingRunExecutor
{
    /// <summary>The exit status <c>train.py</c> uses for a cooperative stop, so a cancel is never read as a failure.</summary>
    public const int CancelledExitCode = 3;

    /// <summary>
    ///     Stands in for the status of a trainer that had to be killed because it would not exit. The conventional
    ///     shell encoding of SIGKILL (128 + 9), named rather than left as a bare literal. It is never read:
    ///     <c>CompleteAsync</c> answers a set <see cref="StreamState.WatchdogReason" /> before it looks at the status.
    /// </summary>
    private const int KilledExitCode = 137;

    /// <summary>How often progress and the log tail are flushed. A trainer logs every step; the database does not need to.</summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(1);

    private readonly ITrainingCapacityGate _capacity = capacity ?? throw new ArgumentNullException(nameof(capacity));
    private readonly TrainingRunCancellationRegistry _cancellations = cancellations ?? throw new ArgumentNullException(nameof(cancellations));
    private readonly INodeDataDirectory _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    private readonly ITrainingOptionDefaultsCalculator _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
    private readonly ITrainingRunEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly ILogger<TrainingRunExecutor> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TrainingRunQueueOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly ITrainingRuntimeService _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    private readonly ITrainingProcessSpawner _spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
    private readonly ITrainingRunStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly TrainingRunWorkspace _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));

    public async Task ExecuteAsync(TrainingWorkClaim claim, CancellationToken stoppingToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.Run is not { } run)
        {
            // An evaluation target lives in a table this executor does not own; terminalize rather than strand it.
            _ = await _store.CompleteRunAsync(claim.TargetId, TrainingWorkStatus.Failed, "Unsupported work kind.", CancellationToken.None);
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var registration = _cancellations.Register(run.Id, cancellation);
        TrainingCapacityReservation? reservation = null;
        try
        {
            var runOptions = Read<TrainingRunOptionsV1>(run.OptionsJson) ?? new TrainingRunOptionsV1();
            var freeze = Read<TrainingRunFreezeV1>(run.FreezeJson);
            if (freeze is null)
            {
                await TerminalizeAsync(run.Id, TrainingWorkStatus.Failed, "The run's frozen dataset record could not be read.");
                return;
            }

            // Reserved HERE rather than inside the preparation below: assigning the handle from a returned value
            // would lose it if anything after the reservation threw, and the ledger would hold those bytes for the
            // lifetime of the process — starving every later spawn decision on the node.
            var estimate = await _defaults.EstimateAsync(run.BaseArtifactId, runOptions, stoppingToken);
            reservation = await _capacity.ReserveAsync(estimate, stoppingToken);
            if (!reservation.Granted)
            {
                await TerminalizeAsync(run.Id, TrainingWorkStatus.Failed, reservation.Reason);
                return;
            }

            await PrepareAndRunAsync(run, runOptions, freeze, cancellation, stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "The training run {RunId} failed before it could report its own outcome.", run.Id);
            await TerminalizeAsync(run.Id, TrainingWorkStatus.Failed, "The training run failed to start.");
        }
        finally
        {
            reservation?.Dispose();
            // The decrypted dataset is the one plaintext this feature puts on disk; it goes on EVERY terminal path,
            // failures included, not only on the happy one.
            _workspace.DeleteWorkDirectory(run.Id);
            _events.EvictPlaintext(run.Id);
        }
    }

    private async Task PrepareAndRunAsync(TrainingRunRecord run,
        TrainingRunOptionsV1 runOptions,
        TrainingRunFreezeV1 freeze,
        CancellationTokenSource cancellation,
        CancellationToken stoppingToken)
    {
        var version = await TransitionAsync(run.Id, run.Version, TrainingRunStatus.Preparing, stoppingToken);

        var interpreter = _runtime.ResolveInterpreterPath();
        if (interpreter is null || _runtime.GetStatus().Phase != TrainingRuntimePhase.Ready)
        {
            await TerminalizeAsync(run.Id, TrainingWorkStatus.Failed, "The Python training runtime is not installed.");
            return;
        }

        var datasetPath = await _workspace.MaterializeWorkCopyAsync(run.DatasetId, freeze.FreezeId, run.Id, stoppingToken);
        var jobPath = await WriteJobConfigAsync(run, runOptions, freeze, datasetPath, stoppingToken);

        _ = await TransitionAsync(run.Id, version, TrainingRunStatus.Training, stoppingToken);
        await RunTrainerAsync(run, interpreter, jobPath, cancellation, stoppingToken);
    }

    private async Task RunTrainerAsync(TrainingRunRecord run,
        string interpreter,
        string jobPath,
        CancellationTokenSource cancellation,
        CancellationToken stoppingToken)
    {
        var scriptPath = Path.Combine(TrainingScripts.ResolveDirectory(), TrainingScripts.TrainScriptName);
        if (!File.Exists(scriptPath))
        {
            await TerminalizeAsync(run.Id, TrainingWorkStatus.Failed, "The trainer script is missing from this installation.");
            return;
        }

        using var handle = _spawner.Spawn(new TrainingSpawnRequest(interpreter,
            [scriptPath, "--config", jobPath],
            _workspace.WorkDirectory(run.Id),
            Guid.NewGuid().ToString("N")));

        // Durable BEFORE the first line is read: a host that dies now must still be able to prove which process is
        // this run's and reap it on the next boot.
        await _store.SetLaunchReceiptAsync(run.Id, Serialize(ToPersisted(handle.Receipt)), CancellationToken.None);

        var state = new StreamState(_timeProvider.GetUtcNow());
        // The registration is disposed before the handle is: an operator cancel that lands after the stream has closed
        // would otherwise signal a process this method no longer owns.
        var stopOnCancel = cancellation.Token.Register(handle.RequestStop);
        // The watchdog sleeps a whole poll interval between looks, so "the stream ended" has to reach it as a signal
        // rather than as a flag it only notices on its next wake — otherwise every run, and every test, pays up to one
        // interval of dead wall clock at the end for a process that has already exited.
        using var watchdogStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task? watchdog = null;
        try
        {
            watchdog = WatchdogAsync(handle.KillGroup, state, watchdogStop.Token);
            await ConsumeAsync(run.Id, handle, state, stoppingToken);
        }
        finally
        {
            // The watchdog holds the handle, so it is joined here rather than on the happy path only: a throwing
            // consume must not leave it running against a process this method is about to dispose.
            state.Finished = true;
            await watchdogStop.CancelAsync();
            if (watchdog is not null)
            {
                await watchdog;
            }

            await stopOnCancel.DisposeAsync();
        }

        var exitCode = await WaitForExitOrEscalateAsync(handle, state);
        await FlushAsync(run.Id, state, force: true);
        await CompleteAsync(run.Id, state, exitCode, cancellation.IsCancellationRequested);
    }

    /// <summary>Consumes the trainer's merged stdout/stderr, folding protocol lines into state and the rest into the log tail.</summary>
    private async Task ConsumeAsync(Guid runId, ITrainingProcessHandle handle, StreamState state, CancellationToken stoppingToken)
    {
        await foreach (var line in handle.ReadOutputAsync(stoppingToken))
        {
            _ = state.Log.AppendLine(line);
            var parsed = TrainingRunStdioParser.TryParse(line);
            if (parsed is null)
            {
                // Banner or warning text. Kept in the log tail, but deliberately NOT treated as liveness: a library
                // that spams warnings while the trainer is wedged would otherwise hold the watchdog off forever.
                await FlushAsync(runId, state, force: false);
                continue;
            }

            state.LastEventAt = _timeProvider.GetUtcNow();
            Apply(runId, state, parsed);
            await FlushAsync(runId, state, force: parsed.Kind is TrainingStdioEventKind.Phase or TrainingStdioEventKind.Error);
            if (parsed.Kind == TrainingStdioEventKind.Artifact)
            {
                await RecordArtifactAsync(runId, parsed);
            }
        }
    }

    private void Apply(Guid runId, StreamState state, TrainingStdioEvent parsed)
    {
        switch (parsed.Kind)
        {
            case TrainingStdioEventKind.Handshake:
                state.ContractVersion = parsed.ContractVersion;
                break;
            case TrainingStdioEventKind.Phase:
                state.Progress = state.Progress with
                {
                    Phase = parsed.Phase ?? string.Empty
                };
                _ = _events.Append(runId, TrainingRunEventKind.Phase, new TrainingRunPayload(Phase: parsed.Phase));
                break;
            case TrainingStdioEventKind.Progress:
                state.Progress = state.Progress with
                {
                    Step = parsed.Step ?? state.Progress.Step,
                    TotalSteps = parsed.TotalSteps ?? state.Progress.TotalSteps,
                    Epoch = parsed.Epoch ?? state.Progress.Epoch,
                    Loss = parsed.Loss ?? state.Progress.Loss,
                    LearningRate = parsed.LearningRate ?? state.Progress.LearningRate,
                    VramBytes = parsed.VramBytes ?? state.Progress.VramBytes
                };
                _ = _events.Append(runId,
                    TrainingRunEventKind.Progress,
                    new TrainingRunPayload(Step: state.Progress.Step,
                        TotalSteps: state.Progress.TotalSteps,
                        Epoch: state.Progress.Epoch,
                        Loss: state.Progress.Loss,
                        LearningRate: state.Progress.LearningRate,
                        VramBytes: state.Progress.VramBytes));
                break;
            case TrainingStdioEventKind.Done:
                state.Done = true;
                state.Cancelled |= parsed.Cancelled;
                break;
            case TrainingStdioEventKind.Error:
                state.ErrorMessage = parsed.Message ?? parsed.Category;
                _ = _events.Append(runId, TrainingRunEventKind.Error, new TrainingRunPayload(Message: state.ErrorMessage));
                break;
            case TrainingStdioEventKind.Artifact:
            case TrainingStdioEventKind.Heartbeat:
            default:
                // A heartbeat's only job is to have arrived; the artifact is handled outside the state fold.
                break;
        }
    }

    /// <summary>
    ///     How often the watchdog looks. Capped at a second for a production-length bound, but scaled down with the
    ///     bound itself so a short one is not rounded up to the poll interval.
    /// </summary>
    private static TimeSpan WatchdogInterval(TimeSpan inactivityTimeout) =>
        inactivityTimeout < TimeSpan.FromSeconds(4) ? inactivityTimeout / 4 : TimeSpan.FromSeconds(1);

    /// <summary>
    ///     Waits for the child to reap itself once its output has closed, bounded.
    ///     <para>
    ///         A closed stream means the trainer should be exiting right now, and normally it already has. One that is
    ///         wedged after its last write would otherwise hold this method — and with it the run's
    ///         <c>TrainingCapacityReservation</c> — forever, starving every later spawn decision on the node: exactly
    ///         the failure the reservation comment in <c>ExecuteAsync</c> exists to prevent.
    ///     </para>
    ///     <para>
    ///         The bound is <see cref="TrainingRunQueueOptions.ExitGracePeriod" />, which exists for this and nothing
    ///         else: it is a teardown allowance, not a silence tolerance, so it is short where the inactivity window
    ///         is long. On expiry the escalation is the watchdog's own — SIGKILL to the group, and the run recorded
    ///         through <see cref="StreamState.WatchdogReason" /> — so the outcome is honest rather than a success that
    ///         was really a kill. The status is not waited for a second time: a SIGKILL that has not already settled
    ///         the process will not settle on this thread, and the status of a killed process is not information this
    ///         method uses.
    ///     </para>
    /// </summary>
    private async Task<int> WaitForExitOrEscalateAsync(ITrainingProcessHandle handle, StreamState state)
    {
        using var exitGrace = new CancellationTokenSource(_options.ExitGracePeriod, _timeProvider);
        try
        {
            return await handle.WaitForExitAsync(exitGrace.Token);
        }
        catch (OperationCanceledException)
        {
            handle.KillGroup();
            state.WatchdogReason ??= "The trainer stopped responding after closing its output and was terminated.";
            return KilledExitCode;
        }
    }

    /// <summary>Takes the kill callback rather than the handle: the watchdog's only power over the child is to end it.</summary>
    private async Task WatchdogAsync(Action killGroup, StreamState state, CancellationToken stoppingToken)
    {
        while (!state.Finished && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchdogInterval(_options.InactivityTimeout), _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // The flag is re-read HERE, not only in the while condition above: the delay can complete in the same
            // instant the stream closes, and a body that judged silence on the way out would kill a process that had
            // already finished — recording a successful run as Failed for a stillness that is simply the end of its
            // output. The cancellation above closes the whole poll interval; this closes the instant it cannot.
            if (state.Finished)
            {
                return;
            }

            var now = _timeProvider.GetUtcNow();
            if (now - state.LastEventAt > _options.InactivityTimeout)
            {
                state.WatchdogReason = "The trainer stopped reporting progress and was terminated.";
            }
            else if (now - state.StartedAt > _options.MaxRunDuration)
            {
                state.WatchdogReason = "The training run exceeded its maximum duration and was terminated.";
            }

            if (state.WatchdogReason is not null)
            {
                // Escalating to SIGKILL is the point: a cooperative stop is exactly what a wedged trainer cannot do.
                killGroup();
                return;
            }
        }
    }

    private async Task RecordArtifactAsync(Guid runId, TrainingStdioEvent parsed)
    {
        if (parsed.Path is not { Length: > 0 } path)
        {
            return;
        }

        // The only kind a training run can produce. GGUF artifacts arrive with the export step.
        const TrainingArtifactKind kind = TrainingArtifactKind.HfAdapterDir;
        var staged = _workspace.StagedDirectory(runId);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(staged) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(full, Path.GetFullPath(staged), StringComparison.Ordinal))
        {
            // The trainer names its own output path; anything outside the run's staged directory is refused rather
            // than recorded, so a compromised or buggy script cannot register a registry candidate from anywhere.
            _logger.LogWarning("The training run {RunId} reported an artifact outside its staged directory; it was ignored.", runId);
            return;
        }

        _ = await _store.CreateArtifactAsync(new TrainingArtifactInput(runId, kind, full), CancellationToken.None);
        _ = _events.Append(runId, TrainingRunEventKind.Artifact, new TrainingRunPayload(Message: kind.ToString()));
    }

    private async Task FlushAsync(Guid runId, StreamState state, bool force)
    {
        var now = _timeProvider.GetUtcNow();
        if (!force && now - state.LastPersistAt < PersistInterval)
        {
            return;
        }

        state.LastPersistAt = now;
        if (state.Log.Length > 0)
        {
            var chunk = state.Log.ToString();
            _ = state.Log.Clear();
            await _store.AppendLogTailAsync(runId, chunk, CancellationToken.None);
        }

        await _store.UpdateProgressAsync(runId,
                        Serialize(state.Progress with
                        {
                            UpdatedAtUtc = now.ToUnixTimeMilliseconds()
                        }),
                        CancellationToken.None);
    }

    private async Task CompleteAsync(Guid runId, StreamState state, int exitCode, bool cancelRequested)
    {
        var cancelled = state.Cancelled || exitCode == CancelledExitCode || (cancelRequested && state.WatchdogReason is null);
        var (status, message) = state switch
        {
            { WatchdogReason: { } reason } => (TrainingWorkStatus.Failed, reason),
            _ when cancelled => (TrainingWorkStatus.Cancelled, "The training run was cancelled."),
            { Done: true, ErrorMessage: null } when exitCode == 0 => (TrainingWorkStatus.Succeeded, (string?)null),
            { ErrorMessage: { } error } when !string.IsNullOrWhiteSpace(error) => (TrainingWorkStatus.Failed, error),
            _ => (TrainingWorkStatus.Failed, $"The trainer exited with status {exitCode}.")
        };

        await TerminalizeAsync(runId, status, message);
    }

    private async Task TerminalizeAsync(Guid runId, TrainingWorkStatus status, string? message)
    {
        var run = await _store.CompleteRunAsync(runId, status, message, CancellationToken.None);
        _ = _events.Append(runId,
            TrainingRunEventKind.State,
            new TrainingRunPayload(State: run.Status.ToString(), Message: message, RunVersion: run.Version));
    }

    private async Task<long> TransitionAsync(Guid runId, long expectedVersion, TrainingRunStatus status, CancellationToken cancellationToken)
    {
        var run = await _store.TransitionAsync(runId, expectedVersion, status, cancellationToken);
        _ = _events.Append(runId, TrainingRunEventKind.State, new TrainingRunPayload(State: run.Status.ToString(), RunVersion: run.Version));
        return run.Version;
    }

    private async Task<string> WriteJobConfigAsync(TrainingRunRecord run,
        TrainingRunOptionsV1 options,
        TrainingRunFreezeV1 freeze,
        string datasetPath,
        CancellationToken cancellationToken)
    {
        var staged = _workspace.StagedDirectory(run.Id);
        TrainingRunWorkspace.CreateOwnerOnlyDirectory(staged);
        var job = new TrainingJobConfigV1
        {
            ContractVersion = TrainingRunStdioParser.ContractVersion,
            RunId = run.Id,
            BasePath = BaseArtifactManifest.ResolveDirectory(_dataDirectory, run.BaseArtifactId),
            DatasetPath = datasetPath,
            WorkDir = _workspace.WorkDirectory(run.Id),
            OutputDir = staged,
            HoldoutSequences = freeze.HoldoutSequences,
            Options = options
        };
        var path = _workspace.JobConfigPath(run.Id);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(job, TrainingJson.Options), cancellationToken);
        return path;
    }

    private static TrainingLaunchReceiptV1 ToPersisted(TrainingLaunchReceipt receipt) =>
        new()
        {
            Pid = receipt.Pid,
            Pgid = receipt.Pgid,
            ExecutablePath = receipt.ExecutablePath,
            StartTicks = receipt.StartTicks,
            RunToken = receipt.RunToken
        };

    private static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, TrainingJson.Options);

    private static T? Read<T>(ReadOnlyMemory<byte>? payload)
        where T : class
    {
        if (payload is not { } bytes || bytes.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(bytes.Span, TrainingJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Everything that survives across the stream loop. Mutable by design — it is one run's scratchpad.</summary>
    private sealed class StreamState(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset LastEventAt { get; set; } = startedAt;
        public DateTimeOffset LastPersistAt { get; set; } = startedAt;
        public StringBuilder Log { get; } = new();
        public TrainingRunProgressV1 Progress { get; set; } = new();
        public int? ContractVersion { get; set; }
        public bool Done { get; set; }
        public bool Cancelled { get; set; }
        public bool Finished { get; set; }
        public string? ErrorMessage { get; set; }
        public string? WatchdogReason { get; set; }
    }
}

/// <summary>The <c>job.json</c> handed to <c>train.py</c> — the whole input contract, in one file.</summary>
public sealed record TrainingJobConfigV1
{
    public int ContractVersion { get; init; }

    public Guid RunId { get; init; }

    public string BasePath { get; init; } = string.Empty;

    public string DatasetPath { get; init; } = string.Empty;

    public string WorkDir { get; init; } = string.Empty;

    public string OutputDir { get; init; } = string.Empty;

    /// <summary>Canonical sequences the trainer must skip — the frozen holdout.</summary>
    public IReadOnlyList<int> HoldoutSequences { get; init; } = [];

    public TrainingRunOptionsV1 Options { get; init; } = new();
}

/// <summary>
///     Where the shipped Python scripts live. Duplicates <c>TrainingRuntimeLayout</c>'s resolution because that type is
///     internal to the provider assembly and this is the only fact the application layer needs from it.
/// </summary>
public static class TrainingScripts
{
    public const string TrainScriptName = "train.py";

    /// <summary>The adapter merge step. The GGUF conversion beside it runs llama.cpp's own scripts, not this one.</summary>
    public const string ExportScriptName = "export.py";

    private const string PublishedScriptsDirectoryName = "training-scripts";
    private const string RepositoryScriptsRelativePath = "tools/training";

    public static string ResolveDirectory()
    {
        var published = Path.Combine(AppContext.BaseDirectory, PublishedScriptsDirectoryName);
        if (File.Exists(Path.Combine(published, TrainScriptName)))
        {
            return published;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RepositoryScriptsRelativePath);
            if (File.Exists(Path.Combine(candidate, TrainScriptName)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return published;
    }
}
