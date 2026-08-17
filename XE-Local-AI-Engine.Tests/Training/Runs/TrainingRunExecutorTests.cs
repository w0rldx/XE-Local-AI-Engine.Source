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

        await harness.ExecuteAsync();

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
        // The inactivity bound is generous here, so only the absolute ceiling can stop this run.
        await using var harness = await Harness.CreateAsync(this,
            lines: [],
            inactivityTimeout: TimeSpan.FromMinutes(10),
            maxRunDuration: TimeSpan.FromMilliseconds(200));

        await harness.ExecuteAsync();

        AssertEx.True(harness.Handle.Killed, "A run that never ends still has to give the GPU back.");
        _ = await harness.Store.Received(1)
                         .CompleteRunAsync(harness.RunId,
                             TrainingWorkStatus.Failed,
                             Arg.Is<string>(message => message.Contains("maximum duration", StringComparison.Ordinal)),
                             Arg.Any<CancellationToken>());
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

    /// <summary>Wires one executor over scripted collaborators and a controllable clock.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        public const int Pid = 5150;
        public const int Pgid = 5150;

        private readonly TrainingWorkClaim _claim;
        private readonly TrainingRunExecutor _executor;
        private readonly TaskCompletionSource _spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Harness(TrainingRunExecutor executor,
            TrainingWorkClaim claim,
            ITrainingRunStore store,
            FakeTrainingProcessSpawner spawner,
            FakeTrainingProcessHandle handle,
            TrainingRunCancellationRegistry cancellations,
            TrainingRunWorkspace workspace)
        {
            _executor = executor;
            _claim = claim;
            Store = store;
            Spawner = spawner;
            Handle = handle;
            Cancellations = cancellations;
            Workspace = workspace;
        }

        public ITrainingRunStore Store { get; }
        public FakeTrainingProcessSpawner Spawner { get; }
        public FakeTrainingProcessHandle Handle { get; }
        public TrainingRunCancellationRegistry Cancellations { get; }
        public TrainingRunWorkspace Workspace { get; }
        public Guid RunId => _claim.TargetId;
        public TrainingLaunchReceiptV1? PersistedReceipt { get; private set; }

        public static async Task<Harness> CreateAsync(TrainingRunExecutorTests owner,
            IReadOnlyList<string> lines,
            int exitCode = 0,
            bool capacityGranted = true,
            bool runtimeReady = true,
            TimeSpan? inactivityTimeout = null,
            TimeSpan? maxRunDuration = null)
        {
            var runId = Guid.NewGuid();
            var datasetId = Guid.NewGuid();
            var freezeId = Guid.NewGuid();
            var workspace = new TrainingRunWorkspace(new FixedNodeDataDirectory(owner._root), owner._keyHolder);
            await workspace.WriteFrozenDatasetAsync(datasetId, freezeId, Encoding.UTF8.GetBytes("{\"sequence\":0}\n"), CancellationToken.None);

            var staged = workspace.StagedDirectory(runId);
            var scripted = lines.Select(line => line.Replace("__STAGED__", staged, StringComparison.Ordinal)).ToArray();

            var receipt = new TrainingLaunchReceipt(Pid, Pgid, "/venv/bin/python", StartTicks: 42, RunToken: "token");
            var handle = new FakeTrainingProcessHandle(receipt, scripted, exitCode);
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
                     .Returns(callInfo => Task.FromResult(new TrainingArtifactRecord(Guid.NewGuid(),
                         runId,
                         callInfo.Arg<TrainingArtifactInput>().Kind,
                         callInfo.Arg<TrainingArtifactInput>().Path,
                         Sha256: null,
                         SizeBytes: 0,
                         TrainingArtifactSmokeState.Pending,
                         SmokeReason: null,
                         CommittedModelName: null,
                         Version: 1,
                         CreatedAtUtc: 0,
                         UpdatedAtUtc: 0)));

            var capacity = Substitute.For<ITrainingCapacityGate>();
#pragma warning disable CA2000 // Ownership passes to the executor, which disposes the reservation in its finally.
            var reservation = new TrainingCapacityReservation(capacityGranted, capacityGranted ? null : "no room", Handle: null);
#pragma warning restore CA2000
            _ = capacity.ReserveAsync(Arg.Any<TrainingFootprintEstimate>(), Arg.Any<CancellationToken>()).Returns(reservation);

            var defaults = Substitute.For<ITrainingOptionDefaultsCalculator>();
            _ = defaults.EstimateAsync(Arg.Any<Guid>(), Arg.Any<TrainingRunOptionsV1>(), Arg.Any<CancellationToken>())
                        .Returns(new TrainingFootprintEstimate(1, 1, 1, 1, Experimental: false));

            var runtime = Substitute.For<ITrainingRuntimeService>();
            _ = runtime.ResolveInterpreterPath().Returns(runtimeReady ? "/venv/bin/python" : null);
            _ = runtime.GetStatus()
                       .Returns(new TrainingRuntimeStatus(runtimeReady ? TrainingRuntimePhase.Ready : TrainingRuntimePhase.Idle,
                           IsRunning: false,
                           Terminal: true,
                           [],
                           LogStartSequence: 0,
                           SanitizedError: null,
                           Installed: null,
                           StartedAtUtc: null,
                           CompletedAtUtc: null));

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
                Options.Create(new TrainingRunQueueOptions
                {
                    InactivityTimeout = inactivityTimeout ?? TimeSpan.FromMilliseconds(200),
                    MaxRunDuration = maxRunDuration ?? TimeSpan.FromHours(24)
                }),
                TimeProvider.System,
                NullLogger<TrainingRunExecutor>.Instance);

            var harness = new Harness(executor,
                new TrainingWorkClaim(QueueSequence: 1, TrainingWorkKind.TrainingRun, runId, Version: 2, run),
                store,
                spawner,
                handle,
                cancellations,
                workspace);

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

        public Task WaitForSpawnAsync() =>
            _spawned.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public async ValueTask DisposeAsync()
        {
            Handle.Dispose();
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private static TrainingRunRecord Run(Guid runId, Guid datasetId, Guid freezeId) =>
            new(runId,
                datasetId,
                "v1:abc",
                DatasetRevision: 1,
                JsonSerializer.SerializeToUtf8Bytes(new TrainingRunFreezeV1
                {
                    FreezeId = freezeId,
                    DatasetContentFingerprint = "v1:abc",
                    DatasetRevision = 1
                }, TrainingJson.Options),
                Guid.NewGuid(),
                LinkedInstalledModelName: null,
                LinkedModelContentFingerprint: null,
                JsonSerializer.SerializeToUtf8Bytes(new TrainingRunOptionsV1(), TrainingJson.Options),
                LicenseConfirmationJson: null,
                TrainingRunStatus.Queued,
                ProgressJson: null,
                LogTail: null,
                LaunchReceiptJson: null,
                ErrorMessage: null,
                Version: 2,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0,
                TrainingWorkStatus.Running,
                WorkErrorMessage: null);
    }
}
