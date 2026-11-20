namespace XE_Local_AI_Engine.Tests.Capacity;

using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     GPU demand against an idle holder, through the real GPU work gate, the real llama runtime gate and the real broker.
///     Only the holder is a fake, standing in for the Model Lab session.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class GpuIdleHolderBrokerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    [Test]
    public async Task WithNoHolder_EveryMemberIsANoOp()
    {
        var broker = new GpuIdleHolderBroker();

        broker.RequestPreemption();

        AssertEx.False(broker.IsIdlePreemptible);
        var waiting = broker.WaitForReleaseAsync(Bound, CancellationToken.None);
        AssertEx.True(waiting.IsCompletedSuccessfully, "With no holder the wait answers at once.");
        AssertEx.True(await waiting);
    }

    /// <summary>The self-preemption guard: the holder's own gate and lease acquisitions raise no demand, so a job with no competitor ends Idle.</summary>
    [Test]
    public async Task AColdAdmission_WithNoCompetitor_RaisesNoDemand_AndEndsIdle()
    {
        await using var harness = new IdleHolderHarness();

        var holder = await harness.AdmitIdleAsync();

        AssertEx.Equal(expected: 0, holder.PreemptionRequestCount, "The admission's own gate and lease raised demand.");
        AssertEx.True(harness.Broker.IsIdlePreemptible);
        AssertEx.Equal(GpuWorkKind.ModelLabExperiment, harness.Gate.ExclusiveKind!.Value, "Idle still holds the gate.");
        AssertEx.True(harness.Supervisor.IsKeepWarmSuppressed(), "Idle still holds the lease.");
    }

    /// <summary>The negative control for the guard above: the same admission through a lease that raises demand preempts itself.</summary>
    [Test]
    public async Task AColdAdmission_ThroughADemandRaisingLease_WouldPreemptItself()
    {
        await using var harness = new IdleHolderHarness();
        var holder = new FakeGpuIdleHolder(static () => Task.CompletedTask);
        harness.Broker.Register(holder);
        AssertEx.Succeeded(harness.Gate.TryBeginExclusive(GpuWorkKind.ModelLabExperiment, out var handle));
        using var handleScope = handle;

        await using var lease = await harness.Supervisor.AcquireRuntimeMutationLeaseOrNullAsync(CancellationToken.None);

        AssertEx.NotNull(lease);
        AssertEx.True(holder.PreemptionPending, "A demand-raising lease latches demand on the holder admitting it.");
        harness.Broker.Deregister(holder);
    }

    [Test]
    public async Task DemandWhileAdmitting_IsLatched_AndServedWhenTheJobEnds()
    {
        await using var harness = new IdleHolderHarness();
        var holder = new FakeGpuIdleHolder(static () => Task.CompletedTask);
        harness.Broker.Register(holder);

        harness.Broker.RequestPreemption();

        AssertEx.True(holder.PreemptionPending, "Demand that races the admission is latched, not lost.");
        AssertEx.Equal(FakeGpuIdleHolder.HolderState.Admitting, holder.State);
        holder.EnterActive();
        holder.ExitActive();
        AssertEx.Equal(expected: 1, holder.EvictionCount, "The job ends straight into eviction, never Idle.");
        harness.Broker.Deregister(holder);
    }

    [Test]
    public async Task DemandWhileActive_NeverPreempts_AndIsServedAtExitActive()
    {
        await using var harness = new IdleHolderHarness();
        var holder = await harness.AdmitAsync();

        AssertEx.False(harness.Gate.TryBeginShared(GpuWorkKind.ImageJob, out _), "An active job is not preempted.");
        AssertEx.False(await harness.Broker.WaitForReleaseAsync(Bound, CancellationToken.None), "A wait against an active job answers false at once.");

        AssertEx.Equal(FakeGpuIdleHolder.HolderState.Active, holder.State);
        AssertEx.True(holder.PreemptionPending);
        AssertEx.Equal(expected: 0, holder.EvictionCount);
        holder.ExitActive();
        await holder.Released.WaitAsync(TestBudgets.Contended);
        AssertEx.Succeeded(harness.Gate.TryBeginShared(GpuWorkKind.ImageJob, out var image), "The latched demand freed the gate when the job ended.");
        image.Dispose();
        AssertEx.False(harness.Supervisor.IsKeepWarmSuppressed(), "And the lease.");
    }

    [Test]
    public async Task AGateRefusal_EvictsAnIdleHolder()
    {
        await using var harness = new IdleHolderHarness();
        var holder = await harness.AdmitIdleAsync();

        AssertEx.False(harness.Gate.TryBeginExclusive(GpuWorkKind.TrainingRun, out _), "The idle holder still owns the gate at the refusal.");

        await holder.Released.WaitAsync(TestBudgets.Contended);
        AssertEx.Equal(expected: 1, holder.EvictionCount);
        AssertEx.False(harness.Broker.IsIdlePreemptible, "An evicted holder is deregistered.");
        AssertEx.Succeeded(harness.Gate.TryBeginExclusive(GpuWorkKind.TrainingRun, out var run), "The next attempt admits.");
        run.Dispose();
    }

    [Test]
    public async Task EvictionStartsOnce_UnderContendedDemand()
    {
        await using var harness = new IdleHolderHarness();
        var evictionMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = await harness.AdmitIdleAsync(evictionMayFinish.Task);

        var demands = Enumerable.Range(0, 32)
                                .Select(index => Task.Run(async () =>
                                {
                                    switch (index % 4)
                                    {
                                        case 0:
                                            _ = harness.Gate.TryBeginShared(GpuWorkKind.Benchmark, out var shared);
                                            shared?.Dispose();
                                            return true;
                                        case 1:
                                            harness.Broker.RequestPreemption();
                                            return true;
                                        case 2:
                                            return await harness.Broker.WaitForReleaseAsync(Bound, CancellationToken.None);
                                        default:
                                            using (await harness.Supervisor.EnterRuntimeMutationSharedAsync(CancellationToken.None))
                                            {
                                                return true;
                                            }
                                    }
                                }))
                                .ToArray();
        await AssertEx.EventuallyAsync(() => holder.PreemptionRequestCount >= 32, TestBudgets.Contended, "Every demand reached the holder.");
        evictionMayFinish.SetResult();

        var answers = await Task.WhenAll(demands).WaitAsync(TestBudgets.Contended);

        AssertEx.True(answers.All(static released => released), "Every waiter saw the release.");
        AssertEx.Equal(expected: 1, holder.EvictionCount, "Concurrent demand latches ONE eviction.");
    }

    [Test]
    public async Task ARequestersCancellation_EndsOnlyItsWait_NeverTheEviction()
    {
        await using var harness = new IdleHolderHarness();
        var evictionMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = await harness.AdmitIdleAsync(evictionMayFinish.Task);
        using var requester = new CancellationTokenSource();

        var waiting = harness.Broker.WaitForReleaseAsync(Bound, requester.Token);
        await requester.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => waiting);
        AssertEx.Equal(FakeGpuIdleHolder.HolderState.Evicting, holder.State, "The eviction the cancelled requester started carries on.");
        evictionMayFinish.SetResult();
        await holder.Released.WaitAsync(TestBudgets.Contended);
        AssertEx.Null(harness.Gate.ExclusiveKind);
    }

    /// <summary>The exclusive-profiling path waits on the runtime gate through its public exclusive entry, so it asks the idle holder to leave.</summary>
    [Test]
    public async Task AnExclusiveProfilingWaiter_EvictsAnIdleHolder()
    {
        await using var harness = new IdleHolderHarness();
        var holder = await harness.AdmitIdleAsync();

        var measured = await harness.Supervisor.RunExclusiveProfilingAsync("llama3",
                                        ModelRole.Chat,
                                        ResolvedLaunchArguments.Explore(),
                                        enableMetrics: false,
                                        static (_, _) => Task.FromResult(result: true),
                                        CancellationToken.None)
                                    .WaitAsync(TestBudgets.Contended);

        AssertEx.True(measured);
        AssertEx.Equal(expected: 1, holder.EvictionCount);
        AssertEx.Equal(expected: 1, harness.Launcher.LaunchCount, "The profiling spawn ran once the holder left.");
    }

    [Test]
    public async Task AModelLoad_EvictsAnIdleHolder_AndLoads()
    {
        await using var harness = new IdleHolderHarness();
        var holder = await harness.AdmitIdleAsync();

        _ = await harness.Supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None).WaitAsync(TestBudgets.Contended);

        AssertEx.Equal(expected: 1, holder.EvictionCount);
        AssertEx.Equal(expected: 1, harness.Launcher.LaunchCount);
        await harness.Supervisor.EvictAllRolesAsync("llama3", CancellationToken.None);
    }

    [Test]
    public async Task OneRetry_AfterAnIdleHolderReleased_Admits()
    {
        await using var harness = new IdleHolderHarness();
        var holder = await harness.AdmitIdleAsync();

        using var export = await harness.Gate.BeginExclusiveOrNullAsync(harness.Broker, GpuWorkKind.Export, CancellationToken.None);

        AssertEx.NotNull(export, "The retry after the release admits.");
        AssertEx.Equal(expected: 1, holder.EvictionCount);
        AssertEx.Equal(GpuWorkKind.Export, harness.Gate.ExclusiveKind!.Value);
    }

    [Test]
    public async Task OneRetry_AgainstAnActiveHolder_RefusesAtOnce()
    {
        await using var harness = new IdleHolderHarness();
        var holder = await harness.AdmitAsync();

        var refused = await harness.Gate.BeginSharedOrNullAsync(harness.Broker, GpuWorkKind.ImageJob, CancellationToken.None);

        AssertEx.Null(refused);
        AssertEx.True(holder.PreemptionPending);
        AssertEx.Equal(GpuWorkKind.ModelLabExperiment, harness.Gate.ExclusiveKind!.Value);
    }

    /// <summary>A node with no idle holder refuses exactly as before: the wait answers at once and the one retry meets the same holder.</summary>
    [Test]
    public async Task OneRetry_WithNoHolder_RefusesAsBefore()
    {
        var broker = new GpuIdleHolderBroker();
        var gate = new GpuWorkGate(broker);
        AssertEx.Succeeded(gate.TryBeginExclusive(GpuWorkKind.TrainingRun, out var run));
        using var runScope = run;

        AssertEx.Null(await gate.BeginExclusiveOrNullAsync(broker, GpuWorkKind.Export, CancellationToken.None));
        AssertEx.Null(await gate.BeginSharedOrNullAsync(broker, GpuWorkKind.ImageJob, CancellationToken.None));
        AssertEx.Equal(GpuWorkKind.TrainingRun, gate.ExclusiveKind!.Value);
    }
}
