namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two argv-adjacent composer helpers that no launch-spec test reaches: the verbosity probe that decides
///     whether a placement-probe spawn may add its own <c>-lv</c>, and the launch-plan summary appended to the spawn
///     log line. <c>BuildLaunchSpec</c> itself is pinned by the launch-spec/projection suites.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LlamaServerLaunchArgumentComposerTests
{
    [Test]
    public void Composer_WhenAllocationCarriesOverride_EmitsExplicitContextFlag()
    {
        // The request-scoped context override reaches the composer as the launch-policy plan's requested context. An
        // explore spawn must still hand placement to auto-fit AND pin the window, in that order: --fit adjusts only
        // UNSET arguments, so an explicit -c emitted after it is respected rather than overwritten. Without this the
        // override would never reach llama-server or the co-invoked llama-fit-params helper.
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: 16384,
            UseKvCacheQuantization: false,
            LlamaServerKvCacheTypes.F16,
            CpuThreads: null,
            CpuThreadsBatch: null);

        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(new LlamaServerProcessSupervisor.ProcessKey("qwen3", ModelRole.Chat),
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(contextTokensOverride: 16384),
            chatCacheReuse: 256,
            plan: plan,
            chatCacheRamMiB: 512);

        var fitIndex = spec.Arguments.ToList().IndexOf("--fit");
        AssertEx.True(fitIndex >= 0, "a GPU explore must emit --fit.");
        AssertEx.Equal("on", spec.Arguments[fitIndex + 1]);
        AssertEx.Equal("-c", spec.Arguments[fitIndex + 2]);

        // The chat pool is the window plus the background headroom; the window itself is the per-slot cap.
        AssertEx.Equal("24576", spec.Arguments[fitIndex + 3]);
        AssertEx.Equal("16384", ValueOf(spec, "--kv-unified-per-slot"));
    }

    [Test]
    public void Composer_ChatRole_RunsTwoSlotsOverOneUnifiedPoolCappedAtTheWindow()
    {
        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(new LlamaServerProcessSupervisor.ProcessKey("model", ModelRole.Chat),
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 0,
            plan: new LlamaServerLaunchPlan(RequestedContextTokens: 4096,
                UseKvCacheQuantization: false,
                LlamaServerKvCacheTypes.F16,
                CpuThreads: null,
                CpuThreadsBatch: null,
                ChatSlots: LlamaServerChatSlots.Count));

        AssertEx.Equal("2", ValueOf(spec, "--parallel"));
        AssertEx.Equal("12288", ValueOf(spec, "-c"));
        AssertEx.Equal("4096", ValueOf(spec, "--kv-unified-per-slot"));
        AssertEx.Contains(spec.Arguments, "--kv-unified");

        // Without it the server saves and clears the idle chat slot whenever the background slot starts a task.
        AssertEx.Contains(spec.Arguments, "--no-cache-idle-slots");
    }

    [Test]
    public void Composer_ChatRoleWithNoPlan_RunsOneSlot()
    {
        // A policy-free spawn (a frozen replay's profiling, a smoke load, an evaluation) replays a placement measured without a second slot.
        var spec = BuildGpuReplaySpec(ModelRole.Chat);

        AssertEx.Equal("1", ValueOf(spec, "--parallel"));
        AssertEx.Equal("4096", ValueOf(spec, "-c"));
        AssertEx.False(spec.Arguments.Any(static argument => argument.Contains("kv-unified", StringComparison.Ordinal) || argument.Contains("cache-idle", StringComparison.Ordinal)),
            "a policy-free chat spawn carries no pool flags.");
    }

    [Test]
    public void Composer_ChatRoleAdmittedSingleSlot_EmitsTheSingleSlotVectorAndRecordsIt()
    {
        // The fallback when the second slot would cost window or residency: develop's vector, no pool flags, the window as -c.
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: 65536,
            UseKvCacheQuantization: false,
            LlamaServerKvCacheTypes.F16,
            CpuThreads: null,
            CpuThreadsBatch: null,
            ChatSlots: 1);

        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(new LlamaServerProcessSupervisor.ProcessKey("qwen3", ModelRole.Chat),
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 0,
            plan: plan);

        AssertEx.Equal("1", ValueOf(spec, "--parallel"));
        AssertEx.Equal("65536", ValueOf(spec, "-c"));
        AssertEx.False(spec.Arguments.Any(static argument => argument.Contains("kv-unified", StringComparison.Ordinal) || argument.Contains("cache-idle", StringComparison.Ordinal)),
            "a single-slot chat process carries no pool flags.");

        var recorded = AssertEx.NotNull(LlamaServerLaunchProjection.FromArgumentsOrNull(spec.Arguments));
        AssertEx.Equal(expected: 1, recorded.Parallel);
        AssertEx.Null(recorded.SlotContextTokens);
    }

    [Test]
    [Arguments(ModelRole.Embedding)]
    [Arguments(ModelRole.Reranker)]
    [Arguments(ModelRole.Decision)]
    public void Composer_NonChatRole_KeepsOneSlotAndTheRequestedContext(ModelRole role)
    {
        var spec = BuildGpuReplaySpec(role);

        AssertEx.Equal("1", ValueOf(spec, "--parallel"));
        AssertEx.Equal("4096", ValueOf(spec, "-c"));
        AssertEx.False(spec.Arguments.Any(static argument => argument.Contains("kv-unified", StringComparison.Ordinal) || argument.Contains("cache-idle", StringComparison.Ordinal)),
            "only the chat role runs the two-slot pool.");
    }

    [Test]
    public void ManagedFlags_ReserveTheChatSlotPoolFlags()
    {
        // An operator override could split the pool in half, uncap a slot or bring back the idle-slot clear.
        foreach (var flag in new[]
                 {
                     "--kv-unified",
                     "-kvu",
                     "--no-kv-unified",
                     "-no-kvu",
                     "--kv-unified-per-slot",
                     "--cache-idle-slots",
                     "--no-cache-idle-slots"
                 })
        {
            AssertEx.Contains(LlamaServerManagedFlags.All, flag);
        }
    }

    [Test]
    public void Composer_WhenAliasGiven_EmitsItSoTheServerNeverReportsTheModelPath()
    {
        var spec = BuildChatSpec(modelAlias: "org/model-GGUF:Q4_K_M");

        var aliasIndex = spec.Arguments.ToList().IndexOf("--alias");
        AssertEx.True(aliasIndex >= 0, "a supported runtime must be told the id clients route by.");
        AssertEx.Equal("org/model-GGUF:Q4_K_M", spec.Arguments[aliasIndex + 1]);
        AssertEx.Equal(expected: 1, spec.Arguments.Count(static argument => argument == "--alias"));
    }

    [Test]
    public void Composer_WhenNoAlias_OmitsTheFlag()
    {
        var spec = BuildChatSpec(modelAlias: null);

        AssertEx.False(spec.Arguments.Contains("--alias"), "a runtime without --alias support must not be handed the flag.");
    }

    [Test]
    public void ManagedFlags_ReserveTheAliasTheComposerEmits()
    {
        // An operator alias would rename the model under the node's routing id, so it is refused on write and stripped on read like -m.
        AssertEx.Contains(LlamaServerManagedFlags.All, "--alias");
        AssertEx.Contains(LlamaServerManagedFlags.All, "-a");
    }

    [Test]
    [Arguments("-v")]
    [Arguments("--verbose")]
    [Arguments("--log-verbose")]
    [Arguments("-lv")]
    [Arguments("--verbosity")]
    [Arguments("--log-verbosity")]
    public void HasVerbosityArgument_RecognizesEveryUpstreamSpelling(string argument)
    {
        AssertEx.True(LlamaServerLaunchArgumentComposer.HasVerbosityArgument(["-m", "/fake/model.gguf", argument, "4"]),
            $"'{argument}' already sets a log verbosity, so the caller must not add a second one.");
    }

    [Test]
    public void HasVerbosityArgument_WhenNoVerbosityFlag_ReturnsFalse()
    {
        AssertEx.False(LlamaServerLaunchArgumentComposer.HasVerbosityArgument(["-m", "/fake/model.gguf", "--parallel", "1", "--no-warmup"]));
    }

    private static string ValueOf(LlamaServerLaunchSpec spec, string flag)
    {
        var index = spec.Arguments.ToList().IndexOf(flag);
        AssertEx.True(index >= 0 && index + 1 < spec.Arguments.Count, $"'{flag}' must be emitted with a value.");
        return spec.Arguments[index + 1];
    }

    // A GPU replay pins the frozen context verbatim, so -c is always emitted.
    private static LlamaServerLaunchSpec BuildGpuReplaySpec(ModelRole role)
    {
        return LlamaServerLaunchArgumentComposer.BuildLaunchSpec(new LlamaServerProcessSupervisor.ProcessKey("model", role),
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Replay(ctxSize: 4096),
            chatCacheReuse: 0);
    }

    private static LlamaServerLaunchSpec BuildChatSpec(string? modelAlias)
    {
        return LlamaServerLaunchArgumentComposer.BuildLaunchSpec(new LlamaServerProcessSupervisor.ProcessKey("qwen3", ModelRole.Chat),
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Replay(ctxSize: 4096),
            chatCacheReuse: 0,
            modelAlias: modelAlias);
    }

    [Test]
    public void DescribeLaunchPlan_WhenNoPlan_IsEmpty()
    {
        AssertEx.Equal(string.Empty, LlamaServerLaunchArgumentComposer.DescribeLaunchPlan(plan: null));
    }

    [Test]
    public void DescribeLaunchPlan_WhenPlanCarriesNothing_IsEmpty()
    {
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: null,
            UseKvCacheQuantization: false,
            KvCacheType: "q8_0",
            CpuThreads: null,
            CpuThreadsBatch: null);

        AssertEx.Equal(string.Empty, LlamaServerLaunchArgumentComposer.DescribeLaunchPlan(plan));
    }

    [Test]
    public void DescribeLaunchPlan_SummarizesContextKvAndThreads()
    {
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: 8192,
            UseKvCacheQuantization: true,
            KvCacheType: "q8_0",
            CpuThreads: 8,
            CpuThreadsBatch: 16);

        AssertEx.Equal(" [ctx=8192, kv=q8_0+fa, threads=8/16]", LlamaServerLaunchArgumentComposer.DescribeLaunchPlan(plan));
    }

    [Test]
    public void DescribeLaunchPlan_WhenBatchThreadsUnset_RendersADash()
    {
        var plan = new LlamaServerLaunchPlan(RequestedContextTokens: null,
            UseKvCacheQuantization: false,
            KvCacheType: "q8_0",
            CpuThreads: 4,
            CpuThreadsBatch: null);

        AssertEx.Equal(" [threads=4/-]", LlamaServerLaunchArgumentComposer.DescribeLaunchPlan(plan));
    }
}
