namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for <see cref="LlamaServerLaunchPolicy" /> — the central component that decides the deterministic
///     context (<c>-c</c>), GPU KV-cache quantization + flash attention, and CPU thread policy (AUD4-02/05/17).
/// </summary>
public sealed class LlamaServerLaunchPolicyTests
{
    [Test]
    public async Task Resolve_GpuExploreChat_RequestsChatContext_AndEnablesKvQuant()
    {
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions());

        var plan = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Cuda, ResolvedLaunchArguments.Explore(), modelTrainContextTokens: null, CancellationToken.None);

        AssertEx.Equal(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, plan.RequestedContextTokens!.Value);
        AssertEx.True(plan.UseKvCacheQuantization, "a GPU explore spawn defaults to KV-cache quantization + flash attention.");
        AssertEx.Equal("q8_0", plan.KvCacheType);
        AssertEx.True(plan.CpuThreads is null, "a GPU spawn must not carry CPU thread flags.");
        AssertEx.True(plan.CpuThreadsBatch is null, "a GPU spawn must not carry CPU thread-batch flags.");
    }

    [Test]
    public async Task Resolve_EmbeddingAndReranker_UseTheirSmallerContextDefaults()
    {
        var options = new LlamaServerLaunchPolicyOptions();
        var policy = NewPolicy(options);

        var embedding = await policy.ResolveAsync(ModelRole.Embedding, GpuVariant.Cuda, ResolvedLaunchArguments.Explore(), modelTrainContextTokens: null, CancellationToken.None);
        var reranker = await policy.ResolveAsync(ModelRole.Reranker, GpuVariant.Cuda, ResolvedLaunchArguments.Explore(), modelTrainContextTokens: null, CancellationToken.None);

        AssertEx.Equal(options.EmbeddingContextTokens, embedding.RequestedContextTokens!.Value);
        AssertEx.Equal(options.RerankerContextTokens, reranker.RequestedContextTokens!.Value);
    }

    [Test]
    public async Task Resolve_WhenRoleContextExceedsTrainContext_CapsToTrainContextMinusSafetyMargin()
    {
        var options = new LlamaServerLaunchPolicyOptions
        {
            ChatContextTokens = 16384,
            ContextSafetyMarginTokens = 256
        };
        var policy = NewPolicy(options);

        // A small 4096-context model: the 16384 chat default cannot be requested, so it caps to trainCtx - margin.
        var plan = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Cuda, ResolvedLaunchArguments.Explore(), modelTrainContextTokens: 4096, CancellationToken.None);

        AssertEx.Equal(expected: 4096 - 256, plan.RequestedContextTokens!.Value);
    }

    [Test]
    public async Task Resolve_WhenRoleContextFitsTrainContext_LeavesItUncapped()
    {
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions());

        // A large-context model: the 16384 chat default fits under the train context, so it is requested unchanged.
        var plan = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Cuda, ResolvedLaunchArguments.Explore(), modelTrainContextTokens: 262144, CancellationToken.None);

        AssertEx.Equal(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, plan.RequestedContextTokens!.Value);
    }

    [Test]
    public async Task Resolve_WhenBackendFallbackRecorded_DisablesKvQuant()
    {
        var fallbackStore = new FakeLaunchFallbackStore();
        fallbackStore.Disable(GpuVariant.Cuda);
        var policy = new LlamaServerLaunchPolicy(new LlamaServerLaunchPolicyOptions(), fallbackStore);

        var plan = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Cuda, ResolvedLaunchArguments.Explore(), modelTrainContextTokens: null, CancellationToken.None);

        AssertEx.False(plan.UseKvCacheQuantization, "a backend with a recorded optimized-config fallback must not re-enable KV-cache quantization.");
        // A different backend is unaffected by the Cuda fallback record.
        var vulkanPlan = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Vulkan, ResolvedLaunchArguments.Explore(), modelTrainContextTokens: null, CancellationToken.None);
        AssertEx.True(vulkanPlan.UseKvCacheQuantization, "an unrelated backend keeps the optimized config.");
    }

    [Test]
    public async Task Resolve_WhenGpuKvQuantDisabledInOptions_NeverEnablesIt()
    {
        var policy = NewPolicy(new LlamaServerLaunchPolicyOptions
        {
            EnableGpuKvCacheQuantization = false
        });

        var plan = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Cuda, ResolvedLaunchArguments.Explore(), modelTrainContextTokens: null, CancellationToken.None);

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

        var plan = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Cpu, ResolvedLaunchArguments.Explore(), modelTrainContextTokens: null, CancellationToken.None);

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

        var plan = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Cpu, ResolvedLaunchArguments.Explore(), modelTrainContextTokens: null, CancellationToken.None);

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
        var gpuReplay = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Cuda, ResolvedLaunchArguments.Replay(ctxSize: 8192), modelTrainContextTokens: null, CancellationToken.None);
        AssertEx.True(gpuReplay.RequestedContextTokens is null, "a GPU replay's -c must come from the frozen profile, not the policy.");
        AssertEx.False(gpuReplay.UseKvCacheQuantization, "a replay owns its own KV/FA — the policy must not add them.");
        AssertEx.True(gpuReplay.CpuThreads is null, "a GPU replay carries no CPU threads.");

        // CPU replay: a frozen GPU profile does NOT transfer to a CPU spawn, so the policy applies its own deterministic
        // context and the CPU thread policy (the GPU -ngl/-ts/-ot/KV are meaningless on CPU).
        var cpuReplay = await policy.ResolveAsync(ModelRole.Chat, GpuVariant.Cpu, ResolvedLaunchArguments.Replay(ctxSize: 8192), modelTrainContextTokens: null, CancellationToken.None);
        AssertEx.Equal(LlamaServerLaunchPolicyOptions.DefaultChatContextTokens, cpuReplay.RequestedContextTokens!.Value);
        AssertEx.False(cpuReplay.UseKvCacheQuantization, "a CPU spawn never quantizes KV.");
        AssertEx.Equal(expected: 4, cpuReplay.CpuThreads!.Value);
    }

    [Test]
    public async Task RecordOptimizedConfigFailed_PersistsThroughTheStore()
    {
        var fallbackStore = new FakeLaunchFallbackStore();
        var policy = new LlamaServerLaunchPolicy(new LlamaServerLaunchPolicyOptions(), fallbackStore);

        await policy.RecordOptimizedConfigFailedAsync(GpuVariant.Vulkan, CancellationToken.None);

        AssertEx.True(await fallbackStore.IsOptimizedConfigDisabledAsync(GpuVariant.Vulkan, CancellationToken.None),
            "recording a failed optimized config must persist through the fallback store.");
    }

    private static LlamaServerLaunchPolicy NewPolicy(LlamaServerLaunchPolicyOptions options)
    {
        return new LlamaServerLaunchPolicy(options, new FakeLaunchFallbackStore());
    }
}
