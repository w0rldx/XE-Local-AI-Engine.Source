namespace XE_Local_AI_Engine.Tests.Endpoints.Agents.V1;

using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Mapper tests for the usage-summary projection (Wave 8 Lane A): a bucket carries its fine-grained provider through
///     to the response, and the per-provider rollup folds the (model, provider, day) buckets down to one row per provider
///     — summing across days and models and ordering the biggest consumer first.
/// </summary>
public sealed class UsageSummaryMapperTests
{
    private static TokenUsageAggregateRecord Bucket(string model, string provider, long day, int runs, long prompt, long completion, long reasoning, long total)
    {
        return new TokenUsageAggregateRecord(model, provider, day, runs, prompt, completion, reasoning, total);
    }

    [Test]
    public void ToResponse_CarriesProviderAndTokenCounts()
    {
        var response = Bucket("llama-x", AgentUsageProviders.Local, day: 86_400_000L, runs: 2, prompt: 40, completion: 50, reasoning: 10, total: 100).ToResponse();

        AssertEx.Equal("llama-x", response.ModelName);
        AssertEx.Equal(AgentUsageProviders.Local, response.Provider);
        AssertEx.Equal(86_400_000L, response.DayStartUtcMs);
        AssertEx.Equal(expected: 2, response.RunCount);
        AssertEx.Equal(expected: 40L, response.PromptTokens);
        AssertEx.Equal(expected: 50L, response.CompletionTokens);
        AssertEx.Equal(expected: 10L, response.ReasoningTokens);
        AssertEx.Equal(expected: 100L, response.TotalTokens);
    }

    [Test]
    public void ToByProvider_FoldsAcrossDaysAndModels_OrderedByTotalDescending()
    {
        // Two "local" buckets across different models AND days must fold into one provider row; codex is the biggest
        // consumer and must sort first; 'unknown' (backfilled rows) is a first-class provider in the rollup.
        var buckets = new[]
        {
            Bucket("llama-x", AgentUsageProviders.Local, day: 86_400_000L, runs: 2, prompt: 40, completion: 50, reasoning: 10, total: 100),
            Bucket("llama-y", AgentUsageProviders.Local, day: 172_800_000L, runs: 1, prompt: 20, completion: 25, reasoning: 5, total: 50),
            Bucket("gpt-5", AgentUsageProviders.Codex, day: 86_400_000L, runs: 3, prompt: 100, completion: 150, reasoning: 50, total: 300),
            Bucket("legacy", AgentUsageProviders.Unknown, day: 86_400_000L, runs: 1, prompt: 4, completion: 4, reasoning: 2, total: 10)
        };

        var byProvider = buckets.ToByProvider();

        // Ordered by total tokens descending → codex (300), local (150), unknown (10).
        AssertEx.Equal(expected: 3, byProvider.Count);

        AssertEx.Equal(AgentUsageProviders.Codex, byProvider[0].Provider);
        AssertEx.Equal(expected: 3, byProvider[0].RunCount);
        AssertEx.Equal(expected: 300L, byProvider[0].TotalTokens);

        var local = byProvider[1];
        AssertEx.Equal(AgentUsageProviders.Local, local.Provider);
        // Both local buckets folded: runs 2+1, prompt 40+20, completion 50+25, reasoning 10+5, total 100+50.
        AssertEx.Equal(expected: 3, local.RunCount);
        AssertEx.Equal(expected: 60L, local.PromptTokens);
        AssertEx.Equal(expected: 75L, local.CompletionTokens);
        AssertEx.Equal(expected: 15L, local.ReasoningTokens);
        AssertEx.Equal(expected: 150L, local.TotalTokens);

        AssertEx.Equal(AgentUsageProviders.Unknown, byProvider[2].Provider);
        AssertEx.Equal(expected: 10L, byProvider[2].TotalTokens);
    }

    [Test]
    public void ToByProvider_EmptyBuckets_YieldsEmptyRollup()
    {
        AssertEx.Empty(System.Array.Empty<TokenUsageAggregateRecord>().ToByProvider());
    }
}
