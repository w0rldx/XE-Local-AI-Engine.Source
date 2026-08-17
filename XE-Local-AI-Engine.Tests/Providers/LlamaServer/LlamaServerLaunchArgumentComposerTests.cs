namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two argv-adjacent composer helpers that no launch-spec test reaches: the verbosity probe that decides
///     whether a placement-probe spawn may add its own <c>-lv</c>, and the launch-plan summary appended to the spawn
///     log line. <c>BuildLaunchSpec</c> itself is pinned by the launch-spec/projection suites.
/// </summary>
public sealed class LlamaServerLaunchArgumentComposerTests
{
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
