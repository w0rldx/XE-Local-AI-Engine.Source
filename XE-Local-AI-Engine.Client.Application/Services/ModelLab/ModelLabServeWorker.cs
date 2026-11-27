namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Text.Json;
using System.Text.Json.Serialization;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>One <c>modellab.py --serve</c> process: its single output pump, the contract-6 frame order, and its jobs one at a time.</summary>
/// <remarks>
///     A frame counts only with this spawn's run token; any other line is output for the diagnostic tail. A frame out of the legal order
///     breaks the worker: its group is killed at once, it never gets another job, and <see cref="OnBroken" /> tells its owner.
/// </remarks>
internal sealed class ModelLabServeWorker : IDisposable
{
    private const string ExitedReason = "The Model Lab worker exited.";
    private const string ExitedAfterJobReason = "The Model Lab worker exited after its job before reporting ready.";

    private readonly ITrainingProcessHandle _handle;
    private readonly TaskCompletionSource<string?> _handshake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ModelLabScriptStreamState _idleOutput = new();
    private readonly ModelLabJobOptions _options;
    private readonly CancellationTokenSource _pumpStop = new();
    private readonly Lock _sync = new();
    private readonly TimeProvider _timeProvider;
    private string? _brokenReason;
    private Expect _expect = Expect.Handshake;
    private ServeJob? _job;
    private bool _outputClosed;
    private Task _pump = Task.CompletedTask;
    private long? _vramBytes;

    private ModelLabServeWorker(ITrainingProcessHandle handle, string runToken, string workingDirectory, ModelLabJobOptions options, TimeProvider timeProvider)
    {
        _handle = handle;
        _options = options;
        _timeProvider = timeProvider;
        RunToken = runToken;
        WorkingDirectory = workingDirectory;
    }

    private enum Expect
    {
        Handshake,
        Run,
        JobFrames,
        Ready,
        Broken
    }

    /// <summary>The token every frame of this spawn carries, and the name of its receipt file.</summary>
    public string RunToken { get; }

    /// <summary>This spawn's own scratch directory (its HOME, TMPDIR and HF_HOME); deleted only once its exit is seen.</summary>
    public string WorkingDirectory { get; }

    public TrainingLaunchReceipt Receipt => _handle.Receipt;

    /// <summary>Called once, outside every lock, when the worker can no longer take a job: a protocol break, a kill or its exit.</summary>
    public Action? OnBroken { get; set; }

    /// <summary>The GPU memory the model's tensors held at the last ready (allocated, not reserved); <see langword="null" /> before the first.</summary>
    public long? VramBytes
    {
        get
        {
            lock (_sync)
            {
                return _vramBytes;
            }
        }
    }

    /// <param name="workRoot">The Model Lab work root; the spawn gets a directory of its own under it, so no file it leaves reaches a later worker.</param>
    public static ModelLabServeWorker Spawn(ITrainingProcessSpawner spawner,
        ModelLabScriptCommand script,
        string workRoot,
        ModelLabJobOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(spawner);
        ArgumentNullException.ThrowIfNull(script);
        var token = Guid.NewGuid().ToString("N");
        var workingDirectory = Path.Combine(workRoot, "worker-" + token);
        TrainingRunWorkspace.CreateOwnerOnlyDirectory(workingDirectory);
        ITrainingProcessHandle handle;
        try
        {
#pragma warning disable CA2000 // The worker owns the handle and disposes it.
            handle = spawner.Spawn(new TrainingSpawnRequest
            {
                ExecutablePath = script.Interpreter,
                Arguments = [script.ScriptPath, "--serve"],
                WorkingDirectory = workingDirectory,
                RunToken = token,
                RedirectStandardInput = true
            });
#pragma warning restore CA2000
        }
        catch
        {
            // No process started, so nothing can still be writing there.
            BestEffortFile.TryDeleteDirectory(workingDirectory);
            throw;
        }

        return new ModelLabServeWorker(handle, token, workingDirectory, options, timeProvider);
    }

    /// <summary>Starts reading the output. Called once the receipt is durable, so no line is read before a dead host could find the process.</summary>
    public void StartPump()
    {
        var stop = _pumpStop.Token;
        _pump = Task.Run(() => PumpAsync(stop), CancellationToken.None);
    }

