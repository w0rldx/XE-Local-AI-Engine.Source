namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using static ModelLabExperimentServiceTests;

/// <summary>One <c>modellab.py --config</c> spawn over a scripted process: only frames carrying this spawn's token count.</summary>
[Category(TestCategories.Unit)]
public sealed class ModelLabScriptRunnerTests
{
    private const string ForeignToken = "not-this-spawn";

    private static readonly ModelLabScriptRunner.ScriptCommand Script = new()
    {
        Interpreter = "python",
        ScriptPath = "modellab.py"
    };

    [Test]
    public async Task FramesWithThisSpawnsToken_AreApplied_AndPublished()
    {
        using var process = new ScriptedProcess();
        using var work = new TempDirectory("modellab-runner");
        var events = NewEvents();
        var id = Guid.NewGuid();

        var running = Runner(process, events, TimeProvider.System, new ModelLabJobOptions())
            .RunAsync(id, Path.Combine(work.Path, "job"), JobJson(work.Path), Script, static _ => Task.CompletedTask, CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        process.Finish([.. CompleteRun.Select(line => Stamped(line, process.Request!.RunToken))], exitCode: 0);
        var run = await running.WaitAsync(TestBudgets.Contended);

        AssertEx.Null(run.FailureOrNull(cancelRequested: false), run.State.WithLastOutput("The stamped run should succeed."));
        AssertEx.Equal("Phase,Progress", string.Join(',', events.Replay(id, afterSequence: 0).Events.Select(static e => e.Kind)));
    }

    /// <summary>Another spawn's frames, even a done or a mismatched handshake, change nothing and only reach the diagnostic tail.</summary>
    [Test]
    public async Task AForeignFrame_ChangesNoState_AndPublishesNothing()
    {
        using var process = new ScriptedProcess();
        using var work = new TempDirectory("modellab-runner");
        var events = NewEvents();
        var id = Guid.NewGuid();

        var running = Runner(process, events, TimeProvider.System, new ModelLabJobOptions())
            .RunAsync(id, Path.Combine(work.Path, "job"), JobJson(work.Path), Script, static _ => Task.CompletedTask, CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        process.Finish([
            Stamped(Handshake, process.Request!.RunToken),
            Stamped("""{"event":"handshake","contractVersion":1}""", ForeignToken),
            Stamped("""{"event":"phase","phase":"tracing"}""", ForeignToken),
            Stamped("""{"event":"progress","step":1,"totalSteps":2,"vramBytes":1}""", ForeignToken),
            Stamped("""{"event":"error","category":"trace","message":"someone else's"}""", ForeignToken),
            Stamped("""{"event":"done","cancelled":true}""", ForeignToken)
        ], exitCode: 0);
        var run = await running.WaitAsync(TestBudgets.Contended);

        AssertEx.False(process.Killed, "A foreign handshake's version is not this script's.");
        AssertEx.False(run.State.Done, "Only this spawn's done ends the job.");
        AssertEx.False(run.State.ScriptCancelled);
        AssertEx.Null(run.State.ErrorMessage);
        AssertEx.Empty(events.Replay(id, afterSequence: 0).Events);
        var failure = AssertEx.NotNull(run.FailureOrNull(cancelRequested: false));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, failure.Status);
        AssertEx.Contains(failure.ErrorMessage, "exited with status 0");
        AssertEx.Contains(failure.ErrorMessage, ForeignToken, StringComparison.Ordinal, "The foreign lines are kept as output.");
    }

    /// <summary>A foreign frame is not liveness: a script that only relays another spawn's frames is silent.</summary>
    [Test]
    public async Task AForeignFrame_DoesNotHoldOffTheInactivityBound()
    {
        using var work = new TempDirectory("modellab-runner");
        var clock = new MovableClock();
        var options = new ModelLabJobOptions
        {
            InactivityTimeout = TimeSpan.FromSeconds(2),
            ExitGracePeriod = TimeSpan.FromSeconds(2),
            MaxDuration = TimeSpan.FromHours(1)
        };
        using var process = new SilentProcess(clock);

        var run = await Runner(process, NewEvents(), clock, options)
                        .RunAsync(Guid.NewGuid(), Path.Combine(work.Path, "job"), JobJson(work.Path), Script, static _ => Task.CompletedTask, CancellationToken.None)
                        .WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.Killed.Task.IsCompleted);
        AssertEx.Equal("The Model Lab script stopped reporting and was terminated.", run.FailureReason);
    }

    private static ModelLabScriptRunner Runner(ITrainingProcessSpawner process, TrainingRunEventBuffer events, TimeProvider clock, ModelLabJobOptions options) =>
        new(process, events, options, clock);

    private static TrainingRunEventBuffer NewEvents() =>
        new(Options.Create(new TrainingRunEventBufferOptions()));

    /// <summary>The paths <see cref="ScriptedProcess" /> writes its result files to.</summary>
    private static byte[] JobJson(string directory) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tracePath"] = Path.Combine(directory, "trace.jsonl"),
            ["resultPath"] = Path.Combine(directory, "result.json")
        }));

    private static string Stamped(string frame, string token) =>
        string.Concat(frame.AsSpan(0, frame.Length - 1), ",\"token\":\"", token, "\"}");

    /// <summary>A foreign heartbeat at 1.5 s, then the clock past the bound from the start but within it from the heartbeat; it waits for the kill.</summary>
    /// <remarks>The runner reads the output only after it built the watchdog, so the clock moves in step with it.</remarks>
    private sealed class SilentProcess : ITrainingProcessSpawner, ITrainingProcessHandle
    {
        private readonly MovableClock _clock;
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SilentProcess(MovableClock clock)
        {
            _clock = clock;
        }

        public TaskCompletionSource Killed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TrainingLaunchReceipt Receipt { get; } = new()
        {
            Pid = 4545,
            Pgid = 4545,
            ExecutablePath = "/venv/bin/python",
            StartTicks = 1,
            RunToken = "token"
        };

        public ITrainingProcessHandle Spawn(TrainingSpawnRequest request) =>
            this;

        public async IAsyncEnumerable<string> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _clock.Advance(TimeSpan.FromSeconds(1.5));
            yield return Stamped("""{"event":"heartbeat","phase":"tracing"}""", ForeignToken);
            // The runner applied that line before asking for the next one.
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Killed.Task.WaitAsync(TestBudgets.Contended, cancellationToken);
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public void RequestStop()
        {
        }

        public void KillGroup()
        {
            Killed.TrySetResult();
            _exit.TrySetResult(137);
        }

        public Task WriteLineAsync(string line, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Dispose() =>
            KillGroup();
    }

    /// <summary>Moves only when the test says; the watchdog's poll still runs on real timers, and judges by this clock.</summary>
    private sealed class MovableClock : TimeProvider
    {
        private long _ticks = DateTimeOffset.UnixEpoch.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) =>
            Interlocked.Add(ref _ticks, delta.Ticks);
    }
}
