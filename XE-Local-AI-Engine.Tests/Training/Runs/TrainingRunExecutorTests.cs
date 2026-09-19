namespace XE_Local_AI_Engine.Tests.Training.Runs;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     End-to-end executor behaviour against a scripted trainer: no GPU, no venv, no real subprocess. What is pinned
///     here is the part a live 1-epoch run cannot check cheaply — that the launch receipt is durable before any output
///     is read, that a silent trainer is killed rather than waited on forever, that a cooperative stop lands as
///     Cancelled rather than Failed, and that the decrypted dataset is swept on every terminal path.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class TrainingRunExecutorTests : IDisposable
{
    private readonly FixedNodeSqliteKeyHolder _keyHolder = new(RandomNumberGenerator.GetBytes(32));
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Execute_PersistsTheLaunchReceiptBeforeReadingAnyOutput()
    {
        await using var harness = await Harness.CreateAsync(this,
        [
            """{"event":"handshake","contractVersion":1}""",
            """{"event":"phase","phase":"training"}""",
            """{"event":"artifact","kind":"HfAdapterDir","path":"__STAGED__"}""",
            """{"event":"done","cancelled":false}"""
        ]);

        await harness.ExecuteAsync();

        var receipt = AssertEx.NotNull(harness.PersistedReceipt, "The receipt must be written the moment the child exists.");
        AssertEx.Equal(Harness.Pid, receipt.Pid);
        AssertEx.Equal(Harness.Pgid, receipt.Pgid);
        AssertEx.NotNullOrEmpty(receipt.RunToken, "The run token is what proves identity to the reaper.");
        _ = await harness.Store.Received(1).CompleteRunAsync(harness.RunId, TrainingWorkStatus.Succeeded, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_RegistersTheReportedAdapterAsAStagedArtifact()
    {
        await using var harness = await Harness.CreateAsync(this,
        [
            """{"event":"artifact","kind":"HfAdapterDir","path":"__STAGED__"}""",
            """{"event":"done","cancelled":false}"""
        ]);

        await harness.ExecuteAsync();

        _ = await harness.Store.Received(1)
                         .CreateArtifactAsync(Arg.Is<TrainingArtifactInput>(input => input.Kind == TrainingArtifactKind.HfAdapterDir),
                             Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_IgnoresAnArtifactReportedOutsideTheRunsStagedDirectory()
    {
        await using var harness = await Harness.CreateAsync(this,
        [
            """{"event":"artifact","kind":"HfAdapterDir","path":"/etc"}""",
            """{"event":"done","cancelled":false}"""
        ]);

        await harness.ExecuteAsync();

        // The trainer names its own output path; a buggy or tampered script must not be able to register a registry
        // candidate from anywhere on the filesystem.
        _ = await harness.Store.DidNotReceiveWithAnyArgs().CreateArtifactAsync(default!, default);
    }

    [Test]
    public async Task Watchdog_NoHeartbeat_TerminatesRun()
    {
        // A trainer that emits nothing at all: exactly what a wedged CUDA call looks like from the outside, and the
        // reason the protocol has a heartbeat event in the first place.
        await using var harness = await Harness.CreateAsync(this, lines: []);

        await harness.AdvancePastAsync(TimeSpan.FromMilliseconds(250));

        AssertEx.True(harness.Handle.Killed, "A silent trainer is killed, not waited on: it is holding the whole GPU.");
        _ = await harness.Store.Received(1)
                         .CompleteRunAsync(harness.RunId,
                             TrainingWorkStatus.Failed,
                             Arg.Is<string>(message => message.Contains("stopped reporting", StringComparison.Ordinal)),
                             Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Watchdog_BeyondTheMaximumDuration_TerminatesRun()
    {
        // The inactivity bound is generous here, so only the absolute ceiling can stop this run. Its size also sets
        // the watchdog's poll interval to a second, so the clock has to clear that too before the check runs at all.
        await using var harness = await Harness.CreateAsync(this,
            lines: [],
            inactivityTimeout: TimeSpan.FromMinutes(10),
            maxRunDuration: TimeSpan.FromMilliseconds(200));

        await harness.AdvancePastAsync(TimeSpan.FromSeconds(1.5));

        AssertEx.True(harness.Handle.Killed, "A run that never ends still has to give the GPU back.");
        _ = await harness.Store.Received(1)
                         .CompleteRunAsync(harness.RunId,
                             TrainingWorkStatus.Failed,
                             Arg.Is<string>(message => message.Contains("maximum duration", StringComparison.Ordinal)),
                             Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_WhenTheTrainerClosesItsOutputButNeverExits_KillsItAndRecordsTheRun()
    {
        // A trainer that says its piece, closes both pipes and then wedges. Nothing reaps it: the watchdog is
        // already joined by the time the exit is awaited, so an unbounded wait here parks the executor forever —
        // holding the run's TrainingCapacityReservation and starving every later spawn decision on the node.
        await using var harness = await Harness.CreateAsync(this,
        [
            """{"event":"done","cancelled":false}"""
        ], exitsOnStreamClose: false);

        var execution = harness.ExecuteAsync();
        await harness.Handle.ExitWaitEntered.WaitAsync(TestBudgets.Contended);
        await AssertEx.EventuallyAsync(() => harness.Clock.ArmedTimerCount > 0,
            TestBudgets.Contended,
            "The exit grace must be armed on the clock before the clock is moved past it.");
        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        await execution;

        AssertEx.True(harness.Handle.Killed, "A trainer that will not exit has to be taken off the GPU, not waited on.");
        _ = await harness.Store.Received(1)
                         .CompleteRunAsync(harness.RunId,
                             TrainingWorkStatus.Failed,
                             Arg.Is<string>(message => message.Contains("stopped responding", StringComparison.Ordinal)),
                             Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_WhenTheInactivityBoundFallsDueAfterTheStreamClosed_StillRecordsSuccess()
    {
        // The bug the watchdog's cancellation closed: it judged silence on the way OUT. Its delay is a whole poll
        // interval long, so "the stream ended" reached it late, and a trainer whose final phase was quieter than the
        // inactivity bound had its SUCCESSFUL run recorded as Failed — plus a SIGKILL aimed at a process group that
        // had already gone. Here the stream closes first and the bound falls due afterwards; nothing may act on it.
        await using var harness = await Harness.CreateAsync(this,
        [
            """{"event":"done","cancelled":false}"""
        ], exitsOnStreamClose: false);

        var execution = harness.ExecuteAsync();
        await harness.Handle.ExitWaitEntered.WaitAsync(TestBudgets.Contended);
        // Well past the 200 ms inactivity bound and nowhere near the 30 s exit grace, so only the watchdog's bound
        // has come due — and the trainer then exits cleanly, exactly as a slow-finishing one does.
        harness.Clock.Advance(TimeSpan.FromSeconds(5));
        harness.Handle.SignalExit();
        await execution;

        AssertEx.False(harness.Handle.Killed, "A stream that has closed is a run that has finished, not one that has gone quiet.");
        _ = await harness.Store.Received(1)
                         .CompleteRunAsync(harness.RunId, TrainingWorkStatus.Succeeded, null, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cancel_MapsToCancelledRatherThanFailed()
    {
        await using var harness = await Harness.CreateAsync(this, [""" {"event":"done","cancelled":true}"""], exitCode: TrainingRunExecutor.CancelledExitCode);

        await harness.ExecuteAsync();

        _ = await harness.Store.Received(1).CompleteRunAsync(harness.RunId, TrainingWorkStatus.Cancelled, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cancel_SignalsTheProcessGroupCooperatively()
    {
        await using var harness = await Harness.CreateAsync(this, lines: [], exitCode: TrainingRunExecutor.CancelledExitCode);

        var execution = harness.ExecuteAsync();
        await harness.WaitForSpawnAsync();
        AssertEx.True(harness.Cancellations.Cancel(harness.RunId), "A running run is cancellable through the registry.");
        await execution;

        AssertEx.True(harness.Handle.StopRequested, "An operator cancel is SIGTERM to the group, so the trainer can save and exit cleanly.");
        AssertEx.False(harness.Handle.Killed, "Escalating to SIGKILL on an operator cancel would lose the adapter.");
        _ = await harness.Store.Received(1).CompleteRunAsync(harness.RunId, TrainingWorkStatus.Cancelled, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_ErrorEvent_FailsWithTheTrainersOwnReason()
    {
        await using var harness = await Harness.CreateAsync(this,
        [
            """{"event":"error","category":"template","message":"the chat template drops tool calls"}""",
            """{"event":"done","cancelled":false}"""
        ], exitCode: 1);

        await harness.ExecuteAsync();

        _ = await harness.Store.Received(1)
                         .CompleteRunAsync(harness.RunId,
                             TrainingWorkStatus.Failed,
                             "the chat template drops tool calls",
                             Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_DeletesTheDecryptedDatasetOnEveryTerminalPath()
    {
        await using var harness = await Harness.CreateAsync(this, [""" {"event":"error","category":"oom","message":"CUDA out of memory"}"""], exitCode: 1);

        await harness.ExecuteAsync();

        AssertEx.False(Directory.Exists(harness.Workspace.WorkDirectory(harness.RunId)),
            "The decrypted dataset goes on failure too, not only on the happy path.");
    }

    [Test]
    public async Task Execute_WhenCapacityIsRefused_FailsWithoutSpawningAnything()
    {
        await using var harness = await Harness.CreateAsync(this, lines: [], capacityGranted: false);

        await harness.ExecuteAsync();

        AssertEx.Null(harness.Spawner.LastRequest, "A refused reservation must never reach a spawn.");
        _ = await harness.Store.Received(1)
                         .CompleteRunAsync(harness.RunId, TrainingWorkStatus.Failed, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Execute_WhenTheRuntimeIsNotInstalled_FailsBeforeSpawning()
    {
        await using var harness = await Harness.CreateAsync(this, lines: [], runtimeReady: false);

        await harness.ExecuteAsync();

        AssertEx.Null(harness.Spawner.LastRequest, "There is no interpreter to launch through.");
        _ = await harness.Store.Received(1)
                         .CompleteRunAsync(harness.RunId,
                             TrainingWorkStatus.Failed,
                             Arg.Is<string>(message => message.Contains("runtime is not installed", StringComparison.Ordinal)),
                             Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Wires one executor over scripted collaborators and a <see cref="ManualTimeProvider" />. The clock is frozen
    ///     until a test advances it, so the inactivity and max-duration watchdogs are inert for every test that is not
    ///     about them, and exact for the two that are.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        public const int Pid = 5150;
        public const int Pgid = 5150;

        /// <summary>
        ///     How many times <see cref="AdvancePastAsync" /> will step the clock before calling the bound broken.
        ///     Slack, not a budget: one advance is the design, a second covers a poll timer re-armed past the end of
        ///     the previous one, and the rest exist only so the number is not the interesting part of a failure. It
        ///     bounds steps of a clock the test owns, not wall time, so a slow box cannot spend it.
        /// </summary>
        private const int MaxAdvances = 5;

        private readonly TrainingWorkClaim _claim;
        private readonly TrainingRunExecutor _executor;
        private readonly TaskCompletionSource _spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Harness(TrainingRunExecutor executor,
            TrainingWorkClaim claim,
            ITrainingRunStore store,
            FakeTrainingProcessSpawner spawner,
            FakeTrainingProcessHandle handle,
            TrainingRunCancellationRegistry cancellations,
            TrainingRunWorkspace workspace,
            ManualTimeProvider clock)
        {
            _executor = executor;
            _claim = claim;
            Store = store;
            Spawner = spawner;
            Handle = handle;
            Cancellations = cancellations;
            Workspace = workspace;
            Clock = clock;
        }

        public ITrainingRunStore Store { get; }
        public FakeTrainingProcessSpawner Spawner { get; }
        public FakeTrainingProcessHandle Handle { get; }
        public TrainingRunCancellationRegistry Cancellations { get; }
        public TrainingRunWorkspace Workspace { get; }

        /// <summary>The executor's clock. Frozen unless a test moves it, so the watchdog fires only when asked.</summary>
        public ManualTimeProvider Clock { get; }

        public Guid RunId => _claim.TargetId;
        public TrainingLaunchReceiptV1? PersistedReceipt { get; private set; }

        public static async Task<Harness> CreateAsync(TrainingRunExecutorTests owner,
            IReadOnlyList<string> lines,
            int exitCode = 0,
            bool capacityGranted = true,
            bool runtimeReady = true,
            TimeSpan? inactivityTimeout = null,
            TimeSpan? maxRunDuration = null,
            bool exitsOnStreamClose = true,
            TimeSpan? exitGracePeriod = null)
        {
            var clock = new ManualTimeProvider();
            var runId = Guid.NewGuid();
            var datasetId = Guid.NewGuid();
            var freezeId = Guid.NewGuid();
            var workspace = new TrainingRunWorkspace(new FixedNodeDataDirectory(owner._root), owner._keyHolder);
            await workspace.WriteFrozenDatasetAsync(datasetId, freezeId, Encoding.UTF8.GetBytes("{\"sequence\":0}\n"), CancellationToken.None);

            var staged = workspace.StagedDirectory(runId);
            var scripted = lines.Select(line => line.Replace("__STAGED__", staged, StringComparison.Ordinal)).ToArray();

            var receipt = new TrainingLaunchReceipt { Pid = Pid, Pgid = Pgid, ExecutablePath = "/venv/bin/python", StartTicks = 42, RunToken = "token" };
            var handle = new FakeTrainingProcessHandle(receipt, scripted, exitCode, exitsOnStreamClose);
            var spawner = new FakeTrainingProcessSpawner(handle);

            var store = Substitute.For<ITrainingRunStore>();
            var run = Run(runId, datasetId, freezeId);
            _ = store.TransitionAsync(runId, Arg.Any<long>(), Arg.Any<TrainingRunStatus>(), Arg.Any<CancellationToken>())
                     .Returns(callInfo => Task.FromResult(run with
                     {
                         Status = callInfo.ArgAt<TrainingRunStatus>(2),
                         Version = callInfo.ArgAt<long>(1) + 1
                     }));
            _ = store.CompleteRunAsync(runId, Arg.Any<TrainingWorkStatus>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(run with
                     {
                         Status = TrainingRunStatus.Failed
                     }));
            _ = store.CreateArtifactAsync(Arg.Any<TrainingArtifactInput>(), Arg.Any<CancellationToken>())
                     .Returns(callInfo => Task.FromResult(new TrainingArtifactRecord
                     {
                         Id = Guid.NewGuid(),
                         RunId = runId,
                         Kind = callInfo.Arg<TrainingArtifactInput>().Kind,
                         Path = callInfo.Arg<TrainingArtifactInput>().Path,
                         Sha256 = null,
                         SizeBytes = 0,
                         SmokeState = TrainingArtifactSmokeState.Pending,
                         SmokeReason = null,
                         CommittedModelName = null,
                         Version = 1,
                         CreatedAtUtc = 0,
                         UpdatedAtUtc = 0
                     }));

            var capacity = Substitute.For<ITrainingCapacityGate>();
#pragma warning disable CA2000 // Ownership passes to the executor, which disposes the reservation in its finally.
            var reservation = new TrainingCapacityReservation { Granted = capacityGranted, Reason = capacityGranted ? null : "no room", Handle = null };
#pragma warning restore CA2000
            _ = capacity.ReserveAsync(Arg.Any<TrainingFootprintEstimate>(), Arg.Any<CancellationToken>()).Returns(reservation);

            var defaults = Substitute.For<ITrainingOptionDefaultsCalculator>();
            _ = defaults.EstimateAsync(Arg.Any<Guid>(), Arg.Any<TrainingRunOptionsV1>(), Arg.Any<CancellationToken>())
                        .Returns(new TrainingFootprintEstimate { GpuBytes = 1, RamBytes = 1, ParameterCount = 1, TrainableParameterCount = 1, Experimental = false });

            var runtime = Substitute.For<ITrainingRuntimeService>();
            _ = runtime.ResolveInterpreterPath().Returns(runtimeReady ? "/venv/bin/python" : null);
            _ = runtime.GetStatus()
                       .Returns(new TrainingRuntimeStatus
                       {
                           Phase = runtimeReady ? TrainingRuntimePhase.Ready : TrainingRuntimePhase.Idle,
                           IsRunning = false,
                           Terminal = true,
                           LogLines = [],
                           LogStartSequence = 0,
                           SanitizedError = null,
                           Installed = null,
                           StartedAtUtc = null,
                           CompletedAtUtc = null
                       });

            var cancellations = new TrainingRunCancellationRegistry();
            var executor = new TrainingRunExecutor(store,
                new TrainingRunEventBuffer(Options.Create(new TrainingRunEventBufferOptions())),
                defaults,
                capacity,
                runtime,
                spawner,
                workspace,
                cancellations,
                new FixedNodeDataDirectory(owner._root),
                // Short bounds so the watchdog's real behaviour is exercised in milliseconds rather than minutes.
                // Short bounds, but they are measured on `clock` — a manual clock only a test moves. A watchdog on the
                // system clock raced every OTHER test in this file on the box's scheduling jitter: a consume starved
                // past 200 ms was killed as "silent" and the run landed Failed for the watchdog's reason rather than
                // the one under assertion. Frozen, the watchdog provably cannot fire unless a test advances the clock.
                Options.Create(new TrainingRunQueueOptions
                {
                    InactivityTimeout = inactivityTimeout ?? TimeSpan.FromMilliseconds(200),
                    MaxRunDuration = maxRunDuration ?? TimeSpan.FromHours(24),
                    // Deliberately an order of magnitude clear of the inactivity bound, so a test that crosses one
                    // provably has not crossed the other and an assertion can name which bound acted.
                    ExitGracePeriod = exitGracePeriod ?? TimeSpan.FromSeconds(30)
                }),
                clock,
                NullLogger<TrainingRunExecutor>.Instance);

            var harness = new Harness(executor,
                new TrainingWorkClaim { QueueSequence = 1, Kind = TrainingWorkKind.TrainingRun, TargetId = runId, Version = 2, Run = run },
                store,
                spawner,
                handle,
                cancellations,
                workspace,
                clock);

            _ = store.SetLaunchReceiptAsync(runId, Arg.Any<ReadOnlyMemory<byte>?>(), Arg.Any<CancellationToken>())
                     .Returns(callInfo =>
                     {
                         var payload = callInfo.ArgAt<ReadOnlyMemory<byte>?>(1);
                         harness.PersistedReceipt = payload is { } bytes && !bytes.IsEmpty
                             ? JsonSerializer.Deserialize<TrainingLaunchReceiptV1>(bytes.Span, TrainingJson.Options)
                             : null;
                         harness._spawned.TrySetResult();
                         return Task.CompletedTask;
                     });
            return harness;
        }

        public Task ExecuteAsync() =>
            _executor.ExecuteAsync(_claim, CancellationToken.None);

        /// <summary>
        ///     Runs the executor and moves the clock past <paramref name="window" />, for the two tests whose subject IS
        ///     a watchdog bound. It has two phases, and telling them apart is the whole job.
        ///     <para>
        ///         PHASE ONE — advance until the bound fires. The wait on
        ///         <see cref="ManualTimeProvider.ArmedTimerCount" /> is what makes that safe: advancing before the
        ///         watchdog has registered its poll timer would step the clock over a window nothing was waiting on, and
        ///         the run would then hang forever on a bound that can never come due.
        ///     </para>
        ///     <para>
        ///         The advance REPEATS rather than being assumed to land. A window wider than the watchdog's poll
        ///         interval crosses several due instants, and the watchdog re-arms at each;
        ///         <see cref="ManualTimeProvider.Advance" /> honours a re-arm only if it lands before the scan that
        ///         finds nothing armed and sets the clock to the target. A continuation that reads the clock before
        ///         that scan but arms after it is due BEYOND where the clock stopped, with nothing left to move it —
        ///         and the run would then hang rather than fail, the worst shape a test can take in CI. One more
        ///         advance is the only thing that can reach such a timer.
        ///     </para>
        ///     <para>
        ///         <see cref="MaxAdvances" /> keeps that from becoming a way to pass vacuously: the design needs one
        ///         advance and the re-arm race at most one more, so a run still unkilled after several has not lost a
        ///         race — its bound has stopped firing, and the helper says so rather than stepping the clock until
        ///         something happens.
        ///     </para>
        ///     <para>
        ///         PHASE TWO — once the kill has been observed, STOP advancing and only wait for the run to unwind.
        ///         Carrying the armed-timer predicate past the kill is what made this helper fail spuriously: the
        ///         executor always continues into <c>WaitForExitOrEscalateAsync</c>, which arms a
        ///         <see cref="TrainingRunQueueOptions.ExitGracePeriod" /> timer — thirty seconds, an order of magnitude
        ///         past any <paramref name="window" /> either test passes — on this same clock. From that moment "some
        ///         timer is armed" is true of a timer no advance of <paramref name="window" /> can ever reach, so every
        ///         remaining iteration returned from the wait instantly, spent an advance that moved nothing, and the
        ///         helper accused a bound that had in fact fired correctly. Only teardown was still in flight — which
        ///         on a contended runner is precisely what gets delayed.
        ///     </para>
        ///     <para>
        ///         <see cref="FakeTrainingProcessHandle.Killed" /> is the phase signal because it is the first thing
        ///         <c>KillGroup</c> sets, strictly before the exit grace that confounds the timer predicate can exist,
        ///         and because that same call settles the fake's exit — so nothing past the kill needs the clock at
        ///         all. It is NOT a reason to return: the run is still unwinding when it is set, which is why the
        ///         awaited <c>execution</c> below, and not this flag, is what the helper finishes on.
        ///     </para>
        /// </summary>
        public async Task AdvancePastAsync(TimeSpan window)
        {
            var execution = ExecuteAsync();
            var advances = 0;
            while (!Handle.Killed && !execution.IsCompleted)
            {
                if (advances == MaxAdvances)
                {
                    throw new AssertionException($"The run was not killed after {MaxAdvances} advances of {window}: "
                                                 + "the bound under test is no longer firing.");
                }

                await AssertEx.EventuallyAsync(() => Handle.Killed || execution.IsCompleted || Clock.ArmedTimerCount > 0,
                    TestBudgets.Contended,
                    "The run must either be killed or be waiting on a bound of its own for the clock to reach.");
                if (Handle.Killed || execution.IsCompleted)
                {
                    break;
                }

                Clock.Advance(window);
                advances++;
            }

            // The clock's work is over: the kill settled the exit, so what remains is plain asynchronous unwinding.
            await AssertEx.EventuallyAsync(() => execution.IsCompleted,
                TestBudgets.Contended,
                "A killed run must finish unwinding; no advance of the clock can help it, so a stall here is a real one.");
            await execution;
        }

        public Task WaitForSpawnAsync() =>
            _spawned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public async ValueTask DisposeAsync()
        {
            Handle.Dispose();
            await Task.CompletedTask;
        }

        private static TrainingRunRecord Run(Guid runId, Guid datasetId, Guid freezeId) =>
            new()
            {
                Id = runId,
                DatasetId = datasetId,
                DatasetContentFingerprint = "v1:abc",
                DatasetRevision = 1,
                FreezeJson = JsonSerializer.SerializeToUtf8Bytes(new TrainingRunFreezeV1
                {
                    FreezeId = freezeId,
                    DatasetContentFingerprint = "v1:abc",
                    DatasetRevision = 1
                }, TrainingJson.Options),
                BaseArtifactId = Guid.NewGuid(),
                LinkedInstalledModelName = null,
                LinkedModelContentFingerprint = null,
                OptionsJson = JsonSerializer.SerializeToUtf8Bytes(new TrainingRunOptionsV1(), TrainingJson.Options),
                LicenseConfirmationJson = null,
                Status = TrainingRunStatus.Queued,
                ProgressJson = null,
                LogTail = null,
                LaunchReceiptJson = null,
                ErrorMessage = null,
                Version = 2,
                CreatedAtUtc = 0,
                UpdatedAtUtc = 0,
                WorkStatus = TrainingWorkStatus.Running,
                WorkErrorMessage = null
            };
    }
}