    /// <summary>Waits for the handshake; <see langword="null" /> once it came, otherwise what the job ends with. A cancel kills the worker.</summary>
    public async Task<ModelLabScriptRun?> WaitForHandshakeAsync(CancellationToken ct)
    {
        string? failure;
        try
        {
            failure = await _handshake.Task.WaitAsync(_options.HandshakeTimeout, _timeProvider, ct);
        }
        catch (TimeoutException)
        {
            failure = "The Model Lab worker did not start within its bound and was terminated.";
            Break(failure);
        }
        catch (OperationCanceledException)
        {
            Break(reason: null);
            return new ModelLabScriptRun
            {
                State = _idleOutput,
                FailureReason = null,
                ExitCode = -1
            };
        }

        if (failure is null)
        {
            return null;
        }

        // A worker that exited before its handshake failed the way a one-shot script does: by its exit status and its last lines.
        var exited = string.Equals(failure, ExitedReason, StringComparison.Ordinal);
        return new ModelLabScriptRun
        {
            State = _idleOutput,
            FailureReason = exited ? null : failure,
            ExitCode = exited ? await ExitCodeOrKilledAsync() : -1
        };
    }

    /// <summary>Runs one job to its terminal frame and the worker's ready, under a watchdog of its own that is retired before this returns.</summary>
    /// <param name="jobId">The experiment, which its frames carry as <c>jobId</c>.</param>
    /// <param name="configPath">The job file.</param>
    /// <param name="onEvent">Gets the job's phase, progress and heartbeat frames, on the pump, outside every lock.</param>
    /// <param name="cancel">Sends the cooperative cancel; past its acknowledgement bound the group is killed.</param>
    public async Task<ModelLabServeJobOutcome> RunJobAsync(Guid jobId, string configPath, Action<TrainingStdioEvent> onEvent, CancellationToken cancel)
    {
        var watchdog = ModelLabScriptRunner.NewWatchdog(_options, _options.CancelAcknowledgementTimeout, _timeProvider);
        var job = new ServeJob
        {
            JobId = jobId.ToString("D"),
            OnEvent = onEvent,
            Watchdog = watchdog
        };
        job.State.HandshakeSeen = true;
        lock (_sync)
        {
            if (_expect != Expect.Run)
            {
                return new ModelLabServeJobOutcome
                {
                    Run = new ModelLabScriptRun
                    {
                        State = job.State,
                        FailureReason = _brokenReason ?? "The Model Lab worker could not take the job.",
                        ExitCode = -1
                    },
                    Reusable = false
                };
            }

            _job = job;
            _expect = Expect.JobFrames;
        }

        using var watchdogStop = new CancellationTokenSource();
        var watching = watchdog.RunAsync(() => Break(reason: null), watchdogStop.Token);
        CancellationTokenRegistration stop = default;
        try
        {
            if (!await SendAsync(new ServeCommand
                {
                    Op = "run",
                    JobId = job.JobId,
                    Config = configPath
                }))
            {
                Break("The Model Lab worker did not take its job and was terminated.");
            }
            else
            {
                // Only after the run is written: the worker ignores a cancel for a job it has not been given, then runs it.
                stop = cancel.Register(() =>
                {
                    watchdog.MarkStopRequested();
                    // Off the canceller's thread: a cancel arrives under the service's job lock.
                    _ = Task.Run(() => SendAsync(new ServeCommand
                    {
                        Op = "cancel",
                        JobId = job.JobId
                    }), CancellationToken.None);
                });
            }

            await job.Terminal.Task;
        }
        finally
        {
            await stop.DisposeAsync();
            await watchdogStop.CancelAsync();
            await watching;
        }

        var exitCode = 0;
        if (job.TerminalSeen)
        {
            try
            {
                await job.Ready.Task.WaitAsync(_options.ReadyTimeout, _timeProvider, CancellationToken.None);
            }
            catch (TimeoutException)
            {
                Break("The Model Lab worker did not report ready after its job and was terminated.");
            }
        }
        else if (IsOutputClosed)
        {
            exitCode = await watchdog.WaitForExitOrEscalateAsync(_handle);
        }

        string? brokenReason;
        lock (_sync)
        {
            brokenReason = _brokenReason;
        }

        return new ModelLabServeJobOutcome
        {
            Run = new ModelLabScriptRun
            {
                State = job.State,
                // After its terminal frame, only a break that came before its ready is this job's: one after it is the idle worker's.
                FailureReason = watchdog.FailureReason ?? (job.TerminalSeen ? job.BrokenReason : brokenReason),
                ExitCode = exitCode
            },
            Reusable = job.ReadySeen && job.State.Done && !IsBroken
        };
    }

