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

        using var handle = _spawner.Spawn(new TrainingSpawnRequest
        {
            ExecutablePath = script.Interpreter,
            Arguments = [script.ScriptPath, "--config", jobPath],
            WorkingDirectory = workDirectory,
            RunToken = Guid.NewGuid().ToString("N")
        });

        // Durable before the first line is read: a host that dies now must still be able to prove which process is this job's.
        await saveReceipt(JsonSerializer.SerializeToUtf8Bytes(TrainingProcessReaper.ToPersisted(handle.Receipt), TrainingJson.Options));

        var state = new StreamState();
        var watchdog = new TrainingScriptWatchdog(_options.InactivityTimeout,
            _options.MaxDuration,
            _options.ExitGracePeriod,
            _timeProvider,
            silentReason: "The Model Lab script stopped reporting and was terminated.",
            overrunReason: "The Model Lab script exceeded its maximum duration and was terminated.",
            noExitReason: "The Model Lab script stopped responding after closing its output and was terminated.",
            killedExitCode: -1);
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
                if (TrainingRunStdioParser.ParseOrNull(line) is { } parsed)
                {
                    watchdog.MarkEvent();
                    Apply(id, state, watchdog, parsed, handle.KillGroup);
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

    private void Apply(Guid id, StreamState state, TrainingScriptWatchdog watchdog, TrainingStdioEvent parsed, Action killGroup)
    {
        switch (parsed.Kind)
        {
            case TrainingStdioEventKind.Handshake:
                state.HandshakeSeen = true;
                if (parsed.ContractVersion != ModelLabTraceSpec.ContractVersion)
                {
                    watchdog.Fail("The Model Lab script's protocol version does not match this build.");
                    killGroup();
                }

                break;
            case TrainingStdioEventKind.Phase:
                _ = _events.Append(id, TrainingRunEventKind.Phase, new TrainingRunPayload
                {
                    Phase = parsed.Phase
                });
                break;
            case TrainingStdioEventKind.Progress:
                _ = _events.Append(id, TrainingRunEventKind.Progress, new TrainingRunPayload
                {
                    Step = parsed.Step,
                    TotalSteps = parsed.TotalSteps,
                    VramBytes = parsed.VramBytes
                });
                break;
            case TrainingStdioEventKind.Done:
                state.Done = true;
                state.ScriptCancelled |= parsed.Cancelled;
                break;
            case TrainingStdioEventKind.Error:
                state.ErrorMessage = parsed.Message ?? parsed.Category;
                break;
            case TrainingStdioEventKind.Heartbeat:
            case TrainingStdioEventKind.Artifact:
            default:
                break;
        }
    }

    internal sealed class ScriptCommand
    {
        public required string Interpreter { get; init; }

        public required string ScriptPath { get; init; }
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
                return ModelLabExperimentService.Cancelled();
            }

            if (FailureReason is { } reason)
            {
                return ModelLabExperimentService.Finished(ModelLabExperimentStatus.Failed, reason);
            }

            if (State.ErrorMessage is { Length: > 0 } error)
            {
                return ModelLabExperimentService.Finished(ModelLabExperimentStatus.Failed, State.WithLastOutput(error));
            }

            if (!State.HandshakeSeen || !State.Done || ExitCode != 0)
            {
                return ModelLabExperimentService.Finished(ModelLabExperimentStatus.Failed, State.WithLastOutput($"The Model Lab script exited with status {ExitCode}."));
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
