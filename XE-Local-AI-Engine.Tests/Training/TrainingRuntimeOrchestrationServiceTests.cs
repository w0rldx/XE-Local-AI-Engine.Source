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
