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
    public async Task Profiling_AdmittedConflict_ReleasesCapturedLaunchTicket()
    {
        const string modelName = "llama3";
        var registry = new ProcessLaunchAdmissionRegistry();
        var allocation = new ProcessContextAllocation(8192,
            ModelTrainContextTokens: 131072,
            ProcessContextAllocationSource.HardwareTier,
            ProcessPlacementMode.GpuResident,
            ResourceFootprint.Zero,
            ContentIdentity: $"{modelName}:0",
            CacheKey: $"cache:{modelName}");
        using var consumer = registry.Acquire(new ProcessLaunchAdmission(modelName,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            allocation));
        AssertEx.NotNull(consumer);
        await using var supervisor = SupervisorFactory.Create(launchAdmissions: registry);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            supervisor.RunExclusiveProfilingAsync(modelName,
                ModelRole.Chat,
                ResolvedLaunchArguments.Explore(),
                enableMetrics: false,
                (_, _) => Task.FromResult(result: true),
                CancellationToken.None));

        consumer!.Dispose();
        var released = registry.Snapshot(modelName, ModelRole.Chat);
        AssertEx.False(released.HasRequestedKey);
        AssertEx.False(released.HasGlobalBlocker);
        AssertEx.True(registry.TryAcquire(new ProcessLaunchAdmission(modelName,
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            allocation), out var next));
        next!.Dispose();
    }

    [Test]
    public async Task MutationLease_First_BlocksProfilingUntilDisposed()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        var lease = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        AssertEx.NotNull(lease);

        var profiling = supervisor.RunExclusiveProfilingAsync("llama3", ModelRole.Chat, ResolvedLaunchArguments.Explore(), false,
            (_, _) => Task.FromResult(true), CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(profiling, "Profiling must not start while a runtime mutation lease is held.");
        AssertEx.Equal(0, launcher.LaunchCount);

        await (lease ?? throw new InvalidOperationException("lease must not be null.")).DisposeAsync();
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
        await (after ?? throw new InvalidOperationException("after must not be null.")).DisposeAsync();
    }

    [Test]
    public async Task DisposeAsync_CancelsProfilingDelayedBeforeRuntimeGate()
    {
        var launcher = new FakeProcessLauncher();
        var supervisor = SupervisorFactory.Create(launcher);
        var blocker = await supervisor.TryAcquireRuntimeMutationLeaseAsync(CancellationToken.None);
        AssertEx.NotNull(blocker);
        var profiling = supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (_, _) => Task.FromResult(result: true),
            CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(profiling, "Profiling must not start while a runtime mutation lease is held.");

        var disposal = supervisor.DisposeAsync().AsTask();
        await AssertEx.StaysIncompleteAsync(disposal, "Disposal must wait behind the mutation lease the profiling run is parked on.");
        await (blocker ?? throw new InvalidOperationException("blocker must not be null.")).DisposeAsync();

        await AssertEx.ThrowsAsync<ObjectDisposedException>(() => profiling);
        await disposal;
        AssertEx.Equal(0, launcher.LaunchCount);
        AssertEx.Equal(expected: 0, supervisor.CountInflightSpawns());
    }

    [Test]
    public async Task Profiling_Explore_AcquiresMachineReadableFitParamsWithProductionLaunchPolicy()
    {
        var launcher = new FakeProcessLauncher
        {
            StartupLines = ["0.00.539.550 I load_tensors: offloaded 38/49 layers to GPU"]
        };
        var placementReport = new LlamaLayerPlacementReport();
        var fitRunner = new FakeLlamaFitParamsRunner(LlamaFitParamsRunResult.Success(["-c 8192 -ngl 32"]));
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            fitParamsRunner: fitRunner,
            layerPlacementReport: placementReport);

        IReadOnlyList<string> captured = [];
        IReadOnlyList<string> successfulLaunchArguments = [];
        int? capturedProcessId = null;
        LlamaServerLoadObservation? loadObservation = null;
        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (context, _) =>
            {
                captured = context.FitParamsOutput;
                successfulLaunchArguments = context.SuccessfulLaunchArguments;
                capturedProcessId = context.ProcessId;
                loadObservation = context.LoadObservation;
                AssertEx.NotNull(placementReport.Current);
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
        var observedLoad = AssertEx.NotNull(loadObservation);
        AssertEx.Equal(LlamaServerReadinessOutcome.Ready, observedLoad.Outcome);
        AssertEx.Equal(LlamaServerPlacementOutcome.Partial, observedLoad.Placement);
        AssertEx.Equal("test", observedLoad.RuntimeVersion);
        AssertEx.Equal(LlamaServerLoadAttemptKind.Primary, observedLoad.AttemptKind);
        AssertEx.Null(placementReport.Current);
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
        LlamaServerLoadObservation? loadObservation = null;
        await using var supervisor = SupervisorFactory.Create(variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            fitParamsRunner: fitRunner);

        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 32),
            enableMetrics: false,
            (context, _) =>
            {
                loadObservation = context.LoadObservation;
                return Task.FromResult(result: true);
            },
            CancellationToken.None);

        AssertEx.Equal(expected: 0, fitRunner.Calls.Count);
        AssertEx.Equal(LlamaServerLoadAttemptKind.Primary, AssertEx.NotNull(loadObservation).AttemptKind);
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

        await AssertEx.SettleAsync();
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

        // GPU replay specs now emit --metrics natively; enableMetrics stays the guarantee (and appends it for CPU replays).
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
    public async Task Profiling_ReplayWithUnsupportedExactKvFlashVector_RejectsWithoutLaunchingOrMutating()
    {
        const string helpWithoutLongFlashAlias = """
                                                 -m
                                                 --host
                                                 --port
                                                 --parallel
                                                 --no-warmup
                                                 -c
                                                 --metrics
                                                 --n-gpu-layers
                                                 --jinja
                                                 --cache-ram
                                                 -fa [on|off|auto]
                                                 -ctk, --cache-type-k TYPE
                                                     allowed values: f16, q8_0
                                                 -ctv, --cache-type-v TYPE
                                                     allowed values: f16, q8_0
                                                 """;
        var binary = new LlamaBinary("/fake/bin/llama-server", "b10201", GpuVariant.Cuda, IsPinnedFallback: true);
        var manifest = LlamaServerCapabilityManifest.FromSuccessfulProbe(binary,
            executableLengthBytes: 1,
            DateTimeOffset.UnixEpoch,
            executableSha256: new string('A', 64),
            version: "b10201",
            helpWithoutLongFlashAlias);
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            capabilityManifestProbe: new FakeLlamaServerCapabilityManifestProbe(manifest));

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            supervisor.RunExclusiveProfilingAsync("llama3",
                ModelRole.Chat,
                ResolvedLaunchArguments.Replay(ctxSize: 4096,
                    nGpuLayers: 20,
                    kvTypeK: "q8_0",
                    kvTypeV: "q8_0",
                    flashAttn: true),
                enableMetrics: true,
                (_, _) => Task.FromResult(result: true),
                CancellationToken.None));

        AssertEx.Contains(exception.Message, "Recalibrate", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(0, launcher.LaunchCount);
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

    [Test]
    public async Task Profiling_ConcurrentEnsureForSameKey_NeverReusesTheProfilingProcess()
    {
        // Race (A): the profiling process is registered and the exclusive gate is released while the benchmark body
        // still runs, so a chat's fast reuse path could be handed the profiling endpoint — and then killed by the
        // unconditional teardown. The chat must queue behind the per-key single-flight gate and spawn its own process.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int? profilingProcessId = null;
        var profiling = supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            async (context, _) =>
            {
                profilingProcessId = context.ProcessId;
                entered.SetResult();
                await release.Task;
                return true;
            },
            CancellationToken.None);

        await entered.Task;
        var ensure = supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        // No sleep: a reuse of the profiling process completes the ensure synchronously (the reuse probe is
        // rate-limited, so the fast path never awaits), while a correctly refused reuse parks on the gate. Sampled
        // rather than asserted here so a failure cannot strand the barrier and hang the supervisor's disposal.
        var completedDuringProfiling = ensure.IsCompleted;

        release.SetResult();
        await profiling;
        var endpoint = await ensure;

        AssertEx.False(completedDuringProfiling, "A chat must not be handed the profiling process while the benchmark runs.");
        AssertEx.Equal(expected: 2, launcher.LaunchCount); // The chat spawned its own process after teardown.
        var chat = AssertEx.NotNull(supervisor.GetRegisteredProcess("llama3", ModelRole.Chat));
        AssertEx.NotEqual<int?>(profilingProcessId, chat.Handle.ProcessId);
        AssertEx.False(chat.IsProfilingOwned);
        AssertEx.Equal(chat.Endpoint.BaseAddress, endpoint.BaseAddress);
        var handles = launcher.Handles.OrderBy(handle => handle.ProcessId).ToArray();
        AssertEx.True(handles[0].WasTreeKilled, "The profiling process must still be torn down by teardown.");
        AssertEx.False(handles[1].WasTreeKilled, "The chat's own process must survive the profiling teardown.");
    }

    [Test]
    public async Task Profiling_UnpinnedProcess_StillRefusesReuse()
    {
        // The pin is NOT the flag: Pin() runs after registration and Unpin() runs before removal, so a reuse check
        // keyed off IsProfilingPinned leaves both windows open. Clearing the pin emulates both; reuse must still refuse.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int? profilingProcessId = null;
        LlamaServerProcessSupervisor.RunningProcess? registered = null;
        var profiling = supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            async (context, _) =>
            {
                profilingProcessId = context.ProcessId;
                registered = supervisor.GetRegisteredProcess("llama3", ModelRole.Chat);
                registered?.Unpin();
                entered.SetResult();
                await release.Task;
                return true;
            },
            CancellationToken.None);

        await entered.Task;
        var ensure = supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);
        var completedDuringProfiling = ensure.IsCompleted;

        release.SetResult();
        await profiling;
        var endpoint = await ensure;

        AssertEx.False(AssertEx.NotNull(registered, "The profiling process must be registered while its body runs.").IsProfilingPinned);
        AssertEx.False(completedDuringProfiling, "An unpinned profiling process must still be refused for reuse.");
        AssertEx.Equal(expected: 2, launcher.LaunchCount);
        AssertEx.NotEqual<int?>(profilingProcessId, AssertEx.NotNull(supervisor.GetRegisteredProcess("llama3", ModelRole.Chat)).Handle.ProcessId);
        AssertEx.Equal(AssertEx.NotNull(supervisor.GetRegisteredProcess("llama3", ModelRole.Chat)).Endpoint.BaseAddress, endpoint.BaseAddress);
    }

    [Test]
    public async Task Profiling_InferenceLease_IsRefusedForTheProfilingProcess()
    {
        // Chat paths ensure first and look the lease up by key afterwards, so a chat whose own process was replaced in
        // between finds the profiling process under the same key. A lease there would be killed by profiling teardown.
        await using var supervisor = SupervisorFactory.Create();

        var acquisition = LlamaServerLeaseAcquisition.Evicting;
        await supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (_, _) =>
            {
                acquisition = supervisor.TryAcquireInferenceLease("llama3", ModelRole.Chat);
                return Task.FromResult(result: true);
            },
            CancellationToken.None);

        AssertEx.Null(acquisition.Lease, "Inference must never lease the transient profiling process.");
        AssertEx.False(acquisition.ProcessEvicting, "The refusal is profiling ownership, not a draining eject.");
        AssertEx.True(acquisition.ProcessProfiling,
            "Reported as its own refusal: 'not running' would license the caller to proceed leaseless on its cached endpoint.");
    }

    [Test]
    public async Task Profiling_UnpinnedProcess_NotEvictedByCapAdmission()
    {
        // The registration-to-Pin() window again, this time against the LRU victim scan: with the pin cleared, only
        // IsProfilingOwned keeps the measurement process out of the victim set for a cap-hitting competing spawn.
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
                AssertEx.NotNull(supervisor.GetRegisteredProcess("model-a", ModelRole.Chat)).Unpin();
                time.Advance(ttl + TimeSpan.FromMinutes(1));
                rejection = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
                    supervisor.EnsureRunningAsync("model-b", ModelRole.Chat, CancellationToken.None));
                return true;
            },
            CancellationToken.None);

        AssertEx.NotNull(rejection);
        AssertEx.Contains(rejection!.Message, "maximum number of local models", StringComparison.OrdinalIgnoreCase);
        AssertEx.Equal(expected: 1, launcher.LaunchCount); // model-b never launched; the profiling process survived.
    }

    [Test]
    public async Task Profiling_PreSpawnEviction_RefusesWhenTheModelIsServingInference()
    {
        // Race (B): the pre-spawn eviction ran straight into RemoveProcessAsync with no lease check, tree-killing a
        // chat that acquired its lease after the caller's busy check. It must refuse instead, having evicted nothing.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        var acquisition = supervisor.TryAcquireInferenceLease("llama3", ModelRole.Chat);
        using var lease = AssertEx.NotNull(acquisition.Lease);

        var bodyRan = false;
        var refusal = await AssertEx.ThrowsAsync<LlamaServerProfilingRefusedException>(() =>
            supervisor.RunExclusiveProfilingAsync("llama3",
                ModelRole.Chat,
                ResolvedLaunchArguments.Explore(),
                enableMetrics: false,
                (_, _) =>
                {
                    bodyRan = true;
                    return Task.FromResult(result: true);
                },
                CancellationToken.None));

        AssertEx.Equal(ModelRole.Chat, refusal.Role);
        AssertEx.Equal(expected: 1, refusal.ActiveLeases);
        AssertEx.False(bodyRan, "The profiling body must not run when the pre-spawn eviction was refused.");
        AssertEx.Equal(expected: 1, launcher.LaunchCount); // No profiling spawn.
        AssertEx.False(launcher.Handles.Single().WasTreeKilled, "The chat's process must survive a refused eviction.");
        AssertEx.False(AssertEx.NotNull(supervisor.GetRegisteredProcess("llama3", ModelRole.Chat)).IsEvicting);
        AssertEx.False(lease.WasEjected, "The chat's lease must stay valid.");

        // The refusal left the process leasable: the claim was released, not stranded.
        var next = supervisor.TryAcquireInferenceLease("llama3", ModelRole.Chat);
        AssertEx.False(next.ProcessEvicting);
        AssertEx.NotNull(next.Lease).Dispose();
    }

    [Test]
    public async Task Profiling_PreSpawnEviction_WhenAnotherTeardownOwnsTheProcess_ReportsThatReason()
    {
        // The other TryBeginEvict failure: the compare-exchange lost to a teardown that already owns the process.
        // There is no lease count to report there, so the reason carries the meaning instead of a made-up number.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);
        _ = AssertEx.NotNull(supervisor.GetRegisteredProcess("llama3", ModelRole.Chat)).MarkEvicting();

        var refusal = await AssertEx.ThrowsAsync<LlamaServerProfilingRefusedException>(() =>
            supervisor.RunExclusiveProfilingAsync("llama3",
                ModelRole.Chat,
                ResolvedLaunchArguments.Explore(),
                enableMetrics: false,
                (_, _) => Task.FromResult(result: true),
                CancellationToken.None));

        AssertEx.Equal(LlamaServerProfilingRefusalReason.EvictionAlreadyInProgress, refusal.Reason);
        AssertEx.Equal(expected: 0, refusal.ActiveLeases);
        AssertEx.Contains(refusal.Message, "already being torn down", StringComparison.Ordinal);
        AssertEx.Equal(expected: 1, launcher.LaunchCount); // No profiling spawn.
    }

    [Test]
    public async Task Profiling_PreSpawnEviction_RollsBackEarlierClaims_WhenALaterRoleRefuses()
    {
        // Two-phase: the eviction loops every role, so a per-role claim-and-remove would tear down Chat and only then
        // discover Embedding is leased — leaving the model half evicted for a run that never happens.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Embedding, CancellationToken.None);

        using var lease = AssertEx.NotNull(supervisor.TryAcquireInferenceLease("llama3", ModelRole.Embedding).Lease);

        var refusal = await AssertEx.ThrowsAsync<LlamaServerProfilingRefusedException>(() =>
            supervisor.RunExclusiveProfilingAsync("llama3",
                ModelRole.Chat,
                ResolvedLaunchArguments.Explore(),
                enableMetrics: false,
                (_, _) => Task.FromResult(result: true),
                CancellationToken.None));

        AssertEx.Equal(ModelRole.Embedding, refusal.Role);
        AssertEx.Equal(expected: 2, launcher.LaunchCount); // Neither warm role was replaced by a profiling spawn.
        AssertEx.Empty(launcher.Handles.Where(handle => handle.WasTreeKilled));

        var chat = AssertEx.NotNull(supervisor.GetRegisteredProcess("llama3", ModelRole.Chat));
        AssertEx.False(chat.IsEvicting, "The first role's claim must be released, not stranded.");
        AssertEx.NotNull(supervisor.TryAcquireInferenceLease("llama3", ModelRole.Chat).Lease).Dispose();
    }

    [Test]
    public async Task EvictionClaim_RolledBack_LeavesAnotherOwnersMarkStanding()
    {
        // The interleaving the two-phase rollback has to survive: profiling claims an earlier role, an operator eject
        // starts on that same role and takes the mark over, and only then does a later busy role make profiling roll
        // back. Driven at the claim primitive because the claim loop has no await for a second thread to interleave at.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);
        var chat = AssertEx.NotNull(supervisor.GetRegisteredProcess("llama3", ModelRole.Chat));

        AssertEx.True(chat.TryBeginEvict(forProfiling: true, out var profilingClaim));
        var ejectClaim = chat.MarkEvicting();
        chat.ReleaseEvictionClaim(profilingClaim);

        AssertEx.True(chat.IsEvicting, "An operator eject's mark must survive another claimant's rollback.");
        AssertEx.True(supervisor.TryAcquireInferenceLease("llama3", ModelRole.Chat).ProcessEvicting,
            "A lease granted here would be killed by the eject's own teardown.");

        // The owning eject still clears its own mark, so a drain that timed out leaves the process leasable.
        chat.ReleaseEvictionClaim(ejectClaim);
        AssertEx.False(chat.IsEvicting);
        AssertEx.NotNull(supervisor.TryAcquireInferenceLease("llama3", ModelRole.Chat).Lease).Dispose();
    }

    [Test]
    public async Task EvictionClaim_TakenByProfiling_RefusesLeasesAsProfilingRatherThanAsAnOperatorEject()
    {
        // The claim-to-remove window. Reported as a plain eject, a chat here fails terminally with "the model is being
        // ejected by the operator" for a benchmark spawn that clears itself in seconds.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);
        var chat = AssertEx.NotNull(supervisor.GetRegisteredProcess("llama3", ModelRole.Chat));

        AssertEx.True(chat.TryBeginEvict(forProfiling: true, out var claim));

        var refused = supervisor.TryAcquireInferenceLease("llama3", ModelRole.Chat);
        AssertEx.Null(refused.Lease);
        AssertEx.True(refused.ProcessProfiling, "A profiling claim must refuse as profiling, not as an operator eject.");
        AssertEx.False(refused.ProcessEvicting);

        // Releasing the profiling claim restores normal leasing.
        chat.ReleaseEvictionClaim(claim);
        AssertEx.NotNull(supervisor.TryAcquireInferenceLease("llama3", ModelRole.Chat).Lease).Dispose();
    }

    [Test]
    public async Task Profiling_MidRemoval_RefusesAStillClaimedRoleAsProfilingRatherThanAsAnOperatorEject()
    {
        // The real claim-to-remove window, through TryEvictAllRolesForProfilingAsync: it claims EVERY role first, then
        // removes them one at a time. While the first removal is held open, the second role is still registered and
        // still claimed — and a chat landing there must be told a benchmark is spawning, not that the operator is
        // ejecting the model (which is terminal and never retried).
        using var firstKillReached = new ManualResetEventSlim(initialState: false);
        using var releaseFirstKill = new ManualResetEventSlim(initialState: false);
        var launches = 0;
        var launcher = new FakeProcessLauncher(_ => Interlocked.Increment(ref launches) == 1
            ? new FakeProcessHandle(pid: 5000, exitOnTreeKill: true, () =>
            {
                firstKillReached.Set();
                releaseFirstKill.Wait(TimeSpan.FromSeconds(10));
            })
            : new FakeProcessHandle(pid: 5000 + launches));
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Embedding, CancellationToken.None);

        var profiling = Task.Run(() => supervisor.RunExclusiveProfilingAsync("llama3",
            ModelRole.Chat,
            ResolvedLaunchArguments.Explore(),
            enableMetrics: false,
            (_, _) => Task.FromResult(result: true),
            CancellationToken.None));

        AssertEx.True(firstKillReached.Wait(TimeSpan.FromSeconds(10)), "The first role's teardown must be reached.");

        // Chat is already detached; Embedding is claimed and still registered — the window under test.
        var refused = supervisor.TryAcquireInferenceLease("llama3", ModelRole.Embedding);
        releaseFirstKill.Set();
        await profiling;

        AssertEx.Null(refused.Lease);
        AssertEx.True(refused.ProcessProfiling, "A role claimed by profiling must refuse as profiling, not as an operator eject.");
        AssertEx.False(refused.ProcessEvicting);
        AssertEx.Null(supervisor.GetRegisteredProcess("llama3", ModelRole.Embedding), "Every claimed role is torn down.");
    }

    [Test]
    public async Task EvictionClaim_TakenByAnOperatorEject_StillRefusesLeasesAsEvicting()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher);
        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);
        var chat = AssertEx.NotNull(supervisor.GetRegisteredProcess("llama3", ModelRole.Chat));

        _ = chat.MarkEvicting();

        var refused = supervisor.TryAcquireInferenceLease("llama3", ModelRole.Chat);
        AssertEx.True(refused.ProcessEvicting, "An operator eject must still be reported as one.");
        AssertEx.False(refused.ProcessProfiling);
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