    /// <summary>Kills the group at once and marks the worker broken; for a failure the caller already knows the reason for.</summary>
    public void Kill() =>
        Break(reason: null);

    /// <summary>Evicts: asks the worker to exit, kills its group after the grace, and says whether the exit was seen within the verify bound.</summary>
    /// <remarks>The output pump is retired before this returns, so no line from a pipe a descendant still holds reaches a worker being torn down.</remarks>
    public async Task<bool> StopAsync()
    {
        if (!IsOutputClosed)
        {
            _ = await SendAsync(new ServeCommand
            {
                Op = "evict"
            });
        }

        var exited = await ExitedWithinAsync(_options.EvictGrace);
        if (!exited)
        {
            _handle.KillGroup();
            exited = await ExitedWithinAsync(_options.ExitVerifyTimeout);
        }

        await _pumpStop.CancelAsync();
        await _pump;
        return exited;
    }

    public void Dispose()
    {
        _handle.Dispose();
        _pumpStop.Dispose();
    }

    private bool IsOutputClosed
    {
        get
        {
            lock (_sync)
            {
                return _outputClosed;
            }
        }
    }

    private bool IsBroken
    {
        get
        {
            lock (_sync)
            {
                return _expect == Expect.Broken;
            }
        }
    }

    private async Task PumpAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var line in _handle.ReadOutputAsync(stop))
            {
                // A reader may keep yielding buffered lines after the cancel, so a stop is checked per line.
                if (stop.IsCancellationRequested)
                {
                    break;
                }

                Dispatch(line);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            // A torn-down pipe, or the pump retired by a stop, ends the output like a closed one.
        }

        ServeJob? job;
        bool wasBroken;
        lock (_sync)
        {
            _outputClosed = true;
            wasBroken = _expect == Expect.Broken;
            if (_expect == Expect.Ready && _job is { State.Done: true })
            {
                // Its done came, its ready never will: the job did not end cleanly. An error frame already fails it with its own reason.
                _job.BrokenReason ??= ExitedAfterJobReason;
            }

            _expect = Expect.Broken;
            job = _job;
        }

        _handshake.TrySetResult(ExitedReason);
        job?.Terminal.TrySetResult();
        job?.Ready.TrySetResult();
        if (!wasBroken)
        {
            OnBroken?.Invoke();
        }
    }

    private void Dispatch(string line)
    {
        var parsed = TrainingRunStdioParser.ParseOrNull(line);
        ServeJob? deliverTo = null;
        string? violation;
        lock (_sync)
        {
            if (parsed is null || !string.Equals(parsed.Token, RunToken, StringComparison.Ordinal))
            {
                (_job?.State ?? _idleOutput).KeepOutput(line);
                return;
            }

            if (_expect == Expect.Broken)
            {
                return;
            }

            violation = Advance(parsed, ref deliverTo);
        }

        if (violation is not null)
        {
            Break(violation);
            return;
        }

        if (deliverTo is not null)
        {
            deliverTo.Watchdog.MarkEvent();
            if (parsed.Kind is TrainingStdioEventKind.Phase or TrainingStdioEventKind.Progress or TrainingStdioEventKind.Heartbeat)
            {
                deliverTo.OnEvent(parsed);
            }
        }
    }

    /// <summary>Moves the frame order on by one frame, under the lock; the violation's reason, or <see langword="null" /> for a legal frame.</summary>
    private string? Advance(TrainingStdioEvent parsed, ref ServeJob? deliverTo)
    {
        var current = _expect == Expect.JobFrames && _job is not null && string.Equals(parsed.JobId, _job.JobId, StringComparison.Ordinal) ? _job : null;
        switch (parsed.Kind)
        {
            case TrainingStdioEventKind.Handshake when _expect == Expect.Handshake:
                if (parsed.ContractVersion != ModelLabTraceSpec.ContractVersion)
                {
                    return "The Model Lab script's protocol version does not match this build.";
                }

                if (parsed.Serve != true)
                {
                    return "The Model Lab worker's handshake did not declare serve mode and was terminated.";
                }

                _expect = Expect.Run;
                _handshake.TrySetResult(null);
                return null;
            case TrainingStdioEventKind.Phase or TrainingStdioEventKind.Progress or TrainingStdioEventKind.Heartbeat when current is not null:
                deliverTo = current;
                return null;
            case TrainingStdioEventKind.Done or TrainingStdioEventKind.Error when current is not null:
                if (parsed.Kind == TrainingStdioEventKind.Done)
                {
                    current.State.Done = true;
                    current.State.ScriptCancelled |= parsed.Cancelled;
                }
                else
                {
                    current.State.ErrorMessage = parsed.Message ?? parsed.Category;
                    current.State.ErrorCategory = parsed.Category;
                }

                current.TerminalSeen = true;
                _expect = Expect.Ready;
                deliverTo = current;
                current.Terminal.TrySetResult();
                return null;
            case TrainingStdioEventKind.Error when parsed.JobId is null:
                // A process-level error: the worker exits 1 after it.
                if (_job is not null)
                {
                    _job.State.ErrorMessage ??= parsed.Message ?? parsed.Category;
                    _job.State.ErrorCategory ??= parsed.Category;
                }

                return $"The Model Lab worker failed ({parsed.Category}) and was terminated.";
            case TrainingStdioEventKind.Ready when _expect == Expect.Ready && _job is not null:
                // A ready is the process's, never a job's, and always says what VRAM the cleanup left.
                if (parsed.JobId is not null || parsed.VramBytes is not >= 0)
                {
                    return "The Model Lab worker broke its protocol with a malformed ready frame and was terminated.";
                }

                _vramBytes = parsed.VramBytes;
                _job.ReadySeen = true;
                _job.Ready.TrySetResult();
                _job = null;
                _expect = Expect.Run;
                return null;
            default:
                return $"The Model Lab worker broke its protocol with a {parsed.Kind} frame out of order and was terminated.";
        }
    }

    /// <summary>Marks the worker broken and kills its group, once. The job waiting on it wakes; the owner hears of it outside the lock.</summary>
    private void Break(string? reason)
    {
        ServeJob? job;
        lock (_sync)
        {
            _brokenReason ??= reason;
            // The job is cleared at its ready, so a break after that never lands on it.
            if (_job is { } running)
            {
                running.BrokenReason ??= reason;
            }

            if (_expect == Expect.Broken)
            {
                return;
            }

            _expect = Expect.Broken;
            job = _job;
        }

        _handshake.TrySetResult(reason ?? "The Model Lab worker was terminated.");
        job?.Terminal.TrySetResult();
        job?.Ready.TrySetResult();
        _handle.KillGroup();
        OnBroken?.Invoke();
    }

    private async Task<bool> SendAsync(ServeCommand command)
    {
        using var bound = new CancellationTokenSource(_options.CommandWriteTimeout, _timeProvider);
        try
        {
            await _handle.WriteLineAsync(JsonSerializer.Serialize(command), bound.Token);
            return true;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The exit counts once the worker and every process in its group are gone; an output pipe a descendant holds does not keep it waiting.</summary>
    private async Task<bool> ExitedWithinAsync(TimeSpan bound)
    {
        using var timeout = new CancellationTokenSource(bound, _timeProvider);
        return await _handle.WaitForGroupExitAsync(timeout.Token) is not null;
    }

    private async Task<int> ExitCodeOrKilledAsync()
    {
        using var timeout = new CancellationTokenSource(_options.CancelAcknowledgementTimeout, _timeProvider);
        if (await _handle.WaitForGroupExitAsync(timeout.Token) is { } exitCode)
        {
            return exitCode;
        }

        _handle.KillGroup();
        return -1;
    }

    private sealed class ServeJob
    {
        public required string JobId { get; init; }

        public required Action<TrainingStdioEvent> OnEvent { get; init; }

        public required TrainingScriptWatchdog Watchdog { get; init; }

        public ModelLabScriptStreamState State { get; } = new();

        /// <summary>The terminal frame, or the worker breaking or exiting first.</summary>
        public TaskCompletionSource Terminal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Written under the worker's lock, read once <see cref="Terminal" /> has completed.</summary>
        public bool TerminalSeen { get; set; }

        /// <summary>Written under the worker's lock, read once <see cref="Ready" /> has completed.</summary>
        public bool ReadySeen { get; set; }

        /// <summary>Why the worker broke while this job held it, before its ready. Written under the worker's lock.</summary>
        public string? BrokenReason { get; set; }
    }

    private sealed class ServeCommand
    {
        [JsonPropertyName("op")]
        public required string Op { get; init; }

        [JsonPropertyName("jobId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? JobId { get; init; }

        [JsonPropertyName("config")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Config { get; init; }
    }
}
