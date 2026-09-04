namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Globalization;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the supervisor's launch argument vector always carries the mandatory role flags — chat →
///     <c>--jinja</c>; embedding → <c>--embeddings</c> + a non-<c>none</c> <c>--pooling</c> value — and always binds
///     localhost only. Verified against the pinned llama.cpp release <c>b10201</c> flag names (<c>--jinja</c>,
///     <c>--embeddings</c>, <c>--pooling mean|cls|last</c>).
/// </summary>
public sealed class SupervisorSpawnArgsTests
{
    [Test]
    public async Task EnsureRunning_RuntimeMissingMandatoryCapability_FailsBeforeLauncher()
    {
        const string helpWithoutNoWarmup = """
                                           -m, --model FNAME
                                           --host HOST
                                           --port PORT
                                           --parallel N
                                           -c, --ctx-size N
                                           -t, --threads N
                                           -tb, --threads-batch N
                                           --jinja
                                           --cache-ram N
                                           """;
        var binary = new LlamaBinary("/fake/bin/llama-server", "b10201", GpuVariant.Cpu, IsPinnedFallback: true);
        var manifest = LlamaServerCapabilityManifest.FromSuccessfulProbe(binary,
            executableLengthBytes: 1,
            DateTimeOffset.UnixEpoch,
            executableSha256: new string('a', 64),
            version: "b10201",
            helpWithoutNoWarmup);
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            capabilityManifestProbe: new FakeLlamaServerCapabilityManifestProbe(manifest));

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None));

        AssertEx.Contains(exception.Message, "--no-warmup");
        AssertEx.Equal(0, launcher.LaunchCount);
    }

    [Test]
    public async Task EnsureRunning_ChatRole_LaunchArgsContainJinja_AndBindLocalhost()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = NewSupervisor(launcher);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "--jinja");
        AssertEx.False(spec.Arguments.Contains("--embeddings"), "Chat process must not enable embeddings.");
        AssertChatBindsLocalhost(spec);
    }

    [Test]
    public async Task EnsureRunning_EmbeddingRole_LaunchArgsContainEmbeddingsAndNonNonePooling()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = NewSupervisor(launcher);

        await supervisor.EnsureRunningAsync("nomic-embed", ModelRole.Embedding, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "--embeddings");
        AssertEx.Contains(spec.Arguments, "--pooling");

        var poolingIndex = IndexOf(spec.Arguments, "--pooling");
        var poolingValue = spec.Arguments[poolingIndex + 1];
        AssertEx.Contains(new[]
        {
            "mean",
            "cls",
            "last"
        }, poolingValue);
        AssertEx.False(spec.Arguments.Contains("--jinja"), "Embedding process must not enable jinja chat templating.");
        AssertEx.False(string.Equals(poolingValue, "none", StringComparison.OrdinalIgnoreCase), "Pooling must not be none.");
    }

    [Test]
    public async Task EnsureRunning_RerankerRole_LaunchArgsContainRerankAndPoolingRank_NotEmbeddings()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = NewSupervisor(launcher);

        await supervisor.EnsureRunningAsync("bge-reranker-v2-m3", ModelRole.Reranker, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "--rerank");
        AssertEx.Contains(spec.Arguments, "--pooling");

        var poolingIndex = IndexOf(spec.Arguments, "--pooling");
        AssertEx.Equal("rank", spec.Arguments[poolingIndex + 1]);

        // --rerank is mutually exclusive with --embeddings, and carries none of the chat-only flags.
        AssertEx.False(spec.Arguments.Contains("--embeddings"), "A rerank process must not enable embeddings.");
        AssertEx.False(spec.Arguments.Contains("--jinja"), "A rerank process must not enable jinja chat templating.");
    }

    [Test]
    public async Task EnsureRunning_GpuVariant_NoProfile_LaunchArgsEmitFitOnAndMetrics()
    {
        var launcher = new FakeProcessLauncher();
        // The default resolver returns explore-mode (no frozen profile), so a GPU spawn lets llama.cpp auto-fit drive
        // placement: --fit on + --metrics, NEVER the old forced --n-gpu-layers 999.
        await using var supervisor = SupervisorFactory.Create(launcher, variantSelector: new FakeVariantSelector(GpuVariant.Cuda));

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "--fit");
        var fitIndex = IndexOf(spec.Arguments, "--fit");
        AssertEx.Equal("on", spec.Arguments[fitIndex + 1]);
        AssertEx.Contains(spec.Arguments, "--metrics");
        AssertEx.False(spec.Arguments.Contains("--n-gpu-layers"), "Explore mode must not emit an explicit -ngl (it disables auto-fit).");
        AssertEx.False(spec.Arguments.Contains("999"), "The forced -ngl 999 placement is removed.");

        // The policy adds the deterministic chat context and the KV-cache quant + flash-attention optimization.
        var ctxIndex = IndexOf(spec.Arguments, "-c");
        AssertEx.Equal(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens.ToString(CultureInfo.InvariantCulture), spec.Arguments[ctxIndex + 1]);
        AssertEx.Contains(spec.Arguments, "-fa");
        AssertEx.Equal("on", spec.Arguments[IndexOf(spec.Arguments, "-fa") + 1]);
        AssertEx.Equal("q8_0", spec.Arguments[IndexOf(spec.Arguments, "-ctk") + 1]);
        AssertEx.Equal("q8_0", spec.Arguments[IndexOf(spec.Arguments, "-ctv") + 1]);
        AssertEx.False(spec.Arguments.Contains("-t"), "A full-GPU spawn must not emit CPU thread flags.");
    }

    [Test]
    public async Task EnsureRunning_GpuExploreWithExpertOffloadPlacement_EmitsCpuMoeBesideAutoFit()
    {
        var launcher = new FakeProcessLauncher();
        var allocation = new ProcessContextAllocation(ProcessContextTokens: 8192,
            ModelTrainContextTokens: null,
            ProcessContextAllocationSource.HardwareTier,
            ProcessPlacementMode.ExpertOffload,
            ResourceFootprint.Zero,
            ContentIdentity: "moe-model:0",
            CacheKey: "moe-cache");
        var allocationResolver = Substitute.For<IProcessContextAllocationResolver>();
        allocationResolver.ResolveAsync(Arg.Any<string>(),
                              Arg.Any<ModelRole>(),
                              Arg.Any<GpuVariant>(),
                              Arg.Any<ResolvedLaunchArguments>(),
                              Arg.Any<CancellationToken>())
                          .Returns(_ => Task.FromResult<ProcessContextAllocation?>(allocation));
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            allocationResolver: allocationResolver);

        await supervisor.EnsureRunningAsync("moe-model", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "--cpu-moe");
        // --fit adjusts only UNSET arguments, so auto-fit sizes placement around the flag rather than overriding it.
        AssertEx.Contains(spec.Arguments, "--fit");
        AssertEx.False(spec.Arguments.Contains("-ot"), "an explore spawn never emits a frozen -ot beside --cpu-moe.");
        AssertEx.False(spec.Arguments.Contains("--n-cpu-moe"), "the estimator offloads the whole expert share, so no partial N is derived.");
    }

    [Test]
    public async Task EnsureRunning_UsesAdmissionAdjustedAllocationContext()
    {
        var launcher = new FakeProcessLauncher();
        var initial = new ProcessContextAllocation(ProcessContextTokens: 65536,
            ModelTrainContextTokens: 131072,
            ProcessContextAllocationSource.HardwareTier,
            ProcessPlacementMode.GpuResident,
            ResourceFootprint.Zero,
            ContentIdentity: "llama3:0",
            CacheKey: "cache");
        var selected = initial with
        {
            ProcessContextTokens = 16384
        };
        var stored = initial;
        var allocationResolver = Substitute.For<IProcessContextAllocationResolver>();
        allocationResolver.ResolveAsync(Arg.Any<string>(),
                              Arg.Any<ModelRole>(),
                              Arg.Any<GpuVariant>(),
                              Arg.Any<ResolvedLaunchArguments>(),
                              Arg.Any<CancellationToken>())
                          .Returns(_ => Task.FromResult<ProcessContextAllocation?>(stored));
        allocationResolver.TryCommitAdmissionAllocation(selected, out Arg.Any<ProcessContextAllocation>())
                          .Returns(call =>
                          {
                              stored = selected;
                              call[1] = stored;
                              return true;
                          });
        allocationResolver.TryGetEffectiveCommittedAllocation(Arg.Any<ProcessContextAllocation>(),
                              out Arg.Any<ProcessContextAllocation>())
                          .Returns(call =>
                          {
                              call[1] = stored;
                              return true;
                          });
        AssertEx.True(allocationResolver.TryCommitAdmissionAllocation(selected, out var committed));
        AssertEx.Equal(selected, committed);
        var launchAdmissions = new ProcessLaunchAdmissionRegistry();
        var admission = new ProcessLaunchAdmission("llama3",
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            committed);
        AssertEx.True(launchAdmissions.TryAcquire(admission, out var consumer));
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            profileResolver: new FakeInferenceProfileResolver(ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 8)),
            allocationResolver: allocationResolver,
            launchAdmissions: launchAdmissions);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal("16384", spec!.Arguments[IndexOf(spec.Arguments, "-c") + 1]);
        AssertEx.Contains(spec.Arguments, "--fit");
        AssertEx.False(spec.Arguments.Contains("--n-gpu-layers"), "the post-admission frozen profile must not replace admitted Explore args");
        consumer!.Dispose();
    }

    /// <summary>
    ///     The ticket belongs to the variant it was granted against. A recorded source build can move the spawn off that
    ///     variant after the identity check has passed, and then nothing the ticket carries fits: its arguments were
    ///     resolved for another backend, and its allocation was sized for one.
    /// </summary>
    [Test]
    public async Task EnsureRunning_AdmittedVariantOutrankedByServedBuild_ReResolvesArgs()
    {
        var launcher = new FakeProcessLauncher();
        var allocation = new ProcessContextAllocation(16384,
            ModelTrainContextTokens: 131072,
            ProcessContextAllocationSource.HardwareTier,
            ProcessPlacementMode.GpuResident,
            ResourceFootprint.Zero,
            "llama3:0",
            CacheKey: "cache");
        var launchAdmissions = new ProcessLaunchAdmissionRegistry();

        // Admitted as Vulkan with Explore args; the serve hands back the recorded CUDA build instead.
        var admission = new ProcessLaunchAdmission("llama3",
            ModelRole.Chat,
            GpuVariant.Vulkan,
            ResolvedLaunchArguments.Explore(),
            allocation);
        AssertEx.True(launchAdmissions.TryAcquire(admission, out var consumer));
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Vulkan),
            profileResolver: new FakeInferenceProfileResolver(ResolvedLaunchArguments.Replay(ctxSize: 4096, nGpuLayers: 8)),
            launchAdmissions: launchAdmissions,
            binaryManager: new FakeBinaryManager(GpuVariant.Cuda));

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal("8", spec!.Arguments[IndexOf(spec.Arguments, "--n-gpu-layers") + 1]);
        AssertEx.False(spec.Arguments.Contains("--fit"), "The admitted Explore args were resolved for a variant this spawn is no longer on.");
        consumer!.Dispose();
    }

    /// <summary>
    ///     A CPU admission carries no GPU bytes and a CPU-sized context. Spending it on a served GPU build would put
    ///     that load outside VRAM capacity accounting, so the allocation is re-resolved for the variant being launched.
    /// </summary>
    [Test]
    public async Task EnsureRunning_AdmittedCpuAllocationOutrankedByServedBuild_ReResolvesAllocation()
    {
        var launcher = new FakeProcessLauncher();
        var cpuAllocation = new ProcessContextAllocation(4096,
            ModelTrainContextTokens: 131072,
            ProcessContextAllocationSource.HardwareTier,
            ProcessPlacementMode.Cpu,
            ResourceFootprint.Zero,
            "llama3:0",
            CacheKey: "cpu-cache");
        var gpuAllocation = cpuAllocation with
        {
            ProcessContextTokens = 32768,
            Placement = ProcessPlacementMode.GpuResident,
            CacheKey = "gpu-cache"
        };
        var allocationResolver = Substitute.For<IProcessContextAllocationResolver>();
        allocationResolver.ResolveAsync(Arg.Any<string>(),
                              Arg.Any<ModelRole>(),
                              Arg.Any<GpuVariant>(),
                              Arg.Any<ResolvedLaunchArguments>(),
                              Arg.Any<CancellationToken>())
                          .Returns(_ => Task.FromResult<ProcessContextAllocation?>(gpuAllocation));

        // Admitted as a CPU launch; the serve hands back the recorded CUDA build instead. Reusing the CPU allocation
        // would go through TryGetEffectiveCommittedAllocation, which this resolver never satisfies.
        var launchAdmissions = new ProcessLaunchAdmissionRegistry();
        var admission = new ProcessLaunchAdmission("llama3",
            ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            cpuAllocation);
        AssertEx.True(launchAdmissions.TryAcquire(admission, out var consumer));
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            allocationResolver: allocationResolver,
            launchAdmissions: launchAdmissions,
            binaryManager: new FakeBinaryManager(GpuVariant.Cuda));

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal("32768", spec!.Arguments[IndexOf(spec.Arguments, "-c") + 1]);
        await allocationResolver.Received(1).ResolveAsync(Arg.Any<string>(),
            Arg.Any<ModelRole>(),
            GpuVariant.Cuda,
            Arg.Any<ResolvedLaunchArguments>(),
            Arg.Any<CancellationToken>());
        consumer!.Dispose();
    }

    [Test]
    [Arguments(GpuVariant.Vulkan, "llama3:0")]
    [Arguments(GpuVariant.Cuda, "different-content")]
    public async Task EnsureRunning_AdmittedIdentityMismatch_FailsBeforeLauncher(GpuVariant actualVariant,
        string contentIdentity)
    {
        var launcher = new FakeProcessLauncher();
        var registry = new ProcessLaunchAdmissionRegistry();
        var allocation = new ProcessContextAllocation(8192,
            ModelTrainContextTokens: 131072,
            ProcessContextAllocationSource.HardwareTier,
            ProcessPlacementMode.GpuResident,
            ResourceFootprint.Zero,
            contentIdentity,
            CacheKey: "cache");
        using var consumer = registry.Acquire(new ProcessLaunchAdmission("llama3",
            ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            allocation));
        AssertEx.NotNull(consumer);
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(actualVariant),
            launchAdmissions: registry);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None));

        AssertEx.Equal(0, launcher.LaunchCount);
        consumer!.Dispose();
    }

    [Test]
    public async Task EnsureRunning_CpuVariant_EmitsContextAndThreads_ButNoGpuOrKvArgs()
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(),
            launchPolicyOptions: new LlamaServerLaunchPolicyOptions
            {
                // Pin explicit thread counts so the assertion is deterministic across CI host core counts.
                CpuThreadCount = 6,
                CpuThreadsBatchCount = 8
            });

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.False(spec!.Arguments.Contains("--n-gpu-layers"), "The CPU variant must not request GPU layer offload.");
        AssertEx.False(spec.Arguments.Contains("--fit"), "The CPU variant must not emit auto-fit args.");
        AssertEx.False(spec.Arguments.Contains("--metrics"), "The CPU variant must not emit --metrics.");
        AssertEx.False(spec.Arguments.Contains("-ctk"), "The CPU variant keeps f16 KV — no -ctk.");

        // The CPU variant DOES get a deterministic -c (previously it emitted none → full-train-ctx KV in RAM).
        var ctxIndex = IndexOf(spec.Arguments, "-c");
        AssertEx.Equal(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens.ToString(CultureInfo.InvariantCulture), spec.Arguments[ctxIndex + 1]);

        // The CPU thread policy.
        AssertEx.Equal("6", spec.Arguments[IndexOf(spec.Arguments, "-t") + 1]);
        AssertEx.Equal("8", spec.Arguments[IndexOf(spec.Arguments, "-tb") + 1]);
    }

    /// <summary>
    ///     Placement must follow the binary actually launched, not the selector. A recorded managed source build is
    ///     authoritative and is served even when the requested variant disagrees — which happens whenever the cached
    ///     signal has not been seeded yet (a spawn beating startup, or a build another checkout adopted). Every GPU
    ///     argument is gated on <c>variant != Cpu</c> in <see cref="LlamaServerLaunchProjection" />, so keying the spawn
    ///     off the selector runs a CUDA build with no offload at all on the Cpu row below.
    /// </summary>
    [Test]
    [Arguments(GpuVariant.Cpu)]
    [Arguments(GpuVariant.Vulkan)]
    public async Task EnsureRunning_ServedBuildOutranksSelectedVariant_EmitsGpuPlacement(GpuVariant selected)
    {
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(selected),
            binaryManager: new FakeBinaryManager(GpuVariant.Cuda));

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.True(spec!.Arguments.Contains("--fit"), "A served GPU build must hand placement to auto-fit.");
        AssertEx.False(spec.Arguments.Contains("-t"), "A served GPU build must not take the CPU thread policy.");
    }

    [Test]
    public async Task EnsureRunning_FrozenProfileReplay_ReplaysVerbatim_NoPolicyDoubleContext()
    {
        var launcher = new FakeProcessLauncher();
        // A frozen profile (replay): explicit -c 8192 + KV/FA. The policy must NOT add a second -c or its own KV.
        var frozen = ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24, kvTypeK: "q8_0", kvTypeV: "q8_0", flashAttn: true);
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            profileResolver: new FakeInferenceProfileResolver(frozen));

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.False(spec!.Arguments.Contains("--fit"), "A replay must not emit --fit (an explicit fit-arg disables auto-fit).");
        AssertEx.Equal(expected: 1, CountOf(spec.Arguments, "-c"));
        AssertEx.Equal("8192", spec.Arguments[IndexOf(spec.Arguments, "-c") + 1]);
        AssertEx.Equal("24", spec.Arguments[IndexOf(spec.Arguments, "--n-gpu-layers") + 1]);
    }

    [Test]
    public async Task EnsureRunning_AfterReadiness_CapturesEffectiveContext_ExposedViaGetRuntimeInfo()
    {
        var launcher = new FakeProcessLauncher();
        // The /props read reports the effective per-slot context the server actually loaded.
        var healthProbe = new FakeHealthProbe
        {
            EffectiveContextTokens = 4096
        };
        await using var supervisor = SupervisorFactory.Create(launcher, healthProbe: healthProbe);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        var runtimeInfo = AssertEx.NotNull(supervisor.GetRuntimeInfo("llama3", ModelRole.Chat));
        AssertEx.Equal(expected: 4096, runtimeInfo.EffectiveContextTokens);
    }

    [Test]
    [Arguments(ModelRole.Embedding, "nomic-embed")]
    [Arguments(ModelRole.Reranker, "bge-reranker-v2-m3")]
    public async Task EnsureRunning_PooledRole_PinsPhysicalBatchToTheContext(ModelRole role, string modelName)
    {
        // REGRESSION (capture run 2026-08-01): a pooled embedding/rerank forward pass is non-causal, so llama-server
        // REJECTS — never splits — any single input longer than n_ubatch, with
        // `500 "input (N tokens) is too large to process. increase the physical batch size (current batch size: 512)"`.
        // llama.cpp defaults n_ubatch to 512, so the real usable input was 512 tokens, NOT the -c we ask for (2048) and
        // not the window the model advertises. The knowledge-base chunker sizes chunks against the model's CONTEXT
        // window, so ordinary 2000-char markdown chunks (~520-680 real tokens) blew straight past the silent ceiling and
        // EVERY knowledge-base document failed to index on a default node. Measured live against
        // nomic-embed-text-v1.5.Q4_K_M: 11 of 12 consecutive real markdown chunks rejected at the default, 0 of 12 with
        // these flags. Pinning -b/-ub to the context is what makes the advertised window actually usable.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = NewSupervisor(launcher);

        await supervisor.EnsureRunningAsync(modelName, role, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));

        var context = spec!.Arguments[IndexOf(spec.Arguments, "-c") + 1];
        AssertEx.Equal(context, spec.Arguments[IndexOf(spec.Arguments, "-ub") + 1]);

        // -b (logical) must be >= -ub (physical); pinning both to the context satisfies that at any context size.
        AssertEx.Equal(context, spec.Arguments[IndexOf(spec.Arguments, "-b") + 1]);
    }

    [Test]
    public async Task EnsureRunning_ChatRole_DoesNotPinPhysicalBatch()
    {
        // Chat is deliberately EXCLUDED from the batch pinning above: a causal decode splits across micro-batches
        // correctly, so raising its batch is a memory/throughput trade-off rather than a correctness fix — and --fit
        // owns that decision. Pinning it here would silently enlarge every chat spawn's compute buffers.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = NewSupervisor(launcher);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.False(spec!.Arguments.Contains("-ub"), "A chat spawn must not pin the physical batch size.");
        AssertEx.False(spec.Arguments.Contains("-b"), "A chat spawn must not pin the logical batch size.");
    }

    [Test]
    public async Task EnsureRunning_PooledRole_FrozenProfileReplay_PinsBatchToTheReplayedContext()
    {
        // A replay owns its own -c, so the pooled-role batch must follow THAT context rather than the policy default —
        // otherwise a replayed embedding server advertises a context it cannot actually embed into.
        var launcher = new FakeProcessLauncher();
        var frozen = ResolvedLaunchArguments.Replay(ctxSize: 8192, nGpuLayers: 24, kvTypeK: "q8_0", kvTypeV: "q8_0", flashAttn: true);
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            profileResolver: new FakeInferenceProfileResolver(frozen));

        await supervisor.EnsureRunningAsync("nomic-embed", ModelRole.Embedding, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal(expected: 1, CountOf(spec!.Arguments, "-c"));
        AssertEx.Equal("8192", spec.Arguments[IndexOf(spec.Arguments, "-c") + 1]);
        AssertEx.Equal("8192", spec.Arguments[IndexOf(spec.Arguments, "-ub") + 1]);
        AssertEx.Equal("8192", spec.Arguments[IndexOf(spec.Arguments, "-b") + 1]);
    }

    [Test]
    public async Task EnsureRunning_ChatRole_EmitsExplicitHostPromptCacheBudget()
    {
        // The pinned build's implicit --cache-ram default is 8192 MiB — half the RAM of a 16 GB machine — so the
        // supervisor must always emit the budget explicitly (upstream #22629: the Linux limit enforcement is
        // ineffective under default overcommit; the OOM killer fires first).
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            options: new LlamaServerSupervisorOptions
            {
                ChatCacheRamMiB = 1234
            });

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal("1234", spec!.Arguments[IndexOf(spec.Arguments, "--cache-ram") + 1]);
    }

    [Test]
    [Arguments(ModelRole.Embedding, "nomic-embed")]
    [Arguments(ModelRole.Reranker, "bge-reranker-v2-m3")]
    public async Task EnsureRunning_PooledRole_DisablesHostPromptCache(ModelRole role, string modelName)
    {
        // One-shot forward passes cache no reusable prompt state — pooled roles pin --cache-ram 0 instead of
        // inheriting the upstream 8192 MiB default per process.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = NewSupervisor(launcher);

        await supervisor.EnsureRunningAsync(modelName, role, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Equal("0", spec!.Arguments[IndexOf(spec.Arguments, "--cache-ram") + 1]);
    }

    [Test]
    public async Task EnsureRunning_WithPerModelExtraArgs_AppendsThemAfterTheBuiltSpec()
    {
        // The per-model developer override: whatever the resolver returns (already stripped of reserved flags) is
        // appended AFTER the built spec, so a later scalar flag overrides a bundled tuning default (llama.cpp last-wins).
        var launcher = new FakeProcessLauncher();
        var extraArgs = Substitute.For<ILlamaServerExtraLaunchArgumentsResolver>();
        extraArgs.ResolveAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<string>>(["--top-k", "40", "--repeat-penalty", "1.1"]));
        await using var supervisor = SupervisorFactory.Create(launcher, extraArgumentsResolver: extraArgs);

        await supervisor.EnsureRunningAsync("llama3", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "--top-k");
        AssertEx.Equal("40", spec.Arguments[IndexOf(spec.Arguments, "--top-k") + 1]);
        AssertEx.Equal("1.1", spec.Arguments[IndexOf(spec.Arguments, "--repeat-penalty") + 1]);
        AssertEx.True(IndexOf(spec.Arguments, "--top-k") > IndexOf(spec.Arguments, "--jinja"),
            "Operator extra args must be appended after the built spec so they override policy defaults.");
    }

    private static void AssertChatBindsLocalhost(LlamaServerLaunchSpec spec)
    {
        var hostIndex = IndexOf(spec.Arguments, "--host");
        AssertEx.Equal("127.0.0.1", spec.Arguments[hostIndex + 1]);
        AssertEx.Equal("127.0.0.1", spec.BaseAddress.Host);
        AssertEx.True(spec.BaseAddress.AbsoluteUri.EndsWith("/v1", StringComparison.Ordinal));
    }

    private static int IndexOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new AssertionException($"Expected flag '{flag}' in argument vector.");
    }

    private static int CountOf(IReadOnlyList<string> args, string flag)
    {
        return args.Count(arg => string.Equals(arg, flag, StringComparison.Ordinal));
    }

    private static LlamaServerProcessSupervisor NewSupervisor(FakeProcessLauncher launcher)
    {
        return SupervisorFactory.Create(launcher);
    }
}
