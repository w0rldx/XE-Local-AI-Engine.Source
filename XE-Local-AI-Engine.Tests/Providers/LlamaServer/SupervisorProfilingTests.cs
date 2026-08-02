namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Coverage for the operator profiling seam (<see cref="LlamaServerProcessSupervisor.RunExclusiveProfilingAsync{T}" />):
///     it acquires machine-readable fit output before an explore spawn, evicts every warm role for the model so the spawn is
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
    public async Task Profiling_Explore_AcquiresMachineReadableFitParamsWithProductionLaunchPolicy()
    {
        var launcher = new FakeProcessLauncher();
        var fitRunner = new FakeLlamaFitParamsRunner(LlamaFitParamsRunResult.Success(["-c 8192 -ngl 32"]));
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            fitParamsRunner: fitRunner);

        IReadOnlyList<string> captured = [];
        IReadOnlyList<string> successfulLaunchArguments = [];
        int? capturedProcessId = null;
        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (context, _) =>
            {
                captured = context.FitParamsOutput;
                successfulLaunchArguments = context.SuccessfulLaunchArguments;
                capturedProcessId = context.ProcessId;
                return Task.FromResult(result: true);
            },
            CancellationToken.None);

        AssertEx.Contains(captured, "-c 8192 -ngl 32");
        AssertEx.Equal<int?>(launcher.Handles.Single().ProcessId, capturedProcessId);
        AssertEx.Equal(expected: 1, fitRunner.Calls.Count);
        AssertEx.True(fitRunner.Calls.TryPeek(out var spec));
        AssertEx.Contains(spec!.Arguments, "--fit");
        AssertEx.Contains(spec.Arguments, "-v");
        AssertArgumentValue(spec.Arguments, "-c", "16384");
        AssertArgumentValue(spec.Arguments, "-ctk", "q8_0");
        AssertArgumentValue(spec.Arguments, "-ctv", "q8_0");
        AssertArgumentValue(spec.Arguments, "-fa", "on");
        AssertEx.True(launcher.Launches.TryPeek(out var launchedSpec));
        AssertEx.True(spec.Arguments.SequenceEqual(launchedSpec!.Arguments),
            "The helper and profiling server must receive the same production-equivalent launch vector.");
        AssertEx.True(successfulLaunchArguments.SequenceEqual(launchedSpec.Arguments),
            "The profiling body must receive the exact successful server vector so replay preserves its policy.");
    }

    [Test]
    [Arguments(ModelRole.Embedding, "--embeddings")]
    [Arguments(ModelRole.Reranker, "--rerank")]
    public async Task Profiling_Explore_AuxiliaryRolesKeepPoolingOnServerButNotFitHelper(ModelRole role,
        string roleFlag)
    {
        var launcher = new FakeProcessLauncher();
        var fitRunner = new FakeLlamaFitParamsRunner(LlamaFitParamsRunResult.Success(["-c 2048 -ngl 32"]));
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            fitParamsRunner: fitRunner);

        await supervisor.RunExclusiveProfilingAsync("llama3",
            role,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (_, _) => Task.FromResult(result: true),
            CancellationToken.None);

        AssertEx.True(fitRunner.Calls.TryPeek(out var serverSpec));
        AssertEx.Contains(serverSpec!.Arguments, roleFlag);
        AssertArgumentValue(serverSpec.Arguments,
            "--pooling",
            role == ModelRole.Reranker ? "rank" : "mean");

        var fitArguments = LlamaFitParamsProcessRunner.BuildArguments(serverSpec.Arguments);
        AssertEx.Contains(fitArguments, "--fit");
        AssertEx.False(fitArguments.Contains(roleFlag));
        AssertEx.False(fitArguments.Contains("--pooling"),
            "server-only pooling must never be projected to pinned b9692 llama-fit-params");

        AssertEx.True(launcher.Launches.TryPeek(out var launchedSpec));
        AssertEx.True(serverSpec.Arguments.SequenceEqual(launchedSpec!.Arguments),
            "the server must retain its role and pooling flags even though the helper projection filters them");
    }

    [Test]
    public async Task Profiling_Explore_WhenOptimizedCandidateFails_ExposesSuccessfulSafeLaunchArguments()
    {
        var launcher = new FakeProcessLauncher();
        var fitRunner = new FakeLlamaFitParamsRunner(LlamaFitParamsRunResult.Success(["-c 8192 -ngl 32"]));
        await using var supervisor = SupervisorFactory.Create(launcher,
            healthProbe: new FirstReadinessFailsHealthProbe(),
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            fitParamsRunner: fitRunner);

        IReadOnlyList<string> successfulLaunchArguments = [];
        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (context, _) =>
            {
                successfulLaunchArguments = context.SuccessfulLaunchArguments;
                return Task.FromResult(result: true);
            },
            CancellationToken.None);

        AssertEx.Equal(expected: 2, fitRunner.Calls.Count);
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.True(launcher.Launches.TryDequeue(out var optimized));
        AssertEx.Contains(optimized!.Arguments, "-ctk");
        AssertEx.True(launcher.Launches.TryDequeue(out var safe));
        AssertEx.False(safe!.Arguments.Contains("-ctk"));
        AssertEx.False(safe.Arguments.Contains("-ctv"));
        AssertEx.False(safe.Arguments.Contains("-fa"));
        AssertEx.True(successfulLaunchArguments.SequenceEqual(safe.Arguments),
            "The failed optimized candidate's KV/FA policy must not leak into the safe replay profile.");
    }

    [Test]
    public async Task Profiling_Replay_DoesNotInvokeFitParamsCapability()
    {
        var fitRunner = new FakeLlamaFitParamsRunner(LlamaFitParamsRunResult.Success(["-c 8192 -ngl 32"]));
        await using var supervisor = SupervisorFactory.Create(variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
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
    public async Task Profiling_EvictsAllWarmRolesForModel_BeforeVramCapture()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        // Warm processes for both the target role and a sibling role of the same model are running before profiling.
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);
        var chatHandle = launcher.Handles.Single();
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Embedding, CancellationToken.None);
        var embeddingHandle = launcher.Handles.Single(handle => !ReferenceEquals(handle, chatHandle));

        var chatEvictedAtCapture = false;
        var embeddingEvictedAtCapture = false;
        var launchCountAtCapture = -1;
        LlamaServerProfilingVramSnapshot? capturedPreSpawnVram = null;
        var launchCountAtBody = 0;
        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (context, _) =>
            {
                capturedPreSpawnVram = context.PreSpawnVram;
                launchCountAtBody = launcher.LaunchCount;
                return Task.FromResult(result: true);
            },
            CancellationToken.None,
            _ =>
            {
                chatEvictedAtCapture = chatHandle.WasTreeKilled;
                embeddingEvictedAtCapture = embeddingHandle.WasTreeKilled;
                launchCountAtCapture = launcher.LaunchCount;
                return Task.FromResult(new LlamaServerProfilingVramSnapshot(6, 8));
            });

        AssertEx.True(chatEvictedAtCapture, "The target-role warm process must be evicted before ambient VRAM is captured.");
        AssertEx.True(embeddingEvictedAtCapture, "Every sibling-role warm process for the model must be evicted before ambient VRAM is captured.");
        AssertEx.Equal(expected: 2, launchCountAtCapture); // only the two warm processes have launched at capture time.
        AssertEx.Equal(expected: 3, launchCountAtBody); // two warm processes + exclusive profiling spawn.
        AssertEx.Equal(new LlamaServerProfilingVramSnapshot(6, 8), capturedPreSpawnVram);
    }

    [Test]
    public async Task Profiling_AwaitsInflightSiblingRole_ThenEvictsItBeforeVramCapture()
    {
        var launcher = new FakeProcessLauncher();
        var healthProbe = new GatedHealthProbe();
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe);

        var siblingEnsure = supervisor.EnsureRunningAsync("llama3", ModelRole.Embedding, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => healthProbe.Waiting == 1,
            TimeSpan.FromSeconds(5),
            "The sibling-role spawn never reached its blocked readiness probe.");

        var captureEntered = false;
        var siblingEvictedAtCapture = false;
        var profiling = supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (_, _) => Task.FromResult(result: true),
            CancellationToken.None,
            _ =>
            {
                captureEntered = true;
                siblingEvictedAtCapture = launcher.Handles.Single().WasTreeKilled;
                return Task.FromResult(new LlamaServerProfilingVramSnapshot(6, 8));
            });

        await Task.Delay(50);
        AssertEx.False(captureEntered, "VRAM capture must wait for an already-started sibling-role spawn to settle.");

        healthProbe.Release();
        await siblingEnsure;
        await profiling;

        AssertEx.True(captureEntered);
        AssertEx.True(siblingEvictedAtCapture, "The settled sibling-role process must be evicted before ambient VRAM is captured.");
        AssertEx.Equal(expected: 2, launcher.LaunchCount); // sibling role + exclusive profiling spawn.
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

    private static void AssertArgumentValue(IReadOnlyList<string> arguments, string argument, string expectedValue)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], argument, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        AssertEx.True(index >= 0 && index + 1 < arguments.Count, $"Expected argument '{argument}' with a value.");
        AssertEx.Equal(expectedValue, arguments[index + 1]);
    }

    private sealed class FirstReadinessFailsHealthProbe : ILlamaServerHealthProbe
    {
        private int _readinessCalls;

        public Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
        {
            return Task.FromResult(Interlocked.Increment(ref _readinessCalls) > 1);
        }

        public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
        {
            return Task.FromResult(true);
        }

        public Task<int?> TryReadEffectiveContextTokensAsync(Uri baseAddress, CancellationToken ct)
        {
            return Task.FromResult<int?>(null);
        }
    }
}
