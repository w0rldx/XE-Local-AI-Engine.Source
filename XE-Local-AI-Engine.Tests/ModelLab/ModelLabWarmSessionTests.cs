namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Capacity;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Training;
using XE_Local_AI_Engine.Tests.Training.Runs;
using static ModelLabExperimentServiceTests;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The warm Model Lab session on the node's real DI: real gate, lease, ledger, broker and store. Only the serving worker is scripted; it
///     speaks contract 6, and every wait here is on a signal the worker or the session gives.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabWarmSessionTests
{
    private const int WarmSeconds = 300;

    [Test]
    public async Task AColdTrace_WithNoCompetitor_GoesIdle_KeepingItsHolds_AndItsRowDeletable()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);

        var id = await TraceAsync(host, process);

        AssertEx.Equal(RuntimeResidentState.Idle, host.Service.GetResident()!.State);
        AssertEx.Equal("Qwen/Qwen3.5-0.8B", host.Service.GetResident()!.ModelId);
        AssertEx.Equal<long?>(1000, host.Service.GetResident()!.VramBytes, "The row carries the allocated count of the worker's ready.");
        AssertEx.Equal(GpuWorkKind.ModelLabExperiment, host.Gate.ExclusiveKind!.Value, "An idle worker keeps the gate.");
        AssertEx.True(host.Supervisor.IsKeepWarmSuppressed(), "And the lease.");
        AssertEx.True(host.Ledger.Reserved.GpuBytes > 0, "And its reservation, so no VRAM is unaccounted.");
        AssertEx.False(process.HasExited);
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(id, CancellationToken.None))!.Status);
        AssertEx.Equal(expected: 1, ReceiptFiles(host).Length, "The worker's identity is on disk while it lives.");
        AssertEx.Equal(ModelLabExperimentDeleteOutcome.Deleted, await host.Service.DeleteAsync(id, CancellationToken.None), "The row let go of its receipt when its job ended.");
    }

    [Test]
    public async Task ASecondTraceOnTheSameCheckpoint_StaysWarm_WritingARun_WithoutTheGate_OrDemand()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: GateWithHook.Install, warmIdleSeconds: WarmSeconds);
        var gate = (GateWithHook)host.Gate;
        _ = await TraceAsync(host, process);
        var attempts = gate.ExclusiveAttempts;
        var holder = new FakeGpuIdleHolder(static () => Task.CompletedTask);
        host.Services.GetRequiredService<GpuIdleHolderBroker>().Register(holder);

        var second = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.True(second.ExperimentId.HasValue, second.RefusalMessage ?? "The warm start should have started.");
        await process.WaitForRunAsync(2);
        AssertEx.Equal(RuntimeResidentState.Active, host.Service.GetResident()!.State);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(attempts, gate.ExclusiveAttempts, "A warm start never touches the gate.");
        AssertEx.Equal(expected: 0, holder.PreemptionRequestCount, "Nor raises demand.");
        AssertEx.Equal(expected: 2, process.WrittenLines.Count(static line => line.Contains("\"op\":\"run\"", StringComparison.Ordinal)), "Both jobs ran in the one process.");
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(second.ExperimentId!.Value, CancellationToken.None))!.Status);
        AssertEx.Equal(RuntimeResidentState.Idle, host.Service.GetResident()!.State);
    }

    [Test]
    public async Task DemandWhileIdle_Evicts_AndGivesBackTheReceiptReservationAndLease_BeforeTheGate()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, configure: GateWithHook.Install, warmIdleSeconds: WarmSeconds);
        var atGateRelease = new ConcurrentQueue<string>();
        ((GateWithHook)host.Gate).BeforeExclusiveRelease = () =>
            atGateRelease.Enqueue($"{ReceiptFiles(host).Length}:{host.Ledger.Reserved.GpuBytes}:{host.Supervisor.IsKeepWarmSuppressed()}");
        _ = await TraceAsync(host, process);

        var released = await host.Services.GetRequiredService<IGpuIdleHolderPreemption>().WaitForReleaseAsync(TestBudgets.Contended, CancellationToken.None);

        AssertEx.True(released, "Demand evicts an idle worker at once.");
        AssertEx.True(process.EvictRequested, "It is asked to exit first.");
        AssertEx.Equal("0:0:False", string.Join('|', atGateRelease), "Receipt file, reservation and lease are all back before the gate goes.");
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.Null(host.Service.GetResident());
    }

    [Test]
    public async Task DemandDuringAJob_IsServedWhenTheJobEnds_AndTheSessionNeverGoesIdle()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var buffer = host.Services.GetRequiredService<ITrainingRunEventBuffer>();
        var residentAtEnd = new ModelLabWarmResident
        {
            State = RuntimeResidentState.Idle,
            ModelId = "unset"
        };
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        var id = start.ExperimentId!.Value;
        buffer.EventPublished += (_, args) =>
        {
            if (args.Event.RunId == id && args.Event.Payload.State is not null)
            {
                residentAtEnd = host.Service.GetResident();
            }
        };
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        var released = await host.Services.GetRequiredService<IGpuIdleHolderPreemption>().WaitForReleaseAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
        AssertEx.False(released, "A running job is never preempted; the wait says so at once.");
        AssertEx.Equal(RuntimeResidentState.Active, host.Service.GetResident()!.State);

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(id, CancellationToken.None))!.Status);
        AssertEx.Null(residentAtEnd, "The latched demand turns the job's end into an eviction, never Idle.");
        AssertEx.True(process.EvictRequested);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task AnIdleWorker_IsEvictedWhenItsWarmTimeRunsOut()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var clock = new ManualClock();
        host.Service.IdleClock = clock;
        _ = await TraceAsync(host, process);
        AssertEx.Equal(TimeSpan.FromSeconds(WarmSeconds), clock.LastDueTime!.Value, "The warm time is the node setting's.");
        AssertEx.Equal(RuntimeResidentState.Idle, host.Service.GetResident()!.State);

        clock.FireAll();

        await AssertEx.EventuallyAsync(() => host.Gate.ExclusiveKind is null, TestBudgets.Contended, "The expired worker gives the gate back.");
        AssertEx.True(process.EvictRequested);
        AssertEx.Null(host.Service.GetResident());
        AssertEx.Equal(expected: 0L, host.Ledger.Reserved.GpuBytes);
    }

    [Test]
    public async Task AWarmTimeOfZero_IsOneShot()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: 0);

        _ = await TraceAsync(host, process);

        AssertEx.True(process.EvictRequested, "The worker is evicted as its job ends.");
        AssertEx.True(process.HasExited);
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.Null(host.Service.GetResident());
        AssertEx.Empty(ReceiptFiles(host));
    }

    [Test]
    public async Task AStartOnAnotherCheckpoint_EvictsTheIdleWorker_ThenAdmitsCold()
    {
        using var first = new ScriptedProcess();
        using var second = new ScriptedProcess();
        var spawner = new ScriptedSpawner(first, second);
        await using var host = await LabHost.StartAsync(spawner, warmIdleSeconds: WarmSeconds);
        _ = await TraceAsync(host, first);
        var other = await host.SeedOtherReadyArtifactAsync();

        var start = await host.Service.StartExperimentAsync(host.Request() with
        {
            BaseArtifactId = other
        }, CancellationToken.None);

        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The start should have been admitted cold.");
        AssertEx.True(first.EvictRequested && first.HasExited, "The worker holding the other checkpoint went first.");
        await second.Spawned.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(expected: 2, spawner.SpawnCount);
        second.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(RuntimeResidentState.Idle, host.Service.GetResident()!.State);
    }

    [Test]
    public async Task AStartWhileTheWorkerIsEvicting_WaitsForTheEviction_ThenAdmitsCold()
    {
        var exitHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = new ScriptedProcess
        {
            HoldExitOnEvict = exitHold
        };
        using var second = new ScriptedProcess();
        var spawner = new ScriptedSpawner(first, second);
        await using var host = await LabHost.StartAsync(spawner, warmIdleSeconds: WarmSeconds);
        _ = await TraceAsync(host, first);
        host.Services.GetRequiredService<IGpuIdleHolderPreemption>().RequestPreemption();
        await AssertEx.EventuallyAsync(() => first.EvictRequested, TestBudgets.Contended, "The eviction is under way.");

        var starting = host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        await AssertEx.StaysIncompleteAsync(starting, "The same checkpoint cannot reuse a worker that is leaving.");
        exitHold.SetResult();
        var start = await starting.WaitAsync(TestBudgets.Contended);
        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The start should have been admitted cold.");
        await second.Spawned.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(expected: 2, spawner.SpawnCount);
        second.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
    }

    /// <summary>A frame with another spawn's token is output, never protocol: it cannot end a job, and the silence that follows is the watchdog's.</summary>
    [Test]
    public async Task AFrameWithAnotherToken_IsOnlyOutput_AndTheSilentWorkerIsKilledAndEvicted()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions
        {
            InactivityTimeout = TimeSpan.FromMilliseconds(300)
        }, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        process.SayRaw($$"""{"event":"done","cancelled":false,"token":"not-this-spawn","jobId":"{{process.JobId}}"}""");
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        // Taken as the job's done, it would have failed for the result files nobody wrote; the watchdog's reason shows it was ignored.
        AssertEx.Contains(record.ErrorMessage!, "stopped reporting", StringComparison.Ordinal);
        AssertEx.True(process.Killed);
        AssertEx.Null(host.Service.GetResident());
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task AFrameForAnotherJob_BreaksTheWorker_FailsTheJob_AndEvicts()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        process.SayRaw($$"""{"event":"progress","step":1,"totalSteps":2,"token":"{{process.Request!.RunToken}}","jobId":"{{Guid.NewGuid()}}"}""");
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "broke its protocol", StringComparison.Ordinal);
        AssertEx.True(process.Killed);
        AssertEx.Null(host.Service.GetResident());
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task AJobFrameWhileIdle_BreaksTheWorker_AndTheSessionEvicts()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        _ = await TraceAsync(host, process);

        process.SayRaw($$"""{"event":"heartbeat","phase":"tracing","token":"{{process.Request!.RunToken}}"}""");

        await AssertEx.EventuallyAsync(() => host.Gate.ExclusiveKind is null, TestBudgets.Contended, "A frame outside a job evicts the idle worker.");
        AssertEx.True(process.Killed);
        AssertEx.Null(host.Service.GetResident());
        AssertEx.Equal(expected: 0L, host.Ledger.Reserved.GpuBytes);
    }

    [Test]
    public async Task ACancel_TheWorkerAcknowledges_KeepsItWarm()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        AssertEx.True(host.Service.Cancel(start.ExperimentId!.Value));
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, (await host.Service.GetAsync(start.ExperimentId.Value, CancellationToken.None))!.Status);
        AssertEx.True(process.WrittenLines.Any(static line => line.Contains("\"op\":\"cancel\"", StringComparison.Ordinal)), "The cancel goes over stdin, not as a signal.");
        AssertEx.False(process.Killed);
        AssertEx.False(process.HasExited);
        AssertEx.Equal(RuntimeResidentState.Idle, host.Service.GetResident()!.State);
    }

    [Test]
    public async Task ACancelTheWorkerNeverAcknowledges_KillsIt_AndEvicts_EvenWhenWarm()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true
        };
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions
        {
            CancelAcknowledgementTimeout = TimeSpan.FromMilliseconds(50)
        }, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        AssertEx.True(host.Service.Cancel(start.ExperimentId!.Value));
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.Killed);
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, (await host.Service.GetAsync(start.ExperimentId.Value, CancellationToken.None))!.Status);
        AssertEx.Null(host.Service.GetResident());
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task AJobError_EvictsTheWorker_ThoughItReportedReady()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        process.Finish(["""{"event":"error","category":"out-of-memory","message":"The GPU ran out of memory during the trace."}"""], exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal("The GPU ran out of memory during the trace.", (await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.ErrorMessage!);
        AssertEx.True(process.EvictRequested, "Only a done keeps a worker.");
        AssertEx.True(process.HasExited);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    /// <summary>A worker's HOME, TMPDIR and HF_HOME live in its own directory, so nothing one leaves reaches the next.</summary>
    [Test]
    public async Task EachSpawn_HasItsOwnReceiptFileAndScratchDirectory_AndAnEvictionDeletesOnlyItsOwn()
    {
        using var first = new ScriptedProcess();
        using var second = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(new ScriptedSpawner(first, second), warmIdleSeconds: WarmSeconds);
        var workRoot = ModelLabScriptRunner.WorkRoot(host.Services.GetRequiredService<INodeDataDirectory>());
        _ = await TraceAsync(host, first);
        var firstFile = ReceiptFiles(host).Single();
        var firstScratch = first.Request!.WorkingDirectory;
        AssertEx.Contains(firstFile, first.Request.RunToken, StringComparison.Ordinal);
        AssertEx.Equal(Path.Combine(workRoot, "worker-" + first.Request.RunToken), firstScratch);
        AssertEx.True(Directory.Exists(firstScratch), "The worker's scratch directory lives while the worker does.");
        AssertEx.Equal(ModelLabEjectOutcome.Gone, await host.Service.EjectAsync(CancellationToken.None));
        AssertEx.Empty(ReceiptFiles(host));
        AssertEx.False(Directory.Exists(firstScratch), "Its exit was seen, so its scratch directory went with it.");

        _ = await TraceAsync(host, second);

        var secondFile = ReceiptFiles(host).Single();
        AssertEx.Contains(secondFile, second.Request!.RunToken, StringComparison.Ordinal);
        AssertEx.NotEqual(firstFile, secondFile);
        AssertEx.Equal(Path.Combine(workRoot, "worker-" + second.Request.RunToken), second.Request.WorkingDirectory);
    }

    /// <summary>An exit that is never seen leaves a worker that may still hold VRAM: the ledger keeps its bytes, and starts stop until a reap proves it gone.</summary>
    [Test]
    public async Task AnExitThatIsNeverSeen_KeepsTheReceiptAndReservation_AndRefusesStarts()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true,
            ExitsOnKill = false
        };
        var inspector = new FakeTrainingProcessInspector(facts: null)
        {
            AbsentPresence = TrainingProcessPresence.Unknown
        };
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions
        {
            EvictGrace = TimeSpan.FromMilliseconds(20),
            ExitVerifyTimeout = TimeSpan.FromMilliseconds(20)
        }, configure: services =>
        {
            services.RemoveAll<ITrainingProcessInspector>();
            services.AddSingleton<ITrainingProcessInspector>(inspector);
        }, warmIdleSeconds: 0);

        _ = await TraceAsync(host, process);

        AssertEx.True(process.Killed, "The evict was ignored, so the group was killed.");
        AssertEx.Equal(expected: 1, ReceiptFiles(host).Length, "The only record of a process that may still run.");
        AssertEx.True(Directory.Exists(process.Request!.WorkingDirectory), "Its scratch directory stays: the process may still write there.");
        AssertEx.True(host.Ledger.Reserved.GpuBytes > 0, "Its VRAM stays on the ledger.");
        AssertEx.Null(host.Gate.ExclusiveKind, "The gate goes.");
        AssertEx.False(host.Supervisor.IsKeepWarmSuppressed(), "And the lease.");

        var refused = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.OrphanWorker, refused.Refusal!.Value);
        AssertEx.Contains(refused.RefusalMessage!, "4242", StringComparison.Ordinal);
        AssertEx.Equal(expected: 1, ReceiptFiles(host).Length, "An unreadable process is never taken for gone.");
    }

    /// <summary>A start that passed the orphan check before the gate re-checks under it: the running job's unverified eviction frees the gate only after handing over its receipt.</summary>
    [Test]
    public async Task AColdStartWhoseGateIsFreedByAnUnverifiedEviction_IsRefusedAsAnOrphan_AndSpawnsNothing()
    {
        using var first = new ScriptedProcess
        {
            IgnoresStop = true,
            ExitsOnKill = false
        };
        using var second = new ScriptedProcess();
        var spawner = new ScriptedSpawner(first, second);
        var inspector = new FakeTrainingProcessInspector(facts: null)
        {
            AbsentPresence = TrainingProcessPresence.Unknown
        };
        await using var host = await LabHost.StartAsync(spawner, new ModelLabJobOptions
        {
            EvictGrace = TimeSpan.FromMilliseconds(20),
            ExitVerifyTimeout = TimeSpan.FromMilliseconds(20)
        }, configure: services =>
        {
            GateWithHook.Install(services);
            services.RemoveAll<ITrainingProcessInspector>();
            services.AddSingleton<ITrainingProcessInspector>(inspector);
        }, warmIdleSeconds: 0);
        var running = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.True(running.ExperimentId.HasValue, running.RefusalMessage ?? "The first job should have started.");
        await first.Spawned.WaitAsync(TestBudgets.Contended);
        var firstJob = host.Service.InFlight!;
        var gate = (GateWithHook)host.Gate;
        // Inside the second start's gate attempt, after its orphan check: the first job ends and its eviction is never seen.
        gate.BeforeBegin = async () =>
        {
            gate.BeforeBegin = null;
            first.Finish(CompleteRun, exitCode: 0);
            await firstJob.WaitAsync(TestBudgets.Contended);
        };

        var refused = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None).WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(ModelLabStartRefusal.OrphanWorker, refused.Refusal!.Value, refused.RefusalMessage ?? "The start should have been refused.");
        AssertEx.Equal(expected: 1, spawner.SpawnCount, "No second worker beside one that may still hold VRAM.");
        AssertEx.Null(second.Request);
        AssertEx.Null(host.Gate.ExclusiveKind, "The refused start gives the gate back.");
        AssertEx.Null(host.Service.GetResident());
    }

    /// <summary>The ending job lets go of its slot before it reads the warm time; a checkpoint delete in that window must not see the session as free.</summary>
    [Test]
    public async Task DeletingTheCheckpoint_WhileTheEndingJobReadsItsWarmTime_IsRefused()
    {
        using var process = new ScriptedProcess();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warmTime = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = Substitute.For<INodeRuntimeSettings>();
        settings.GetModelLabWarmIdleSecondsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            readStarted.TrySetResult();
            return warmTime.Task;
        });
        await using var host = await LabHost.StartAsync(process, configure: services =>
        {
            services.RemoveAll<ModelLabExperimentService>();
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<ModelLabExperimentService>(provider, settings));
        });
        _ = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await readStarted.Task.WaitAsync(TestBudgets.Contended);
        using var client = host.Factory.CreateClient();

        using (var request = host.Authorized(HttpMethod.Delete, $"/api/local/v1/training/base-artifacts/{host.Request().BaseArtifactId}"))
            using (var response = await client.SendAsync(request))
            {
                AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, "The session is still Active behind the read.");
            }

        warmTime.SetResult(WarmSeconds);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.True(process.EvictRequested && process.HasExited, "The refused delete was latched, so the job's end evicted rather than idled.");
        AssertEx.Null(host.Service.GetResident());

        using (var request = host.Authorized(HttpMethod.Delete, $"/api/local/v1/training/base-artifacts/{host.Request().BaseArtifactId}"))
            using (var response = await client.SendAsync(request))
            {
                AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode, "Nothing holds the checkpoint any more.");
            }
    }

    /// <summary>An eject whose exit is never seen must not answer 204: the worker may still hold GPU memory, and starts are refused.</summary>
    [Test]
    public async Task EjectingAWorkerWhoseExitIsNeverSeen_IsNotConfirmed_AndTheRouteSaysWhy()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true,
            ExitsOnKill = false
        };
        var inspector = new FakeTrainingProcessInspector(facts: null)
        {
            AbsentPresence = TrainingProcessPresence.Unknown
        };
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions
        {
            EvictGrace = TimeSpan.FromMilliseconds(20),
            ExitVerifyTimeout = TimeSpan.FromMilliseconds(20)
        }, configure: services =>
        {
            services.RemoveAll<ITrainingProcessInspector>();
            services.AddSingleton<ITrainingProcessInspector>(inspector);
        }, warmIdleSeconds: WarmSeconds);
        _ = await TraceAsync(host, process);
        AssertEx.Equal(RuntimeResidentState.Idle, host.Service.GetResident()!.State);

        var outcome = await host.Service.EjectAsync(CancellationToken.None).WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(ModelLabEjectOutcome.NotConfirmed, outcome);
        AssertEx.True(process.Killed);
        AssertEx.Equal(expected: 1, ReceiptFiles(host).Length, "The receipt is kept for the next reap.");
        using var client = host.Factory.CreateClient();
        using var request = host.Authorized(HttpMethod.Post, "/api/local/v1/model-lab/warm/eject");
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode, "Still not confirmed: the reap again could not read the process.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertEx.Equal(nameof(ModelLabStartRefusal.OrphanWorker), document.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>No warmth without a durable identity: a receipt that cannot be written kills the worker before any job, and every hold comes back.</summary>
    [Test]
    public async Task AReceiptThatCannotBeWritten_KillsTheWorker_FailsTheJob_AndGivesEverythingBack()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        // A file where the receipts directory belongs: creating the directory, and so the write, fails with an IOException.
        var receipts = Path.Combine(host.DataRoot, "model-lab", "receipts");
        Directory.CreateDirectory(Path.GetDirectoryName(receipts)!);
        await File.WriteAllTextAsync(receipts, "not a directory");

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The job should have been admitted.");
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.Killed, "A worker with no recorded identity is killed.");
        AssertEx.True(process.HasExited, "Its exit is seen, so its holds are given back in full.");
        AssertEx.False(process.WrittenLines.Any(static line => line.Contains("\"op\":\"run\"", StringComparison.Ordinal)), "It never got the job.");
        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "identity could not be recorded", StringComparison.Ordinal);
        AssertEx.Null(host.Service.GetResident(), "No session stays idle or warm.");
        AssertEx.Null(host.Gate.ExclusiveKind, "The gate comes back.");
        AssertEx.False(host.Supervisor.IsKeepWarmSuppressed(), "The lease comes back.");
        AssertEx.Equal(expected: 0L, host.Ledger.Reserved.GpuBytes, "The reservation is released, not kept as an orphan's.");
    }

    [Test]
    public async Task DeletingTheCheckpoint_OfAnIdleWorker_EvictsItFirst()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        _ = await TraceAsync(host, process);
        using var client = host.Factory.CreateClient();

        using var request = host.Authorized(HttpMethod.Delete, $"/api/local/v1/training/base-artifacts/{host.Request().BaseArtifactId}");
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
        AssertEx.True(process.EvictRequested && process.HasExited);
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    /// <summary>An eject during a job is refused but not forgotten: the job runs on and its end evicts. An idle worker goes at once.</summary>
    [Test]
    public async Task TheEjectRoute_RefusesWhileAJobRuns_ThenEvictsAtItsEnd_AndEvictsAnIdleWorkerAtOnce()
    {
        using var first = new ScriptedProcess();
        using var second = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(new ScriptedSpawner(first, second), warmIdleSeconds: WarmSeconds);
        using var client = host.Factory.CreateClient();
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await first.Spawned.WaitAsync(TestBudgets.Contended);

        using (var request = host.Authorized(HttpMethod.Post, "/api/local/v1/model-lab/warm/eject"))
            using (var response = await client.SendAsync(request))
            {
                AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }

        first.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status, "The job ran on.");
        AssertEx.True(first.EvictRequested && first.HasExited, "The refused eject was latched and served at the job's end.");
        AssertEx.Null(host.Service.GetResident(), "Never idle.");

        _ = await TraceAsync(host, second);
        AssertEx.Equal(RuntimeResidentState.Idle, host.Service.GetResident()!.State);

        using (var request = host.Authorized(HttpMethod.Post, "/api/local/v1/model-lab/warm/eject"))
            using (var response = await client.SendAsync(request))
            {
                AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

        AssertEx.True(second.HasExited);
        AssertEx.Null(host.Service.GetResident());
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    /// <summary>A terminal frame does not finish a job until its ready: a protocol break in between fails the job, and the worker goes.</summary>
    [Test]
    public async Task ADoneThenADuplicateTerminalFrame_FailsTheTrace_AndEvicts()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        process.Finish([.. CompleteRun, """{"event":"done","cancelled":false}"""], exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "broke its protocol", StringComparison.Ordinal);
        AssertEx.True(process.Killed);
        AssertEx.Null(host.Service.GetResident());
    }

    [Test]
    public async Task ADoneWithNoReadyWithinItsBound_FailsTheTrace_AndEvicts()
    {
        using var process = new ScriptedProcess
        {
            WithholdsReady = true
        };
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions
        {
            ReadyTimeout = TimeSpan.FromMilliseconds(50)
        }, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "did not report ready", StringComparison.Ordinal);
        AssertEx.True(process.Killed);
        AssertEx.Null(host.Service.GetResident());
    }

    /// <summary>A worker that exits after its done but before its ready did not finish cleanly: the trace fails, whatever its files say.</summary>
    [Test]
    public async Task ADoneThenAnExitBeforeItsReady_FailsTheTrace()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        // A nonzero status ends the scripted process straight after its done, with no ready.
        process.Finish(CompleteRun, exitCode: 1);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, "exited after its job before reporting ready", StringComparison.Ordinal);
        AssertEx.Null(host.Service.GetResident());
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    [Test]
    public async Task ADoneThenItsReadyThenAnExit_LeavesTheTraceSucceeded()
    {
        using var process = new ScriptedProcess
        {
            ExitsAfterReady = true
        };
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
        await AssertEx.EventuallyAsync(() => host.Service.GetResident() is null, TestBudgets.Contended, "The exited worker's session ends.");
    }

    /// <summary>A direct-inference request refused for the running trace is still demand: the trace finishes, and its end evicts rather than idles.</summary>
    [Test]
    public async Task ADirectInferenceRefusedDuringATrace_EvictsTheWorkerAtTheTracesEnd()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(RuntimeResidentState.Active, host.Service.GetResident()!.State);

        var events = await host.Services.GetRequiredService<IDirectInferenceService>().StreamAsync(new DirectInferenceRequest
        {
            ModelId = "qwen3.5-9b",
            Prompt = "Hi"
        }, CancellationToken.None).ToListAsync();

        AssertEx.Equal(DirectInferenceErrorCodes.GpuWorkExclusive, events.Single().ErrorCode);
        AssertEx.Equal(RuntimeResidentState.Active, host.Service.GetResident()!.State, "The running trace is not interrupted.");
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
        AssertEx.True(process.EvictRequested && process.HasExited, "The refusal's demand turned the trace's end into an eviction.");
        AssertEx.Null(host.Service.GetResident(), "Never idle.");
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    /// <summary>A held receipt too incomplete to probe is reaped in place of its file, so removing the file cannot clear it: the refusal says to restart.</summary>
    [Test]
    public async Task AnUnseenExitWhoseHeldReceiptHasNoStartTime_RefusesStartsSayingToRestartTheNode()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true,
            ExitsOnKill = false,
            Receipt = new TrainingLaunchReceipt
            {
                Pid = 4242,
                Pgid = 4242,
                ExecutablePath = "/venv/bin/python",
                StartTicks = 0,
                RunToken = "token"
            }
        };
        await using var host = await LabHost.StartAsync(process, new ModelLabJobOptions
        {
            EvictGrace = TimeSpan.FromMilliseconds(20),
            ExitVerifyTimeout = TimeSpan.FromMilliseconds(20)
        }, warmIdleSeconds: 0);
        _ = await TraceAsync(host, process);
        AssertEx.True(process.Killed && !process.HasExited, "The kill was sent, and the exit was never seen.");

        var refused = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.OrphanWorker, refused.Refusal!.Value);
        AssertEx.Contains(refused.RefusalMessage!, "pid 4242", StringComparison.Ordinal);
        AssertEx.Contains(refused.RefusalMessage!, "restart the node", StringComparison.Ordinal);
        AssertEx.Contains(refused.RefusalMessage!, ModelLabWarmReceipts.Root(host.Services.GetRequiredService<INodeDataDirectory>()), StringComparison.Ordinal);
    }

    [Test]
    public async Task HostShutdown_EvictsAnIdleWorker()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        _ = await TraceAsync(host, process);

        await host.Service.StopAsync(CancellationToken.None).WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.EvictRequested && process.HasExited);
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.Empty(ReceiptFiles(host));
    }

    /// <summary>A start re-reaps only receipts known to be unresolved: a concurrent start's fresh receipt names a live worker, never an orphan.</summary>
    [Test]
    public async Task AStartWhoseOrphanSweepOutlastsAConcurrentColdStart_NeverSignalsThatStartsWorker()
    {
        using var process = new ScriptedProcess();
        var inspector = new BarrierInspector();
        await using var host = await LabHost.StartAsync(process, configure: services =>
        {
            services.RemoveAll<ITrainingProcessInspector>();
            services.AddSingleton<ITrainingProcessInspector>(inspector);
        });
        var orphan = await SeedOrphanReceiptAsync(host);
        host.Service.AdoptUnresolvedReceipt(orphan);

        var late = host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await inspector.FirstKillEntered.Task.WaitAsync(TestBudgets.Contended);
        var first = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None).WaitAsync(TestBudgets.Contended);
        AssertEx.True(first.ExperimentId.HasValue, first.RefusalMessage ?? "The concurrent start should have been admitted cold.");
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        AssertEx.Contains(ReceiptFiles(host).Single(), process.Request!.RunToken, StringComparison.Ordinal);
        inspector.ReleaseFirstKill.SetResult();
        var refused = await late.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(ModelLabStartRefusal.Busy, refused.Refusal!.Value, "The late start meets the running job at the gate.");
        AssertEx.Equal($"{BarrierInspector.OrphanPid}|{BarrierInspector.OrphanPid}", string.Join('|', inspector.SignalledGroups), "Only the orphan is ever signalled.");
        AssertEx.False(process.Killed);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(first.ExperimentId!.Value, CancellationToken.None))!.Status);
    }

    [Test]
    public async Task AReceiptTheStartupSweepCouldNotProveGone_RefusesStarts()
    {
        using var process = new ScriptedProcess();
        var inspector = new FakeTrainingProcessInspector(facts: null)
        {
            AbsentPresence = TrainingProcessPresence.Unknown
        };
        await using var host = await LabHost.StartAsync(process, configure: services =>
        {
            services.RemoveAll<ITrainingProcessInspector>();
            services.AddSingleton<ITrainingProcessInspector>(inspector);
        });
        var orphan = await SeedOrphanReceiptAsync(host);

        await new ModelLabStartupReaper(host.Services.GetRequiredService<IServiceScopeFactory>(),
            inspector,
            host.Services.GetRequiredService<INodeDataDirectory>(),
            host.Service,
            NullLogger<ModelLabStartupReaper>.Instance).StartAsync(CancellationToken.None);
        var refused = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.OrphanWorker, refused.Refusal!.Value, "The sweep hands what it kept to the service by name.");
        AssertEx.Contains(refused.RefusalMessage!, BarrierInspector.OrphanPid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        AssertEx.True(File.Exists(orphan));
        AssertEx.Null(process.Request, "Nothing spawned.");
    }

    /// <summary>A kept receipt read from disk names the group to wait for and the file to delete once it is empty; the start after that is admitted.</summary>
    [Test]
    public async Task AReceiptWhoseLeaderIsGoneButWhoseGroupLives_RefusesStartsNamingTheGroupAndTheFile_UntilTheGroupEmpties()
    {
        using var process = new ScriptedProcess();
        var inspector = new FakeTrainingProcessInspector(facts: null)
        {
            GroupHasMembers = true
        };
        await using var host = await LabHost.StartAsync(process, configure: services =>
        {
            services.RemoveAll<ITrainingProcessInspector>();
            services.AddSingleton<ITrainingProcessInspector>(inspector);
        });
        var orphan = await SeedOrphanReceiptAsync(host);
        await new ModelLabStartupReaper(host.Services.GetRequiredService<IServiceScopeFactory>(),
            inspector,
            host.Services.GetRequiredService<INodeDataDirectory>(),
            host.Service,
            NullLogger<ModelLabStartupReaper>.Instance).StartAsync(CancellationToken.None);

        var refused = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.OrphanWorker, refused.Refusal!.Value);
        AssertEx.Contains(refused.RefusalMessage!, "process group " + BarrierInspector.OrphanPid.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        AssertEx.Contains(refused.RefusalMessage!, "Check whether the process it names still belongs to a Model Lab worker", StringComparison.Ordinal);
        AssertEx.Contains(refused.RefusalMessage!, orphan, StringComparison.Ordinal);
        AssertEx.True(File.Exists(orphan));
        AssertEx.Null(process.Request, "Nothing spawned.");

        inspector.GroupHasMembers = false;
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "Once the group is empty, the start is admitted.");
        AssertEx.False(File.Exists(orphan));
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.Empty(inspector.SignalledGroups);
        AssertEx.Empty(inspector.SignalledProcesses);
    }

    /// <summary>A receipt that names no process is no proof the process is gone: it stays, and the refusal names the file for the operator.</summary>
    [Test]
    public async Task AMalformedWarmReceipt_IsKept_AndTheRefusalNamesItsPath()
    {
        using var process = new ScriptedProcess();
        var inspector = new BarrierInspector();
        await using var host = await LabHost.StartAsync(process, configure: services =>
        {
            services.RemoveAll<ITrainingProcessInspector>();
            services.AddSingleton<ITrainingProcessInspector>(inspector);
        });
        var malformed = ModelLabWarmReceipts.PathFor(host.Services.GetRequiredService<INodeDataDirectory>(), "malformed");
        await ModelLabWarmReceipts.WriteAsync(malformed, "{not json"u8.ToArray());

        await new ModelLabStartupReaper(host.Services.GetRequiredService<IServiceScopeFactory>(),
            inspector,
            host.Services.GetRequiredService<INodeDataDirectory>(),
            host.Service,
            NullLogger<ModelLabStartupReaper>.Instance).StartAsync(CancellationToken.None);
        var refused = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.OrphanWorker, refused.Refusal!.Value);
        AssertEx.Contains(refused.RefusalMessage!, malformed, StringComparison.Ordinal);
        AssertEx.True(File.Exists(malformed));
        AssertEx.Empty(inspector.SignalledGroups);
        AssertEx.Null(process.Request, "Nothing spawned.");
    }

    /// <summary>A receipt file that was never written proves nothing: the receipt held in memory is reaped instead, and starts stay refused until it reaps gone.</summary>
    [Test]
    public async Task AnUnwrittenReceiptWithAnUnseenExit_RefusesStartsByTheHeldReceipt_UntilItReapsGone()
    {
        using var first = new ScriptedProcess
        {
            IgnoresStop = true,
            ExitsOnKill = false
        };
        using var second = new ScriptedProcess();
        var spawner = new ScriptedSpawner(first, second);
        var inspector = new SwitchableInspector();
        await using var host = await LabHost.StartAsync(spawner, new ModelLabJobOptions
        {
            EvictGrace = TimeSpan.FromMilliseconds(20),
            ExitVerifyTimeout = TimeSpan.FromMilliseconds(20)
        }, configure: services =>
        {
            services.RemoveAll<ITrainingProcessInspector>();
            services.AddSingleton<ITrainingProcessInspector>(inspector);
        }, warmIdleSeconds: WarmSeconds);
        // A file where the receipts directory belongs: the receipt write fails with an IOException.
        var receipts = Path.Combine(host.DataRoot, "model-lab", "receipts");
        Directory.CreateDirectory(Path.GetDirectoryName(receipts)!);
        await File.WriteAllTextAsync(receipts, "not a directory");
        var failed = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.True(failed.ExperimentId.HasValue, failed.RefusalMessage ?? "The first job should have been admitted.");
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.True(first.Killed && !first.HasExited, "The kill was sent, and the exit was never seen.");

        var refused = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.OrphanWorker, refused.Refusal!.Value, "A missing file is no evidence of exit while the receipt is held.");
        AssertEx.Contains(refused.RefusalMessage!, "pid 4242", StringComparison.Ordinal);
        AssertEx.Contains(refused.RefusalMessage!, "Make sure that worker and its child processes have exited", StringComparison.Ordinal);
        AssertEx.False(refused.RefusalMessage!.Contains(receipts, StringComparison.Ordinal), "A held receipt is released by its re-reap, so no file is named for deletion.");
        AssertEx.True(host.Ledger.Reserved.GpuBytes > 0, "Its VRAM stays on the ledger.");
        AssertEx.Equal(expected: 1, spawner.SpawnCount);

        inspector.MarkGone();
        File.Delete(receipts);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "Once the held receipt reaps gone, the start is admitted.");
        await second.Spawned.WaitAsync(TestBudgets.Contended);
        second.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight.WaitAsync(TestBudgets.Contended);
    }

    /// <summary>
    ///     A receipts directory the startup sweep cannot list may hide a live worker: starts are refused, naming it, until a re-scan succeeds.
    ///     A parent that cannot be traversed hides even whether it exists, which is not an empty directory either.
    /// </summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task AReceiptDirectoryTheStartupSweepCannotList_RefusesStarts_UntilARescanSucceeds(bool viaParent)
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var dataDirectory = host.Services.GetRequiredService<INodeDataDirectory>();
        var root = ModelLabWarmReceipts.Root(dataDirectory);
        using (ModelLabStartupReaperTests.Unlistable(root, viaParent))
        {
            await new ModelLabStartupReaper(host.Services.GetRequiredService<IServiceScopeFactory>(),
                host.Services.GetRequiredService<ITrainingProcessInspector>(),
                dataDirectory,
                host.Service,
                NullLogger<ModelLabStartupReaper>.Instance).StartAsync(CancellationToken.None);

            var refused = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

            AssertEx.Equal(ModelLabStartRefusal.OrphanWorker, refused.Refusal!.Value);
            AssertEx.Contains(refused.RefusalMessage!, root, StringComparison.Ordinal);
        }

        AssertEx.Null(process.Request, "Nothing spawned while the scan failed.");
        _ = await TraceAsync(host, process);
    }

    /// <summary>A worker not seen to exit gave the gate and lease back but may still run from the runtime, so an install or remove waits for its reap.</summary>
    [Test]
    public async Task ARuntimeInstallOrRemove_IsRefusedWhileAWorkerNotSeenToExitMayRun_AndAdmittedOnceItReapsGone()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true,
            ExitsOnKill = false
        };
        var inspector = new SwitchableInspector();
        await using var host = await UnseenExitHostAsync(process, inspector, warmIdleSeconds: 0);
        _ = await TraceAsync(host, process);
        AssertEx.Null(host.Gate.ExclusiveKind, "The unseen exit gave the gate back, so the gate alone cannot refuse.");
        var orchestration = TrainingRuntimeOrchestrationServiceTests.Orchestration(host.Gate,
            host.Services.GetRequiredService<IGpuIdleHolderPreemption>(),
            host.Supervisor,
            host.Service);

        AssertEx.Equal(TrainingRuntimeInstallOutcome.GpuJobRunning, (await orchestration.InstallAsync(CancellationToken.None)).Outcome);
        AssertEx.Equal(TrainingRuntimeRemoveOutcome.GpuJobRunning, await orchestration.RemoveAsync(CancellationToken.None));

        inspector.MarkGone();

        AssertEx.Equal(TrainingRuntimeInstallOutcome.Started, (await orchestration.InstallAsync(CancellationToken.None)).Outcome);
        AssertEx.Equal(TrainingRuntimeRemoveOutcome.Removed, await orchestration.RemoveAsync(CancellationToken.None));
    }

    /// <summary>A delete honours the eviction: an idle worker goes first, one whose exit is not seen refuses it, and once it reaps gone the delete proceeds.</summary>
    [Test]
    public async Task DeletingTheCheckpoint_OfAWorkerWhoseExitIsNotSeen_IsRefused_UntilItReapsGone()
    {
        using var process = new ScriptedProcess
        {
            IgnoresStop = true,
            ExitsOnKill = false
        };
        var inspector = new SwitchableInspector();
        await using var host = await UnseenExitHostAsync(process, inspector, warmIdleSeconds: WarmSeconds);
        _ = await TraceAsync(host, process);
        AssertEx.Equal(RuntimeResidentState.Idle, host.Service.GetResident()!.State);
        using var client = host.Factory.CreateClient();
        var route = $"/api/local/v1/training/base-artifacts/{host.Request().BaseArtifactId}";

        using (var request = host.Authorized(HttpMethod.Delete, route))
            using (var response = await client.SendAsync(request))
            {
                AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                AssertEx.Equal("worker-not-confirmed", document.RootElement.GetProperty("reason").GetString());
            }

        AssertEx.True(process.Killed, "The idle worker was evicted first.");
        inspector.MarkGone();

        using (var request = host.Authorized(HttpMethod.Delete, route))
            using (var response = await client.SendAsync(request))
            {
                AssertEx.Equal(HttpStatusCode.NoContent, response.StatusCode, "Once the worker reaps gone, nothing holds the checkpoint.");
            }
    }

    /// <summary>A delete while a job holds the checkpoint's worker is refused, and remembered: the job's end evicts the worker so a retry can pass.</summary>
    [Test]
    public async Task DeletingTheCheckpoint_WhileAJobUsesIt_IsInUseByExperiment_AndTheJobsEndEvicts()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The job should have started.");
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        using var client = host.Factory.CreateClient();
        using (var request = host.Authorized(HttpMethod.Delete, $"/api/local/v1/training/base-artifacts/{host.Request().BaseArtifactId}"))
            using (var response = await client.SendAsync(request))
            {
                AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                AssertEx.Equal("in-use-by-experiment", document.RootElement.GetProperty("reason").GetString());
            }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status, "The job ran on.");
        AssertEx.True(process.EvictRequested && process.HasExited, "The refused delete was latched and served at the job's end.");
        AssertEx.Null(host.Service.GetResident(), "Never idle.");
    }

    private static Task<LabHost> UnseenExitHostAsync(ScriptedProcess process, SwitchableInspector inspector, int warmIdleSeconds) =>
        LabHost.StartAsync(process, new ModelLabJobOptions
        {
            EvictGrace = TimeSpan.FromMilliseconds(20),
            ExitVerifyTimeout = TimeSpan.FromMilliseconds(20)
        }, configure: services =>
        {
            services.RemoveAll<ITrainingProcessInspector>();
            services.AddSingleton<ITrainingProcessInspector>(inspector);
        }, warmIdleSeconds: warmIdleSeconds);

    /// <summary>A residents read on the starting tick must find the row the tick announces.</summary>
    [Test]
    public async Task TheFirstResidencyTickOfAColdStart_SeesTheStartingRow()
    {
        using var process = new ScriptedProcess();
        var observer = new ResidentObserver();
        await using var host = await LabHost.StartAsync(process, configure: services =>
        {
            services.RemoveAll<ModelLabExperimentService>();
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<ModelLabExperimentService>(provider, observer));
        }, warmIdleSeconds: WarmSeconds);
        observer.Read = host.Service.GetResident;

        _ = await TraceAsync(host, process);

        AssertEx.Equal(nameof(RuntimeResidentState.Starting), observer.Seen.First(), $"Ticks saw: {string.Join(", ", observer.Seen)}.");
    }

    /// <summary>A waiter whose demand reached the broker before the session registered never latched; the session sees it on the gate instead.</summary>
    [Test]
    public async Task AGateWaiterWhoseDemandNeverLatched_TurnsTheJobsEndIntoAnEviction()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        // Raises no demand, like an ensure whose demand found no session registered yet.
        var waiting = host.Supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None, raisesDemand: false);
        AssertEx.True(host.Supervisor.HasRuntimeGateWaiters(), "It queues behind the session's lease.");
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        await using var lease = await waiting.WaitAsync(TestBudgets.Contended);

        AssertEx.NotNull(lease, "The waiter gets the gate as the job ends, not when the warm time runs out.");
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
        AssertEx.True(process.EvictRequested && process.HasExited);
        AssertEx.Null(host.Service.GetResident());
    }

    /// <summary>The job lets go of its slot before it reads the warm time; a stop in that window must still wait for the eviction.</summary>
    [Test]
    public async Task HostShutdown_WhileTheEndingJobReadsItsWarmTime_ReturnsOnlyOnceTheSessionHasEvicted()
    {
        using var process = new ScriptedProcess();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warmTime = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = Substitute.For<INodeRuntimeSettings>();
        settings.GetModelLabWarmIdleSecondsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            readStarted.TrySetResult();
            return warmTime.Task;
        });
        await using var host = await LabHost.StartAsync(process, configure: services =>
        {
            services.RemoveAll<ModelLabExperimentService>();
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<ModelLabExperimentService>(provider, settings));
        });
        _ = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        process.Finish(CompleteRun, exitCode: 0);
        await readStarted.Task.WaitAsync(TestBudgets.Contended);

        var stopping = host.Service.StopAsync(CancellationToken.None);

        await AssertEx.StaysIncompleteAsync(stopping, "The session is still Active behind the read.");
        warmTime.SetResult(WarmSeconds);
        await stopping.WaitAsync(TestBudgets.Contended);
        AssertEx.True(process.EvictRequested && process.HasExited, "The stop forbade reuse, so the job's end evicted.");
        AssertEx.Null(host.Service.GetResident());
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.False(host.Supervisor.IsKeepWarmSuppressed());
        AssertEx.Equal(expected: 0L, host.Ledger.Reserved.GpuBytes);
    }

    /// <summary>A stop on the warm claim's own tick must find the job: the claim and the job's publication are one step under the stop's lock.</summary>
    [Test]
    public async Task HostShutdown_OnAWarmStartsClaim_CancelsTheJob_AndReturnsOnlyOnceTheSessionHasEvicted()
    {
        using var process = new ScriptedProcess();
        var observer = new ResidentObserver();
        await using var host = await LabHost.StartAsync(process, configure: services =>
        {
            services.RemoveAll<ModelLabExperimentService>();
            services.AddSingleton(provider => ActivatorUtilities.CreateInstance<ModelLabExperimentService>(provider, observer));
        }, warmIdleSeconds: WarmSeconds);
        _ = await TraceAsync(host, process);
        var stop = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        observer.OnTick = () =>
        {
            // Called on the starting thread, so the stop's first read happens right at the claim.
            if (!stop.Task.IsCompleted && host.Service.GetResident()?.State == RuntimeResidentState.Active)
            {
                stop.TrySetResult(host.Service.StopAsync(CancellationToken.None));
            }
        };

        var second = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await (await stop.Task.WaitAsync(TestBudgets.Contended)).WaitAsync(TestBudgets.Contended);

        AssertEx.True(process.EvictRequested && process.HasExited, "The stop waited for the claimed session's eviction.");
        AssertEx.Null(host.Service.GetResident());
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.False(host.Supervisor.IsKeepWarmSuppressed());
        AssertEx.Equal(expected: 0L, host.Ledger.Reserved.GpuBytes);
        AssertEx.True(second.ExperimentId.HasValue, second.RefusalMessage ?? "The claimed start was published, so it ran as a job.");
        AssertEx.Equal(ModelLabExperimentStatus.Cancelled, (await host.Service.GetAsync(second.ExperimentId!.Value, CancellationToken.None))!.Status);
    }

    /// <summary>A start after the stop is refused before it can claim the idle session.</summary>
    [Test]
    public async Task AStartAfterHostShutdown_IsRefusedAsStopping_AndSpawnsNothing()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        await host.Service.StopAsync(CancellationToken.None).WaitAsync(TestBudgets.Contended);

        var refused = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.Busy, refused.Refusal!.Value);
        AssertEx.Contains(refused.RefusalMessage!, "stopping", StringComparison.Ordinal);
        AssertEx.Null(process.Request, "Nothing spawned.");
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    /// <summary>The runtime routes' pre-check refuses while a trace runs; its demand must still latch, or the trace's worker idles on the old runtime.</summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task ARuntimeInstallRefusedDuringATrace_LatchesDemand_SoTheTracesEndEvictsTheWorker()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, warmIdleSeconds: WarmSeconds);
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The trace should have started.");
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        using var client = host.Factory.CreateClient();

        using (var request = host.Authorized(HttpMethod.Post, "/api/local/v1/training/runtime/install"))
            using (var response = await client.SendAsync(request))
            {
                AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                AssertEx.Equal("gpu-job-running", document.RootElement.GetProperty("reason").GetString());
            }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, (await host.Service.GetAsync(start.ExperimentId!.Value, CancellationToken.None))!.Status);
        AssertEx.True(process.EvictRequested && process.HasExited, "The latched demand turned the trace's end into an eviction.");
        AssertEx.Null(host.Service.GetResident(), "Never idle.");
        AssertEx.Null(host.Gate.ExclusiveKind);
    }

    private static async Task<string> SeedOrphanReceiptAsync(LabHost host)
    {
        var path = ModelLabWarmReceipts.PathFor(host.Services.GetRequiredService<INodeDataDirectory>(), "orphan");
        await ModelLabWarmReceipts.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(BarrierInspector.OrphanReceipt, TrainingJson.Options));
        return path;
    }

    /// <summary>Starts a trace, plays one clean job in <paramref name="process" />, and waits for the job and its session to settle.</summary>
    private static async Task<Guid> TraceAsync(LabHost host, ScriptedProcess process)
    {
        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The trace should have started.");
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        return start.ExperimentId!.Value;
    }

    private static string[] ReceiptFiles(LabHost host)
    {
        var root = Path.Combine(host.DataRoot, "model-lab", "receipts");
        return Directory.Exists(root) ? Directory.GetFiles(root) : [];
    }

    /// <summary>Knows the orphan and the scripted worker by pid, as a live <c>/proc</c> would; the first group kill waits until the test lets it go.</summary>
    private sealed class BarrierInspector : ITrainingProcessInspector
    {
        public const int OrphanPid = 999;

        public static readonly TrainingLaunchReceiptV1 OrphanReceipt = new()
        {
            Pid = OrphanPid,
            Pgid = OrphanPid,
            ExecutablePath = "/venv/bin/python",
            StartTicks = 5,
            RunToken = "orphan"
        };

        private int _kills;

        public TaskCompletionSource FirstKillEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstKill { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<int> SignalledGroups { get; } = new();

        public int HostProcessGroupId => 777;

        // Any other pid answers with the scripted worker's identity, so its receipt, were it reaped, would match and be killed.
        public TrainingProcessFacts? Inspect(int processId) =>
            processId == OrphanPid
                ? new TrainingProcessFacts
                {
                    Pgid = OrphanPid,
                    StartTicks = OrphanReceipt.StartTicks,
                    ExecutablePath = OrphanReceipt.ExecutablePath,
                    RunToken = OrphanReceipt.RunToken
                }
                : new TrainingProcessFacts
                {
                    Pgid = 4242,
                    StartTicks = 1,
                    ExecutablePath = "/venv/bin/python",
                    RunToken = "token"
                };

        public TrainingProcessPresence ProbePresence(int processId) =>
            TrainingProcessPresence.Alive;

        public bool ProcessGroupHasMembers(int processGroupId) =>
            false;

        public async Task<TrainingProcessPresence> KillProcessGroupAsync(int processGroupId, long expectedStartTicks, string expectedRunToken, CancellationToken cancellationToken = default)
        {
            SignalledGroups.Enqueue(processGroupId);
            if (Interlocked.Increment(ref _kills) == 1)
            {
                FirstKillEntered.TrySetResult();
                await ReleaseFirstKill.Task;
            }

            return TrainingProcessPresence.Gone;
        }

        public Task<TrainingProcessPresence> KillProcessAsync(int processId, long expectedStartTicks, string expectedRunToken, CancellationToken cancellationToken = default)
        {
            SignalledGroups.Enqueue(processId);
            return Task.FromResult(TrainingProcessPresence.Gone);
        }
    }

    /// <summary>Knows no process by pid and reads every one as unknown until the test says the worker is gone.</summary>
    private sealed class SwitchableInspector : ITrainingProcessInspector
    {
        private volatile bool _gone;

        public int HostProcessGroupId => 777;

        public void MarkGone() =>
            _gone = true;

        public TrainingProcessFacts? Inspect(int processId) =>
            null;

        public TrainingProcessPresence ProbePresence(int processId) =>
            _gone ? TrainingProcessPresence.Gone : TrainingProcessPresence.Unknown;

        public bool ProcessGroupHasMembers(int processGroupId) =>
            false;

        public Task<TrainingProcessPresence> KillProcessGroupAsync(int processGroupId, long expectedStartTicks, string expectedRunToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProbePresence(processGroupId));

        public Task<TrainingProcessPresence> KillProcessAsync(int processId, long expectedStartTicks, string expectedRunToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(ProbePresence(processId));
    }

    /// <summary>Records what a residents read returns at each tick, as the widget's refetch would see it.</summary>
    private sealed class ResidentObserver : IRuntimeResidencyChangeNotifier
    {
        public Func<ModelLabWarmResident?>? Read { get; set; }

        public ConcurrentQueue<string> Seen { get; } = new();

        /// <summary>Runs on the ticking thread, after the read.</summary>
        public Action? OnTick { get; set; }

        public void NotifyChanged()
        {
            if (Read is { } read)
            {
                Seen.Enqueue(read()?.State.ToString() ?? "none");
            }

            OnTick?.Invoke();
        }
    }

    /// <summary>A clock whose timers fire only when the test says so; the time itself is the system's.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly ConcurrentQueue<ManualTimer> _timers = new();

        public TimeSpan? LastDueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            LastDueTime = dueTime;
            var timer = new ManualTimer(callback, state);
            _timers.Enqueue(timer);
            return timer;
        }

        public void FireAll()
        {
            foreach (var timer in _timers)
            {
                timer.Fire();
            }
        }
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private volatile bool _disposed;

        public ManualTimer(TimerCallback callback, object? state)
        {
            _callback = callback;
            _state = state;
        }

        public void Fire()
        {
            if (!_disposed)
            {
                _callback(_state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) =>
            !_disposed;

        public void Dispose() =>
            _disposed = true;

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
