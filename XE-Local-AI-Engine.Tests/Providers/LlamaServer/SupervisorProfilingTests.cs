namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Coverage for the operator profiling seam (<see cref="LlamaServerProcessSupervisor.RunExclusiveProfilingAsync{T}" />):
///     it acquires machine-readable fit output before an explore spawn, evicts any warm process so the spawn is
///     exclusive, pins the process against idle eviction for the benchmark, appends <c>--metrics</c> to a replay spawn
///     when asked, and always releases the single-flight gate + evicts the transient process — even when the body throws.
/// </summary>
public sealed class SupervisorProfilingTests
{
    [Test]
    public async Task MutationLease_First_BlocksProfilingUntilDisposed()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        var lease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        AssertEx.NotNull(lease);

        var profiling = supervisor.RunExclusiveProfilingAsync("llama3", ModelRole.Chat, ResolvedLaunchArguments.Explore(), false,
            (_, _) => Task.FromResult(true), CancellationToken.None);
        await Task.Delay(50);
        AssertEx.False(profiling.IsCompleted);
        AssertEx.Equal(0, launcher.LaunchCount);

        await lease!.DisposeAsync();
        await profiling;
        AssertEx.Equal(1, launcher.LaunchCount);
    }

    [Test]
    public async Task Profiling_First_MutationLeaseFailsWhileProfileRuns_ThenSucceedsAfterDisposal()
    {
        await using var supervisor = SupervisorFactory.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profiling = supervisor.RunExclusiveProfilingAsync("llama3", ModelRole.Chat, ResolvedLaunchArguments.Explore(), false,
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
                return true;
            }, CancellationToken.None);
        await entered.Task;

        AssertEx.Null(await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None));
        release.SetResult();
        await profiling;

        var after = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        AssertEx.NotNull(after);
        await after!.DisposeAsync();
    }

    [Test]
    public async Task Profiling_Explore_AcquiresMachineReadableFitParams()
    {
        var launcher = new FakeProcessLauncher();
        var fitRunner = new FakeLlamaFitParamsRunner(
            LlamaFitParamsRunResult.Success(["-c 8192 -ngl 32"]));
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            fitParamsRunner: fitRunner);

        IReadOnlyList<string> captured = [];
        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (context, _) =>
            {
                captured = context.FitParamsOutput;
                return Task.FromResult(result: true);
            },
            CancellationToken.None);

        AssertEx.Contains(captured, "-c 8192 -ngl 32");
        AssertEx.Equal(expected: 1, fitRunner.Calls.Count);
        AssertEx.True(fitRunner.Calls.TryPeek(out var spec));
        AssertEx.Contains(spec!.Arguments, "--fit");
    }

    [Test]
    public async Task Profiling_Replay_DoesNotInvokeFitParamsCapability()
    {
        var fitRunner = new FakeLlamaFitParamsRunner(
            LlamaFitParamsRunResult.Success(["-c 8192 -ngl 32"]));
        await using var supervisor = SupervisorFactory.Create(
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            fitParamsRunner: fitRunner);

        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 32),
            enableMetrics: false,
            (_, _) => Task.FromResult(result: true),
            CancellationToken.None);

        AssertEx.Equal(expected: 0, fitRunner.Calls.Count);
    }

    [Test]
    public async Task Profiling_EvictsWarmProcess_ThenSpawnsExclusive()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        // A warm process for the same key is already running before profiling starts.
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);
        var warmHandle = launcher.Handles.Single();

        var warmEvictedAtBody = false;
        var launchCountAtBody = 0;
        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (_, _) =>
            {
                // Inside the body: the warm process was tree-killed and a fresh exclusive process spawned.
                warmEvictedAtBody = warmHandle.WasTreeKilled;
                launchCountAtBody = launcher.LaunchCount;
                return Task.FromResult(result: true);
            },
            CancellationToken.None);

        AssertEx.True(warmEvictedAtBody, "The warm process must be evicted before the profiling spawn.");
        AssertEx.Equal(expected: 2, launchCountAtBody); // warm + exclusive profiling spawn.
    }

    [Test]
    public async Task Profiling_PinnedProcess_NotEvictedByReaper()
    {
        // cap=1 so a competing distinct-model admission must evict an idle LRU to make room. The profiling process is
        // idle past the TTL but PINNED, so the idle-LRU eviction skips it and the competing admission is rejected —
        // proving the pin exempts the process from idle eviction (the same predicate the background reaper honours).
        var launcher = new FakeProcessLauncher();
        var time = new AdvanceableTimeProvider();
        var ttl = TimeSpan.FromMinutes(5);
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: new LlamaServerSupervisorOptions
            {
                MaxLoadedProcesses = 1,
                IdleTimeToLive = ttl,
                MaxRestartAttempts = 3
            },
            timeProvider: time);

        LlamaRuntimeException? rejection = null;
        await supervisor.RunExclusiveProfilingAsync("model-a",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            async (_, _) =>
            {
                // The pinned profiling process is now idle well past the TTL.
                time.Advance(ttl + TimeSpan.FromMinutes(1));

                // A competing distinct model cannot evict the pinned process to claim its slot.
                rejection = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
                    supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None));
                return true;
            },
            CancellationToken.None);

        AssertEx.NotNull(rejection);
        AssertEx.Contains(rejection!.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(expected: 1, launcher.LaunchCount); // model-b never launched; the pinned process survived.
    }

    [Test]
    public async Task Profiling_EnableMetrics_AppendsMetricsToReplaySpawn()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda));

        // A replay profile carries no --metrics; enableMetrics must append it so the benchmark can read /metrics.
        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 20),
            enableMetrics: true,
            (_, _) => Task.FromResult(result: true),
            CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "--metrics");
        AssertEx.Contains(spec.Arguments, "-c"); // replay args still emitted verbatim.
        AssertEx.False(spec.Arguments.Contains("--fit"), "A replay spawn must not emit --fit.");
        AssertEx.Equal(expected: 1, spec.Arguments.Count(a => string.Equals(a, "--metrics", StringComparison.Ordinal)));
    }

    [Test]
    public async Task Profiling_EnableMetrics_DoesNotDuplicate_OnExploreSpawn()
    {
        // Explore mode already emits --metrics; enableMetrics must not append a second copy.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda));

        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: true,
            (_, _) => Task.FromResult(result: true),
            CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal(expected: 1, spec!.Arguments.Count(a => string.Equals(a, "--metrics", StringComparison.Ordinal)));
    }

    [Test]
    public async Task Profiling_ReleasesGateAndEvicts_OnBodyThrow()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            supervisor.RunExclusiveProfilingAsync<bool>("llama3",
                ModelRole.Chat,
                ResolvedLaunchArguments.Explore(),
                enableMetrics: false,
                (_, _) => throw new InvalidOperationException("benchmark boom"),
                CancellationToken.None));

        // The transient profiling process was torn down despite the throw.
        var profilingHandle = launcher.Handles.Single();
        AssertEx.True(profilingHandle.WasTreeKilled, "The profiling process must be evicted when the body throws.");
        AssertEx.Equal(expected: 0, supervisor.CountRunningProcesses());

        // The single-flight gate was released — a subsequent ensure for the same key proceeds (no deadlock).
        var endpoint = await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);
        AssertEx.NotNull(endpoint);
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
    }
}
