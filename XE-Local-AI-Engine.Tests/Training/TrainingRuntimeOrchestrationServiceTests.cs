namespace XE_Local_AI_Engine.Tests.Training;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Training.Runtime;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Capacity;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The admission check a runtime install or remove runs inside the provider's start transaction, over the real GPU work
///     gate, the real llama supervisor and the real broker. The provider is a substitute that runs the check it is handed.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class TrainingRuntimeOrchestrationServiceTests
{
    /// <summary>The start-then-install order: the job holds gate and lease, so the check refuses and the job keeps running.</summary>
    [Test]
    public async Task Install_WhileAModelLabJobRuns_IsRefused_AndTheJobIsNotPreempted()
    {
        await using var holders = new IdleHolderHarness();
        var holder = await holders.AdmitAsync();
        var orchestration = Orchestration(holders);

        var result = await orchestration.InstallAsync(CancellationToken.None);

        AssertEx.Equal(TrainingRuntimeInstallOutcome.GpuJobRunning, result.Outcome);
        AssertEx.True(orchestration.IsGpuJobRunning);
        AssertEx.Equal(FakeGpuIdleHolder.HolderState.Active, holder.State);
        AssertEx.True(holder.PreemptionPending, "The worker leaves when its job ends, so the next attempt finds it gone.");
    }

    [Test]
    public async Task Remove_WhileAModelLabJobRuns_IsRefused()
    {
        await using var holders = new IdleHolderHarness();
        _ = await holders.AdmitAsync();
        var orchestration = Orchestration(holders);

        AssertEx.Equal(TrainingRuntimeRemoveOutcome.GpuJobRunning, await orchestration.RemoveAsync(CancellationToken.None));
    }

    /// <summary>An idle worker reads as not busy to the endpoint's early check, and the admission evicts it before admitting.</summary>
    [Test]
    public async Task Install_WithAnIdleModelLabWorker_EvictsIt_AndAdmits()
    {
        await using var holders = new IdleHolderHarness();
        var holder = await holders.AdmitIdleAsync();
        var orchestration = Orchestration(holders);
        AssertEx.False(orchestration.IsGpuJobRunning, "An idle worker is evictable, not a running job.");

        var result = await orchestration.InstallAsync(CancellationToken.None);

        AssertEx.Equal(TrainingRuntimeInstallOutcome.Started, result.Outcome);
        AssertEx.Equal(expected: 1, holder.EvictionCount);
        AssertEx.Null(holders.Gate.ExclusiveKind);
        AssertEx.False(holders.Supervisor.IsKeepWarmSuppressed());
    }

    [Test]
    public async Task Remove_WithAnIdleModelLabWorker_EvictsIt_AndAdmits()
    {
        await using var holders = new IdleHolderHarness();
        var holder = await holders.AdmitIdleAsync();
        var orchestration = Orchestration(holders);

        AssertEx.Equal(TrainingRuntimeRemoveOutcome.Removed, await orchestration.RemoveAsync(CancellationToken.None));
        AssertEx.Equal(expected: 1, holder.EvictionCount);
    }

    /// <summary>The routes' pre-check joins a Model Lab eviction already under way instead of answering 409 for it.</summary>
    [Test]
    public async Task PreCheck_MeetingAnEvictionInProgress_JoinsIt_AndDoesNotRefuse()
    {
        await using var holders = new IdleHolderHarness();
        var evictionMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = await holders.AdmitIdleAsync(evictionMayFinish.Task);
        holder.RequestPreemption();
        var orchestration = Orchestration(holders);
        AssertEx.True(orchestration.IsGpuJobRunning, "A leaving worker still holds the gate and the lease.");

        var checking = orchestration.RefusesForRunningGpuJobAsync(CancellationToken.None);
        await AssertEx.EventuallyAsync(() => holder.PreemptionRequestCount >= 2, TestBudgets.Contended, "The pre-check's wait reached the leaving holder.");
        evictionMayFinish.SetResult();

        AssertEx.False(await checking.WaitAsync(TestBudgets.Contended));
        AssertEx.Equal(expected: 1, holder.EvictionCount);
    }

    /// <summary>A running Model Lab job is never waited on: the pre-check refuses at once and latches the demand.</summary>
    [Test]
    public async Task PreCheck_WhileAModelLabJobIsActive_RefusesAtOnce_AndLatchesTheDemand()
    {
        await using var holders = new IdleHolderHarness();
        var holder = await holders.AdmitAsync();
        var orchestration = Orchestration(holders);

        AssertEx.True(await orchestration.RefusesForRunningGpuJobAsync(CancellationToken.None).WaitAsync(TestBudgets.Contended));
        AssertEx.Equal(FakeGpuIdleHolder.HolderState.Active, holder.State);
        AssertEx.True(holder.PreemptionPending);
    }

    /// <summary>A steering hand-off ends the worker's session while its arms keep the gate and the lease: the re-check still refuses.</summary>
    [Test]
    public async Task PreCheck_WhenTheLeavingWorkerHandsItsHoldsToSteeringArms_Refuses()
    {
        await using var holders = new IdleHolderHarness();
        var evictionMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeGpuIdleHolder? holder = null;
        holder = new FakeGpuIdleHolder(async () =>
        {
            await evictionMayFinish.Task;
            holders.Broker.Deregister(holder!);
        });
        holders.Broker.Register(holder);
#pragma warning disable CA2000 // Given back in the finally below, as the arms would at their end.
        AssertEx.Succeeded(holders.Gate.TryBeginExclusive(GpuWorkKind.ModelLabExperiment, out var gate));
#pragma warning restore CA2000
        var lease = AssertEx.NotNull(await holders.Supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None, raisesDemand: false));
        try
        {
            holder.EnterActive();
            holder.ExitActive();
            holder.RequestPreemption();
            var orchestration = Orchestration(holders);

            var checking = orchestration.RefusesForRunningGpuJobAsync(CancellationToken.None);
            await AssertEx.EventuallyAsync(() => holder.PreemptionRequestCount >= 2, TestBudgets.Contended, "The pre-check's wait reached the leaving holder.");
            evictionMayFinish.SetResult();

            AssertEx.True(await checking.WaitAsync(TestBudgets.Contended));
            AssertEx.Equal(FakeGpuIdleHolder.HolderState.Released, holder.State);
        }
        finally
        {
            await lease.DisposeAsync();
            gate.Dispose();
        }
    }

    /// <summary>A CPU job registered first refuses both mutations, which put the runtime back as it was; its release admits them.</summary>
    [Test]
    public async Task AMutation_WhileARuntimeJobIsRegistered_IsRefused_AndThePhaseIsRestored()
    {
        await using var holders = new IdleHolderHarness();
        using var script = new TempDirectory("runtime-job");
        var runtime = new PhasedRuntime();
        var users = new TrainingRuntimeUsers();
        var orchestration = Orchestration(holders, runtime, users);
        AssertEx.Succeeded(ModelLabRuntimeJob.TryEnter(users, runtime.Runtime, Script(script), "not installed", out var job, out var refusal), refusal ?? "entered");

        AssertEx.Equal(TrainingRuntimeInstallOutcome.GpuJobRunning, (await orchestration.InstallAsync(CancellationToken.None)).Outcome);
        AssertEx.Equal(TrainingRuntimePhase.Ready, runtime.Phase);
        AssertEx.Equal(TrainingRuntimeRemoveOutcome.GpuJobRunning, await orchestration.RemoveAsync(CancellationToken.None));
        AssertEx.Equal(TrainingRuntimePhase.Ready, runtime.Phase);
        AssertEx.Equal(runtime.Interpreter, job.Interpreter);

        job.Dispose();
        AssertEx.Equal(TrainingRuntimeInstallOutcome.Started, (await orchestration.InstallAsync(CancellationToken.None)).Outcome);
    }

    /// <summary>A mutation that published its phase first refuses the entry with the "being changed" text, and the entry leaves no registration behind.</summary>
    [Test]
    public async Task AnEntry_WhileAMutationIsPublished_IsRefused_AsBeingChanged_AndHoldsNothing()
    {
        await using var holders = new IdleHolderHarness();
        using var script = new TempDirectory("runtime-job");
        var runtime = new PhasedRuntime
        {
            Barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var users = new TrainingRuntimeUsers();
        var installing = Orchestration(holders, runtime, users).InstallAsync(CancellationToken.None);
        await runtime.Published.Task.WaitAsync(TestBudgets.Contended);

        AssertEx.False(ModelLabRuntimeJob.TryEnter(users, runtime.Runtime, Script(script), "not installed", out _, out var refusal));
        AssertEx.Equal(ModelLabRuntimeJob.RuntimeChangingMessage, refusal);
        AssertEx.False(users.InUse, "A refused entry gives its registration back.");

        runtime.Barrier.SetResult();
        AssertEx.Equal(TrainingRuntimeInstallOutcome.Started, (await installing.WaitAsync(TestBudgets.Contended)).Outcome);
    }

    /// <summary>Registrations count; disposing one twice releases it once, so the other still refuses until it goes too.</summary>
    [Test]
    public async Task TwoRegistrations_OneDisposedTwice_StillRefuse_UntilTheOtherIsDisposed()
    {
        await using var holders = new IdleHolderHarness();
        using var script = new TempDirectory("runtime-job");
        var runtime = new PhasedRuntime();
        var users = new TrainingRuntimeUsers();
        var orchestration = Orchestration(holders, runtime, users);
        AssertEx.Succeeded(ModelLabRuntimeJob.TryEnter(users, runtime.Runtime, Script(script), "not installed", out var first, out _));
        AssertEx.Succeeded(ModelLabRuntimeJob.TryEnter(users, runtime.Runtime, Script(script), "not installed", out var second, out _));

        first.Dispose();
        first.Dispose();

        AssertEx.True(users.InUse);
        AssertEx.Equal(TrainingRuntimeInstallOutcome.GpuJobRunning, (await orchestration.InstallAsync(CancellationToken.None)).Outcome);
        second.Dispose();
        AssertEx.False(users.InUse);
        AssertEx.Equal(TrainingRuntimeInstallOutcome.Started, (await orchestration.InstallAsync(CancellationToken.None)).Outcome);
    }

    /// <summary>A runtime at rest that cannot run the job refuses with the caller's own text, and the entry holds nothing.</summary>
    [Test]
    public void AnEntry_IntoARuntimeAtRestThatCannotRunTheJob_IsRefused_WithTheCallersText()
    {
        using var script = new TempDirectory("runtime-job");
        var users = new TrainingRuntimeUsers();
        var runtime = new PhasedRuntime();

        AssertEx.False(ModelLabRuntimeJob.TryEnter(users, runtime.Runtime, Path.Combine(script.Path, "missing.py"), "not installed", out _, out var noScript));
        AssertEx.Equal("not installed", noScript);
        runtime.Phase = TrainingRuntimePhase.Failed;
        AssertEx.False(ModelLabRuntimeJob.TryEnter(users, runtime.Runtime, Script(script), "not installed", out _, out var failed));
        AssertEx.Equal("not installed", failed);
        runtime.Phase = TrainingRuntimePhase.Ready;
        runtime.Runtime.ResolveInterpreterPath().Returns((string?)null);
        AssertEx.False(ModelLabRuntimeJob.TryEnter(users, runtime.Runtime, Script(script), "not installed", out _, out var noInterpreter));
        AssertEx.Equal("not installed", noInterpreter);
        AssertEx.False(users.InUse);
    }

    /// <summary>A recheck that throws lets the exception out and still gives the registration back, so installs and removes are not blocked until a restart.</summary>
    [Test]
    public void AnEntry_WhoseRecheckThrows_HoldsNothing()
    {
        using var script = new TempDirectory("runtime-job");
        var users = new TrainingRuntimeUsers();
        var statusThrows = new PhasedRuntime();
        statusThrows.Runtime.GetStatus().Returns(_ => throw new IOException("status unreadable"));
        var interpreterThrows = new PhasedRuntime();
        interpreterThrows.Runtime.ResolveInterpreterPath().Returns(_ => throw new IOException("venv unreadable"));

        foreach (var runtime in new[]
                 {
                     statusThrows,
                     interpreterThrows
                 })
        {
            _ = AssertEx.Throws<IOException>(() => ModelLabRuntimeJob.TryEnter(users, runtime.Runtime, Script(script), "not installed", out _, out _));
            AssertEx.False(users.InUse, "A throwing recheck gives its registration back.");
        }
    }

    /// <summary>No Model Lab worker is left unconfirmed, so admission is the gate's and the lease's alone.</summary>
    private static TrainingRuntimeOrchestrationService Orchestration(IdleHolderHarness holders) =>
        Orchestration(holders.Gate, holders.Broker, holders.Supervisor, Substitute.For<IModelLabActiveJob>());

    /// <summary>A script file that exists, as the job's recheck requires.</summary>
    private static string Script(TempDirectory directory)
    {
        var path = directory.FilePath("job.py");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static TrainingRuntimeOrchestrationService Orchestration(IdleHolderHarness holders, PhasedRuntime runtime, TrainingRuntimeUsers users) =>
        new(runtime.Runtime,
            Substitute.For<ITrainingRuntimePrerequisiteProbe>(),
            holders.Gate,
            holders.Broker,
            holders.Supervisor,
            Substitute.For<IModelLabActiveJob>(),
            users);

    /// <summary>Over a provider substitute that runs the admission check it is handed and reports what it would have done.</summary>
    internal static TrainingRuntimeOrchestrationService Orchestration(IGpuWorkGate gate,
        IGpuIdleHolderPreemption idlePreemption,
        ILlamaServerProcessSupervisor supervisor,
        IModelLabActiveJob labJob,
        TrainingRuntimeUsers? runtimeUsers = null)
    {
        var runtime = Substitute.For<ITrainingRuntimeService>();
        _ = runtime.InstallAsync(Arg.Any<Func<CancellationToken, Task<bool>>?>(), Arg.Any<CancellationToken>())
                   .Returns(async call => new TrainingRuntimeInstallResult
                   {
                       Outcome = await call.Arg<Func<CancellationToken, Task<bool>>>()(CancellationToken.None)
                           ? TrainingRuntimeInstallOutcome.Started
                           : TrainingRuntimeInstallOutcome.GpuJobRunning
                   });
        _ = runtime.RemoveAsync(Arg.Any<Func<CancellationToken, Task<bool>>?>(), Arg.Any<CancellationToken>())
                   .Returns(async call => await call.Arg<Func<CancellationToken, Task<bool>>>()(CancellationToken.None)
                       ? TrainingRuntimeRemoveOutcome.Removed
                       : TrainingRuntimeRemoveOutcome.GpuJobRunning);
        return new TrainingRuntimeOrchestrationService(runtime,
            Substitute.For<ITrainingRuntimePrerequisiteProbe>(),
            gate,
            idlePreemption,
            supervisor,
            labJob,
            runtimeUsers ?? new TrainingRuntimeUsers());
    }

    /// <summary>A provider that mutates as the real one does: publish the non-ready phase, wait at the test's barrier, ask the admission, restore the phase on a refusal.</summary>
    private sealed class PhasedRuntime
    {
        private readonly Lock _lock = new();
        private TrainingRuntimePhase _phase = TrainingRuntimePhase.Ready;

        public PhasedRuntime()
        {
            Runtime.ResolveInterpreterPath().Returns(Interpreter);
            Runtime.GetStatus().Returns(_ => Status(Phase));
            Runtime.InstallAsync(Arg.Any<Func<CancellationToken, Task<bool>>?>(), Arg.Any<CancellationToken>())
                   .Returns(async call => new TrainingRuntimeInstallResult
                   {
                       Outcome = await MutateAsync(TrainingRuntimePhase.AcquiringUv, call.Arg<Func<CancellationToken, Task<bool>>>())
                           ? TrainingRuntimeInstallOutcome.Started
                           : TrainingRuntimeInstallOutcome.GpuJobRunning
                   });
            Runtime.RemoveAsync(Arg.Any<Func<CancellationToken, Task<bool>>?>(), Arg.Any<CancellationToken>())
                   .Returns(async call => await MutateAsync(TrainingRuntimePhase.Removing, call.Arg<Func<CancellationToken, Task<bool>>>())
                       ? TrainingRuntimeRemoveOutcome.Removed
                       : TrainingRuntimeRemoveOutcome.GpuJobRunning);
        }

        public ITrainingRuntimeService Runtime { get; } = Substitute.For<ITrainingRuntimeService>();

        public string Interpreter { get; } = "/venv/bin/python";

        /// <summary>Set: a mutation waits here after publishing its phase and before asking the admission.</summary>
        public TaskCompletionSource? Barrier { get; init; }

        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TrainingRuntimePhase Phase
        {
            get
            {
                lock (_lock)
                {
                    return _phase;
                }
            }
            set
            {
                lock (_lock)
                {
                    _phase = value;
                }
            }
        }

        /// <summary>An admitted mutation is taken to finish at once and leave a Ready runtime, so the next one starts from rest.</summary>
        private async Task<bool> MutateAsync(TrainingRuntimePhase phase, Func<CancellationToken, Task<bool>> admitAsync)
        {
            var prior = Phase;
            Phase = phase;
            Published.TrySetResult();
            if (Barrier is { } barrier)
            {
                await barrier.Task.WaitAsync(TestBudgets.Contended);
            }

            var admitted = await admitAsync(CancellationToken.None);
            Phase = admitted ? TrainingRuntimePhase.Ready : prior;
            return admitted;
        }

        private static TrainingRuntimeStatus Status(TrainingRuntimePhase phase) =>
            new()
            {
                Phase = phase,
                IsRunning = false,
                Terminal = phase is TrainingRuntimePhase.Ready or TrainingRuntimePhase.Failed or TrainingRuntimePhase.Idle,
                LogLines = [],
                LogStartSequence = 0,
                SanitizedError = null,
                Installed = null,
                StartedAtUtc = null,
                CompletedAtUtc = null
            };
    }
}
