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

    /// <summary>No Model Lab worker is left unconfirmed, so admission is the gate's and the lease's alone.</summary>
    private static TrainingRuntimeOrchestrationService Orchestration(IdleHolderHarness holders) =>
        Orchestration(holders.Gate, holders.Broker, holders.Supervisor, Substitute.For<IModelLabActiveJob>());

    /// <summary>Over a provider substitute that runs the admission check it is handed and reports what it would have done.</summary>
    internal static TrainingRuntimeOrchestrationService Orchestration(IGpuWorkGate gate,
        IGpuIdleHolderPreemption idlePreemption,
        ILlamaServerProcessSupervisor supervisor,
        IModelLabActiveJob labJob)
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
            labJob);
    }
}
