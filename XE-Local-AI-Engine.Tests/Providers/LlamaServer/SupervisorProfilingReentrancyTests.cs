namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Coverage for re-entrancy into the supervisor from INSIDE an exclusive profiling / benchmark body. For a benchmark
///     run that body is the whole normal invocation pipeline, so it calls back into <c>EnsureRunningAsync</c> (through
///     the provider's warm) and <c>GetRuntimeInfo</c> (through the effective-context read) for the very key the
///     exclusive operation holds the single-flight gate on. Those calls must be answered from the process that
///     operation pinned: routed into the profiling-owned exclusion added by <c>30a514d00</c> they park on a semaphore
///     their own frame holds, which is a self-deadlock, not a wait. That exclusion still applies to every caller from
///     another flow — <c>SupervisorProfilingTests</c> pins that half on its own, and it is re-asserted here while the
///     body is reusing the pinned process, so both behaviours are proven together in one run.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SupervisorProfilingReentrancyTests
{
    private const string ModelName = "llama3";

    [Test]
    public async Task Benchmark_BodyEnsuresTheSameKey_IsHandedTheProcessItPinned()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        // real-timer: this bound is a DEADLOCK detector, not a wait for an event. On the unfixed supervisor the nested
        // ensure parks forever on the gate its own frame holds, and no in-process signal can ever report that. The wait
        // is CANCELLED rather than merely asserted so the exclusive operation unwinds and releases the runtime
        // operation barrier — otherwise the supervisor's own disposal would hang behind it and take the module with it.
        using var deadlock = new CancellationTokenSource();
        Uri? pinnedEndpoint = null;
        LlamaServerEndpoint? reentered = null;
        var deadlocked = false;
        try
        {
            await supervisor.RunExclusiveBenchmarkAsync(ModelName,
                ModelRole.Chat,
                ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 24),
                LlamaServerBenchmarkLaunchPolicy.DeterministicV1,
                async (context, _) =>
                {
                    pinnedEndpoint = context.Endpoint.BaseAddress;
                    deadlock.CancelAfter(TestBudgets.Contended);
                    reentered = await supervisor.EnsureRunningAsync(ModelName, ModelRole.Chat, deadlock.Token);
                    return true;
                },
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (deadlock.IsCancellationRequested)
        {
            deadlocked = true;
        }

        AssertEx.False(deadlocked,
            "The benchmark body's own EnsureRunningAsync never completed: it self-deadlocked on the per-key single-flight gate the exclusive operation holds across the body.");
        AssertEx.Equal(AssertEx.NotNull(pinnedEndpoint), AssertEx.NotNull(reentered).BaseAddress);
        AssertEx.Equal(expected: 1, launcher.LaunchCount); // The body reused the pinned process; it spawned no second one.
    }

    [Test]
    public async Task Benchmark_BodyReadsRuntimeInfo_SeesThePinnedWindowWhileOtherFlowsStillSeeNone()
    {
        // The second half of the same flow: after warming, the invocation pipeline reads the launched effective context
        // to size its budgeters, and a benchmark's context admission REJECTS the run when that read comes back null.
        // The measurement's own window is the right answer for the measurement, and still the wrong one for a chat —
        // whose budget must never be sized off a transient measurement spawn.
        var launcher = new FakeProcessLauncher();
        var healthProbe = new FakeHealthProbe
        {
            EffectiveContextTokens = 4096
        };
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe);

        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LlamaServerRuntimeInfo? insideBody = null;
        var benchmark = supervisor.RunExclusiveBenchmarkAsync(ModelName,
            ModelRole.Chat,
            ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 24),
            LlamaServerBenchmarkLaunchPolicy.DeterministicV1,
            async (_, _) =>
            {
                insideBody = supervisor.GetRuntimeInfo(ModelName, ModelRole.Chat);
                read.SetResult();
                await release.Task;
                return true;
            },
            CancellationToken.None);

        await read.Task;

        // Read from the TEST's own execution context, which never inherited the body's marker.
        var outsideBody = supervisor.GetRuntimeInfo(ModelName, ModelRole.Chat);
        release.SetResult();
        await benchmark;

        AssertEx.Equal(expected: 4096, AssertEx.NotNull(insideBody).EffectiveContextTokens);
        AssertEx.Null(outsideBody, "Only the measurement's own flow may read the pinned process's effective context.");
    }

    [Test]
    public async Task Benchmark_BodyRunsTheRealProviderWarmAndContextRead_Completes()
    {
        // One level up, through the seam BenchmarkRunExecutorTests substitutes away: its IInvocationRunner is a mock,
        // so the two supervisor calls the REAL invocation pipeline makes from inside a benchmark body — the local
        // provider's warm and the effective-context read that follows it — were never exercised against the supervisor
        // holding the gate. Both run through the real LlamaServerLocalModelProvider here, in the body's own flow.
        var launcher = new FakeProcessLauncher();
        var healthProbe = new FakeHealthProbe
        {
            EffectiveContextTokens = 4096
        };
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe);
        var provider = new LlamaServerLocalModelProvider(supervisor, new FakeModelStore(), new AdvanceableTimeProvider());

        // real-timer: a DEADLOCK detector, as above — the unfixed warm never returns and nothing can signal that.
        using var deadlock = new CancellationTokenSource();
        int? effectiveContextTokens = null;
        var deadlocked = false;
        try
        {
            await supervisor.RunExclusiveBenchmarkAsync(ModelName,
                ModelRole.Chat,
                ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 24),
                LlamaServerBenchmarkLaunchPolicy.DeterministicV1,
                async (_, _) =>
                {
                    deadlock.CancelAfter(TestBudgets.Contended);
                    await provider.WarmModelAsync(ModelName, deadlock.Token);
                    var runtimeInfo = await provider.GetRuntimeInfoAsync(ModelName, deadlock.Token);
                    effectiveContextTokens = runtimeInfo?.EffectiveContextTokens;
                    return true;
                },
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (deadlock.IsCancellationRequested)
        {
            deadlocked = true;
        }

        AssertEx.False(deadlocked, "The provider warm inside a benchmark body self-deadlocked on the supervisor's per-key gate.");
        AssertEx.Equal<int?>(4096,
            effectiveContextTokens,
            "A benchmark whose effective context reads back null is rejected by its own context admission.");
        AssertEx.Equal(expected: 1, launcher.LaunchCount);
    }

    [Test]
    public async Task Profiling_BodyReuse_DoesNotHandThePinnedProcessToAnOutsideFlow()
    {
        // 30a514d00's guarantee, asserted WHILE the body is reusing the pinned process: the re-entrancy marker is
        // scoped to the body's own logical flow, so a caller from another flow is still refused and still spawns its
        // own process after teardown. The outside ensure is started from the TEST's execution context (after a barrier
        // the body signals), which never inherited the body's marker.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        var reused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // real-timer: the same deadlock detector as the first test. The barrier is signalled from a finally, so a
        // regression that makes the re-entrant ensure fail still lets this test run to a red instead of stranding it.
        using var deadlock = new CancellationTokenSource();
        int? pinnedProcessId = null;
        Uri? pinnedEndpoint = null;
        LlamaServerEndpoint? reentered = null;
        var deadlocked = false;
        var profiling = supervisor.RunExclusiveProfilingAsync(ModelName,
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            async (context, _) =>
            {
                pinnedProcessId = context.ProcessId;
                pinnedEndpoint = context.Endpoint.BaseAddress;
                try
                {
                    deadlock.CancelAfter(TestBudgets.Contended);
                    reentered = await supervisor.EnsureRunningAsync(ModelName, ModelRole.Chat, deadlock.Token);
                }
                finally
                {
                    reused.SetResult();
                }

                await release.Task;
                return true;
            },
            CancellationToken.None);

        await reused.Task;
        var outside = supervisor.EnsureRunningAsync(ModelName, ModelRole.Chat, CancellationToken.None);

        // No sleep: a reuse of the pinned process completes the outside ensure synchronously (the reuse probe is
        // rate-limited, so the fast path never awaits), while a correctly refused reuse parks on the gate. Sampled
        // rather than asserted here so a failure cannot strand the barrier and hang the supervisor's disposal.
        var outsideCompletedDuringBody = outside.IsCompleted;

        release.SetResult();
        try
        {
            await profiling;
        }
        catch (OperationCanceledException) when (deadlock.IsCancellationRequested)
        {
            deadlocked = true;
        }

        var outsideEndpoint = await outside;

        AssertEx.False(deadlocked, "The body's own EnsureRunningAsync never completed: it self-deadlocked on the gate its own frame holds.");
        AssertEx.Equal(AssertEx.NotNull(pinnedEndpoint),
            AssertEx.NotNull(reentered).BaseAddress,
            "The body's own ensure must be answered from the process the exclusive operation pinned.");
        AssertEx.False(outsideCompletedDuringBody,
            "A caller from another flow must not be handed the pinned process while the exclusive body runs.");
        AssertEx.Equal(expected: 2, launcher.LaunchCount); // The outside caller spawned its own process after teardown.
        var outsideProcess = AssertEx.NotNull(supervisor.GetRegisteredProcess(ModelName, ModelRole.Chat));
        AssertEx.NotEqual<int?>(pinnedProcessId, outsideProcess.Handle.ProcessId);
        AssertEx.False(outsideProcess.IsProfilingOwned);
        AssertEx.Equal(outsideProcess.Endpoint.BaseAddress, outsideEndpoint.BaseAddress);
    }

    [Test]
    public async Task Profiling_AfterTheBody_OrdinaryCallersSeeNoMarker()
    {
        // The marker must not outlive the body: an ensure afterwards spawns its own, non-profiling process and a
        // runtime-info read reports THAT process's window, not a stale reuse of the torn-down measurement spawn.
        var launcher = new FakeProcessLauncher();
        var healthProbe = new FakeHealthProbe
        {
            EffectiveContextTokens = 8192
        };
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe);

        // real-timer: the same deadlock detector as the first test — a regression must red, never hang the module.
        using var deadlock = new CancellationTokenSource();
        var deadlocked = false;
        try
        {
            await supervisor.RunExclusiveProfilingAsync(ModelName,
                ModelRole.Chat,
                ResolvedLaunchArguments.Explore(),
                enableMetrics: false,
                async (context, _) =>
                {
                    deadlock.CancelAfter(TestBudgets.Contended);
                    var duringBody = await supervisor.EnsureRunningAsync(ModelName, ModelRole.Chat, deadlock.Token);
                    AssertEx.Equal(context.Endpoint.BaseAddress, duringBody.BaseAddress);
                    return true;
                },
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (deadlock.IsCancellationRequested)
        {
            deadlocked = true;
        }

        var after = await supervisor.EnsureRunningAsync(ModelName, ModelRole.Chat, CancellationToken.None);

        AssertEx.False(deadlocked, "The body's own EnsureRunningAsync never completed: it self-deadlocked on the gate its own frame holds.");
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        var registered = AssertEx.NotNull(supervisor.GetRegisteredProcess(ModelName, ModelRole.Chat));
        AssertEx.False(registered.IsProfilingOwned, "The process serving after the measurement must be an ordinary one.");
        AssertEx.Equal(registered.Endpoint.BaseAddress, after.BaseAddress);
        AssertEx.Equal(expected: 8192, AssertEx.NotNull(supervisor.GetRuntimeInfo(ModelName, ModelRole.Chat)).EffectiveContextTokens);
    }

    [Test]
    public async Task Benchmark_BodyEnsures_AfterThePinnedProcessExited_FailsInsteadOfWaitingForever()
    {
        // The pinned process crashed mid-measurement. The body still holds the gate, so falling through to the normal
        // decision path would be the same self-deadlock; it must fail with the supervisor's classified runtime error.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        using var deadlock = new CancellationTokenSource();
        LlamaRuntimeException? failure = null;
        var deadlocked = false;
        try
        {
            await supervisor.RunExclusiveBenchmarkAsync(ModelName,
                ModelRole.Chat,
                ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 24),
                LlamaServerBenchmarkLaunchPolicy.DeterministicV1,
                async (_, _) =>
                {
                    launcher.Handles.Single().SimulateExit();

                    // real-timer: as above — a bound that turns a hang into a red, not a wait for an event.
                    deadlock.CancelAfter(TestBudgets.Contended);
                    failure = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
                        supervisor.EnsureRunningAsync(ModelName, ModelRole.Chat, deadlock.Token));
                    return true;
                },
                CancellationToken.None);
        }
        catch (OperationCanceledException) when (deadlock.IsCancellationRequested)
        {
            deadlocked = true;
        }

        AssertEx.False(deadlocked, "An ensure for a pinned process that exited must fail, never park on the gate its own frame holds.");
        AssertEx.Contains(AssertEx.NotNull(failure).Message, "no longer running", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(expected: 1, launcher.LaunchCount); // No respawn was attempted from under the gate.
    }
}
