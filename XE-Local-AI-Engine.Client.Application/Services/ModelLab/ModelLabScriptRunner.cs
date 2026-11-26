namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>Spawns one Model Lab script and follows its output to its end, under the shared watchdog.</summary>
internal sealed class ModelLabScriptRunner
{
    private const string JobFileName = "job.json";

    private readonly ITrainingRunEventBuffer _events;
    private readonly ModelLabJobOptions _options;
    private readonly ITrainingProcessSpawner _spawner;
    private readonly TimeProvider _timeProvider;

    public ModelLabScriptRunner(ITrainingProcessSpawner spawner, ITrainingRunEventBuffer events, ModelLabJobOptions options, TimeProvider timeProvider)
    {
        _events = events;
        _options = options;
        _spawner = spawner;
        _timeProvider = timeProvider;
    }

    /// <summary>Where every Model Lab job and serving worker gets its own directory; the startup sweep deletes it whole.</summary>
    public static string WorkRoot(INodeDataDirectory dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataDirectory);
        return Path.Combine(dataDirectory.Root, "model-lab", "work");
    }

    /// <summary>The watchdog over one script or one serving worker's job; <paramref name="exitGrace" /> is the bound after a stop or a closed output.</summary>
    public static TrainingScriptWatchdog NewWatchdog(ModelLabJobOptions options, TimeSpan exitGrace, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new TrainingScriptWatchdog(options.InactivityTimeout,
            options.MaxDuration,
            exitGrace,
            timeProvider,
            silentReason: "The Model Lab script stopped reporting and was terminated.",
            overrunReason: "The Model Lab script exceeded its maximum duration and was terminated.",
            noExitReason: "The Model Lab script stopped responding after closing its output and was terminated.",
            killedExitCode: -1);
    }

    /// <summary>Republishes a phase or progress frame as the experiment's event; every other frame publishes nothing.</summary>
    public static void PublishFrame(ITrainingRunEventBuffer events, Guid id, TrainingStdioEvent parsed)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(parsed);
        switch (parsed.Kind)
        {
            case TrainingStdioEventKind.Phase:
                _ = events.Append(id, TrainingRunEventKind.Phase, new TrainingRunPayload
                {
                    Phase = parsed.Phase
                });
                break;
            case TrainingStdioEventKind.Progress:
                _ = events.Append(id, TrainingRunEventKind.Progress, new TrainingRunPayload
                {
                    Step = parsed.Step,
                    TotalSteps = parsed.TotalSteps,
                    VramBytes = parsed.VramBytes
                });
                break;
            default:
                break;
        }
    }

    /// <param name="id">The experiment, which is also the id its events carry.</param>
    /// <param name="workDirectory">The job's owner-only directory, created here.</param>
    /// <param name="jobJson">The job file's content, which holds plaintext prompts.</param>
    /// <param name="script">What to spawn.</param>
    /// <param name="saveReceipt">Makes the launch receipt durable, before any output is read.</param>
    /// <param name="stopToken">Asks for the cooperative stop, which the watchdog then bounds.</param>
    public async Task<ScriptRun> RunAsync(Guid id,
        string workDirectory,
        byte[] jobJson,
        ScriptCommand script,
        Func<ReadOnlyMemory<byte>, Task> saveReceipt,
        CancellationToken stopToken)
    {
        TrainingRunWorkspace.CreateOwnerOnlyDirectory(Path.GetDirectoryName(workDirectory)!);
        TrainingRunWorkspace.CreateOwnerOnlyDirectory(workDirectory);
        var jobPath = Path.Combine(workDirectory, JobFileName);
        // Owner-only by itself: the directory's own mode is best-effort, and this file holds the plaintext prompt.
        await SecureFilePermissions.WriteAllBytesAtomicAsync(jobPath, jobJson, CancellationToken.None);

        var runToken = Guid.NewGuid().ToString("N");
        using var handle = _spawner.Spawn(new TrainingSpawnRequest
        {
            ExecutablePath = script.Interpreter,
            Arguments = [script.ScriptPath, "--config", jobPath],
            WorkingDirectory = workDirectory,
            RunToken = runToken
        });

        // Durable before the first line is read: a host that dies now must still be able to prove which process is this job's.
        await saveReceipt(JsonSerializer.SerializeToUtf8Bytes(TrainingProcessReaper.ToPersisted(handle.Receipt), TrainingJson.Options));

        var state = new StreamState();
        var watchdog = NewWatchdog(_options, _options.ExitGracePeriod, _timeProvider);
        var stop = stopToken.Register(() =>
        {
            watchdog.MarkStopRequested();
            handle.RequestStop();
        });
        using var watchdogStop = new CancellationTokenSource();
        var watching = watchdog.RunAsync(handle.KillGroup, watchdogStop.Token);
        try
        {
            await foreach (var line in handle.ReadOutputAsync(CancellationToken.None))
            {
                // A frame counts only with this spawn's token: any other line, a foreign frame too, is output for the diagnostic tail.
                if (TrainingRunStdioParser.ParseOrNull(line) is { } parsed && string.Equals(parsed.Token, runToken, StringComparison.Ordinal))
                {
                    watchdog.MarkEvent();
                    Apply(id, script, state, watchdog, parsed, handle.KillGroup);
                }
                else
                {
                    state.KeepOutput(line);
                }
            }
        }
        finally
        {
            await watchdogStop.CancelAsync();
            await watching;
            await stop.DisposeAsync();
        }

        var exitCode = await watchdog.WaitForExitOrEscalateAsync(handle);
        return new ScriptRun
        {
            State = state,
            FailureReason = watchdog.FailureReason,
            ExitCode = exitCode
        };
    }

    private void Apply(Guid id, ScriptCommand script, StreamState state, TrainingScriptWatchdog watchdog, TrainingStdioEvent parsed, Action killGroup)
    {
        switch (parsed.Kind)
        {
            case TrainingStdioEventKind.Handshake:
                state.HandshakeSeen = true;
                if (parsed.ContractVersion != script.ContractVersion)
                {
                    watchdog.Fail("The Model Lab script's protocol version does not match this build.");
                    killGroup();
                }

                break;
            case TrainingStdioEventKind.Phase:
            case TrainingStdioEventKind.Progress:
                if (script.PublishPhases)
                {
                    PublishFrame(_events, id, parsed);
                }

                break;
            case TrainingStdioEventKind.Done:
                state.Done = true;
                state.ScriptCancelled |= parsed.Cancelled;
                break;
            case TrainingStdioEventKind.Error:
                state.ErrorMessage = parsed.Message ?? parsed.Category;
                state.ErrorCategory = parsed.Category;
                break;
            case TrainingStdioEventKind.Heartbeat:
            case TrainingStdioEventKind.Artifact:
            case TrainingStdioEventKind.Ready:
            default:
                break;
        }
    }

    internal sealed class ScriptCommand
    {
        public required string Interpreter { get; init; }

        public required string ScriptPath { get; init; }

        /// <summary>The handshake version this script must speak.</summary>
        public int ContractVersion { get; init; } = ModelLabTraceSpec.ContractVersion;

        /// <summary>False keeps phase and progress frames out of the event buffer entirely: they still count as liveness.</summary>
        public bool PublishPhases { get; init; } = true;
    }

    internal sealed class ScriptRun
    {
        public required StreamState State { get; init; }

        public required string? FailureReason { get; init; }

        public required int ExitCode { get; init; }

        /// <summary>What the job ends with because of the script, or <see langword="null" /> when the script did its work.</summary>
        public ModelLabExperimentCompletion? FailureOrNull(bool cancelRequested)
        {
            if (cancelRequested || State.ScriptCancelled || ExitCode == TrainingRunExecutor.CancelledExitCode)
            {
                return ModelLabExperimentCompletion.Cancelled();
            }

            if (FailureReason is { } reason)
            {
                return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, reason);
            }

            if (State.ErrorMessage is { Length: > 0 } error)
            {
                return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, State.WithLastOutput(error));
            }

            if (!State.HandshakeSeen || !State.Done || ExitCode != 0)
            {
                return ModelLabExperimentCompletion.Finished(ModelLabExperimentStatus.Failed, State.WithLastOutput($"The Model Lab script exited with status {ExitCode}."));
            }

            return null;
        }
    }

    internal sealed class StreamState
    {
        private const int OutputLinesKept = 12;
        private const int OutputLineLength = 300;

        // A native tool's own explanation reaches only stderr: a failed compile says why here and nowhere else.
        private readonly Queue<string> _lastOutput = new();

        public bool HandshakeSeen { get; set; }

        public bool Done { get; set; }

        public bool ScriptCancelled { get; set; }

        public string? ErrorMessage { get; set; }

        /// <summary>The error event's <c>category</c>: the stable code a source job's refusal is mapped from.</summary>
        public string? ErrorCategory { get; set; }

        public void KeepOutput(string line)
        {
            _lastOutput.Enqueue(line.Length > OutputLineLength ? line[..OutputLineLength] : line);
            if (_lastOutput.Count > OutputLinesKept)
            {
                _ = _lastOutput.Dequeue();
            }
        }

        public string WithLastOutput(string message) =>
            _lastOutput.Count == 0 ? message : $"{message}\n\nLast output:\n{string.Join('\n', _lastOutput)}";
    }
}
