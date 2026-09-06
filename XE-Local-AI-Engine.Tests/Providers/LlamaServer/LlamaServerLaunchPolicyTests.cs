namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for <see cref="LlamaServerLaunchPolicy" /> — the central component that applies the shared context
///     allocation (<c>-c</c>), GPU KV-cache quantization + flash attention, and CPU thread policy.
/// </summary>
public sealed class LlamaServerLaunchPolicyTests
{
    [Test]
    public async Task Resolve_GpuExploreChat_RequestsChatContext_AndEnablesKvQuant()
    {
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions());

        var plan = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            Allocation(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, ProcessPlacementMode.GpuResident),
            CancellationToken.None);

        AssertEx.Equal(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, plan.RequestedContextTokens!.Value);
        AssertEx.True(plan.UseKvCacheQuantization, "a GPU explore spawn defaults to KV-cache quantization + flash attention.");
        AssertEx.Equal("q8_0", plan.KvCacheType);
        AssertEx.True(plan.CpuThreads is null, "a GPU spawn must not carry CPU thread flags.");
        AssertEx.True(plan.CpuThreadsBatch is null, "a GPU spawn must not carry CPU thread-batch flags.");
    }

    [Test]
    public async Task Resolve_EmbeddingAndReranker_UseTheirAllocatedContexts()
    {
        var options = new LlamaServerLaunchPolicyOptions();
        var policy = NewPolicy(options);

        var embedding = await policy.ResolveAsync(ModelRole.Embedding,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            Allocation(options.EmbeddingContextTokens, ProcessPlacementMode.GpuResident),
            CancellationToken.None);
        var reranker = await policy.ResolveAsync(ModelRole.Reranker,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            Allocation(options.RerankerContextTokens, ProcessPlacementMode.GpuResident),
            CancellationToken.None);

        AssertEx.Equal(options.EmbeddingContextTokens, embedding.RequestedContextTokens!.Value);
        AssertEx.Equal(options.RerankerContextTokens, reranker.RequestedContextTokens!.Value);
    }

    [Test]
    public async Task Resolve_WhenAllocationIsBelowRoleDefault_UsesAllocatedContext()
    {
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions());

        // The shared allocation resolver owns train-context capping; the launch policy must use its decision verbatim.
        var allocation = Allocation(processContextTokens: 3840,
            ProcessPlacementMode.GpuResident,
            modelTrainContextTokens: 4096);
        var plan = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            allocation,
            CancellationToken.None);

        AssertEx.Equal(expected: 3840, plan.RequestedContextTokens!.Value);
    }

    [Test]
    public async Task Resolve_WhenAllocationUsesRoleDefault_UsesAllocatedContext()
    {
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions());

        var plan = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            Allocation(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens,
                ProcessPlacementMode.GpuResident,
                modelTrainContextTokens: 262144),
            CancellationToken.None);

        AssertEx.Equal(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, plan.RequestedContextTokens!.Value);
    }

    [Test]
    public async Task Resolve_WhenBackendFallbackRecorded_DisablesKvQuant()
    {
        var fallbackStore = new FakeLaunchFallbackStore();
        fallbackStore.Disable(GpuVariant.Cuda);
        var policy = new LlamaServerLaunchPolicy(new LlamaServerLaunchPolicyOptions(), fallbackStore);

        var allocation = Allocation(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, ProcessPlacementMode.GpuResident);
        var plan = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Cuda, ResolvedLaunchArguments.Explore(), allocation, CancellationToken.None);

        AssertEx.False(plan.UseKvCacheQuantization, "a backend with a recorded optimized-config fallback must not re-enable KV-cache quantization.");
        // A different backend is unaffected by the Cuda fallback record.
        var vulkanPlan = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Vulkan, ResolvedLaunchArguments.Explore(), allocation, CancellationToken.None);
        AssertEx.True(vulkanPlan.UseKvCacheQuantization, "an unrelated backend keeps the optimized config.");
    }

    [Test]
    public async Task Resolve_WhenGpuKvQuantDisabledInOptions_NeverEnablesIt()
    {
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions
        {
            EnableGpuKvCacheQuantization = false
        });

        var plan = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            Allocation(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, ProcessPlacementMode.GpuResident),
            CancellationToken.None);

        AssertEx.False(plan.UseKvCacheQuantization, "KV-cache quantization must stay off when disabled by options.");
    }

    [Test]
    public async Task Resolve_CpuVariant_RequestsContext_EmitsThreads_AndNoKvQuant()
    {
        var options = new LlamaServerLaunchPolicyOptions
        {
            CpuThreadCount = 6,
            CpuThreadsBatchCount = 8
        };
        var policy = NewPolicy(options);

        var plan = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            Allocation(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, ProcessPlacementMode.Cpu),
            CancellationToken.None);

        AssertEx.Equal(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, plan.RequestedContextTokens!.Value);
        AssertEx.False(plan.UseKvCacheQuantization, "the CPU variant keeps f16 KV — no quantization.");
        AssertEx.Equal(expected: 6, plan.CpuThreads!.Value);
        AssertEx.Equal(expected: 8, plan.CpuThreadsBatch!.Value);
    }

    [Test]
    public async Task Resolve_CpuVariant_WhenThreadPolicyDisabled_EmitsNoThreads()
    {
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions
        {
            EnableCpuThreadPolicy = false
        });

        var plan = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            Allocation(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, ProcessPlacementMode.Cpu),
            CancellationToken.None);

        AssertEx.True(plan.CpuThreads is null, "no -t is emitted when the CPU thread policy is disabled.");
        AssertEx.True(plan.CpuThreadsBatch is null, "no -tb is emitted when the CPU thread policy is disabled.");
    }

    [Test]
    public async Task Resolve_ReplayMode_LeavesContextAndKvToTheFrozenProfile()
    {
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions
        {
            CpuThreadCount = 4,
            CpuThreadsBatchCount = 4
        });

        // GPU replay: the frozen profile owns -c/KV/FA, so the policy supplies neither.
        var gpuReplay = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Replay(ctxSize: 8192),
            Allocation(8192, ProcessPlacementMode.GpuResident, source: ProcessContextAllocationSource.FrozenProfile),
            CancellationToken.None);
        AssertEx.True(gpuReplay.RequestedContextTokens is null, "a GPU replay's -c must come from the frozen profile, not the policy.");
        AssertEx.False(gpuReplay.UseKvCacheQuantization, "a replay owns its own KV/FA — the policy must not add them.");
        AssertEx.True(gpuReplay.CpuThreads is null, "a GPU replay carries no CPU threads.");

        // CPU replay: a frozen GPU profile does NOT transfer to a CPU spawn, so the policy applies its own deterministic
        // context and the CPU thread policy (the GPU -ngl/-ts/-ot/KV are meaningless on CPU).
        var cpuReplay = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Replay(ctxSize: 8192),
            Allocation(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens,
                ProcessPlacementMode.Cpu,
                source: ProcessContextAllocationSource.HardwareTier),
            CancellationToken.None);
        AssertEx.Equal(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, cpuReplay.RequestedContextTokens!.Value);
        AssertEx.False(cpuReplay.UseKvCacheQuantization, "a CPU spawn never quantizes KV.");
        AssertEx.Equal(expected: 4, cpuReplay.CpuThreads!.Value);
    }

    [Test]
    public async Task GpuReplay_KvCacheTypeSetting_IsNotApplied()
    {
        // The policy never overrides a replay's KV: a frozen profile pins its own -ctk/-ctv verbatim. Under D13 that
        // stays true AND is now unreachable in the superseded case — changing the knob stales the profile through the
        // launch-policy fingerprint first, so a replay carrying a KV type the operator has since abandoned never runs.
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions
        {
            KvCacheType = LlamaServerKvCacheTypes.Q4_0
        });

        var gpuReplay = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, kvTypeK: "q8_0", kvTypeV: "q8_0", flashAttn: true),
            Allocation(8192, ProcessPlacementMode.GpuResident, source: ProcessContextAllocationSource.FrozenProfile),
            CancellationToken.None);

        AssertEx.False(gpuReplay.UseKvCacheQuantization, "a replay owns its own KV/FA — the policy must not add them.");
        AssertEx.True(gpuReplay.RequestedContextTokens is null, "a GPU replay's -c must come from the frozen profile.");
    }

    [Test]
    public async Task GpuExplore_NonExpertOffloadAllocation_PlanCarriesNoCpuMoe()
    {
        // Byte-identical default for S2: --cpu-moe follows the ADMITTED placement, so every dense model and every MoE
        // model that fits resident keeps exactly today's argv.
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions());

        var resident = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            Allocation(8192, ProcessPlacementMode.GpuResident),
            CancellationToken.None);

        AssertEx.False(resident.CpuMoe, "a GPU-resident placement must never emit --cpu-moe.");
        AssertEx.Null(resident.CpuMoeLayers, "this slice never derives a partial layer count.");
    }

    [Test]
    public async Task GpuExplore_ExpertOffloadAllocation_PlanCarriesCpuMoe()
    {
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions());

        var offload = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            Allocation(8192, ProcessPlacementMode.ExpertOffload),
            CancellationToken.None);

        AssertEx.True(offload.CpuMoe, "an expert-offload placement must emit the flag that makes it true.");
    }

    [Test]
    public async Task CpuAndReplayBranches_NeverCarryCpuMoe()
    {
        // Neither branch can honour the flag: a CPU build has no experts to move off a GPU, and a replay pins its own
        // -ot verbatim, so --cpu-moe and -ot can never coexist.
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions());

        var cpu = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            Allocation(8192, ProcessPlacementMode.ExpertOffload),
            CancellationToken.None);
        var replay = await policy.ResolveAsync(ModelRole.Chat,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Replay(ctxSize: 8192, overrideTensor: "exps=CPU"),
            Allocation(8192, ProcessPlacementMode.ExpertOffload, source: ProcessContextAllocationSource.FrozenProfile),
            CancellationToken.None);

        AssertEx.False(cpu.CpuMoe, "a CPU build never emits --cpu-moe.");
        AssertEx.False(replay.CpuMoe, "a frozen replay owns its own placement; --cpu-moe must never join its -ot.");
    }

    [Test]
    public async Task RecordOptimizedConfigFailed_PersistsThroughTheStore()
    {
        var fallbackStore = new FakeLaunchFallbackStore();
        var policy = new LlamaServerLaunchPolicy(new LlamaServerLaunchPolicyOptions(), fallbackStore);

        await policy.RecordOptimizedConfigFailedAsync(GpuVariant.Vulkan, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None);

        AssertEx.True(await fallbackStore.IsOptimizedConfigDisabledAsync(GpuVariant.Vulkan, LlamaServerKvCacheTypes.Q8_0, CancellationToken.None),
            "recording a failed optimized config must persist through the fallback store.");
    }

    private static LlamaServerLaunchPolicy NewPolicy(LlamaServerLaunchPolicyOptions options)
    {
        return new LlamaServerLaunchPolicy(options, new FakeLaunchFallbackStore());
    }

    private static ProcessContextAllocation Allocation(int processContextTokens,
        ProcessPlacementMode placement,
        int? modelTrainContextTokens = null,
        ProcessContextAllocationSource source = ProcessContextAllocationSource.HardwareTier)
    {
        return new ProcessContextAllocation(processContextTokens,
            modelTrainContextTokens,
            source,
            placement,
            ResourceFootprint.Zero,
            ContentIdentity: "test-content",
            CacheKey: "test-cache");
    }
}
