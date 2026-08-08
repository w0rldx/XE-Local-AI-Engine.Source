namespace XE_Local_AI_Engine.Tests.Endpoints.Agents.V1;

using XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Mapper tests for the usage-summary projection: a bucket carries its fine-grained
///     provider through to the response, the per-provider rollup folds the (model, provider, day) buckets down to one row
///     per provider (biggest consumer first), and each level attaches a server-computed USD cost estimate — reasoning
///     billed as output, rates per 1M tokens, local runtimes free, rounded to 4 decimals.
/// </summary>
public sealed class UsageSummaryMapperTests
{
    private static TokenUsageAggregateRecord Bucket(string model, string provider, long day, int runs, long prompt, long completion, long reasoning, long total)
    {
        return new TokenUsageAggregateRecord(model, provider, day, runs, prompt, completion, reasoning, total);
    }

    // A resolver that prices one model (gpt-5) with round rates and leaves everything else to the default table / free.
    private static IUsageRateResolver Resolver(double inputPer1M, double outputPer1M)
    {
        return UsageRateResolver.FromSettings(new NodeUsageRateSettings
        {
            Models = new Dictionary<string, ModelRate>
            {
                ["gpt-5"] = new()
                {
                    InputPer1M = inputPer1M,
                    OutputPer1M = outputPer1M
                }
            }
        });
    }

    [Test]
    public void ToResponse_CarriesProviderAndTokenCounts()
    {
        var response = Bucket("llama-x", AgentUsageProviders.Local, day: 86_400_000L, runs: 2, prompt: 40, completion: 50, reasoning: 10, total: 100)
            .ToResponse(Resolver(inputPer1M: 1, outputPer1M: 1));

        AssertEx.Equal("llama-x", response.ModelName);
        AssertEx.Equal(AgentUsageProviders.Local, response.Provider);
        AssertEx.Equal(86_400_000L, response.DayStartUtcMs);
        AssertEx.Equal(expected: 2, response.RunCount);
        AssertEx.Equal(expected: 40L, response.PromptTokens);
        AssertEx.Equal(expected: 50L, response.CompletionTokens);
        AssertEx.Equal(expected: 10L, response.ReasoningTokens);
        AssertEx.Equal(expected: 100L, response.TotalTokens);
        // A local bucket is always free, even though the resolver has non-zero rates.
        AssertEx.Equal(expected: 0d, response.EstimatedCostUsd);
        AssertEx.Equal("USD", response.Currency);
    }

    [Test]
    public void ToResponse_PricesReasoningAsOutput_AndDividesPerMillion()
    {
        // gpt-5 priced at $2/1M input, $4/1M output. prompt 1,000,000 → $2 input; (completion 500,000 + reasoning 500,000)
        // = 1,000,000 output tokens → $4. Reasoning is billed at the OUTPUT rate. Total = $6.0000.
        var response = Bucket("gpt-5", AgentUsageProviders.Codex, day: 86_400_000L, runs: 1, prompt: 1_000_000, completion: 500_000, reasoning: 500_000, total: 2_000_000)
            .ToResponse(Resolver(inputPer1M: 2, outputPer1M: 4));

        AssertEx.Equal(expected: 6d, response.EstimatedCostUsd);
        AssertEx.Equal("USD", response.Currency);
    }

    [Test]
    public void ToResponse_RoundsCostToFourDecimals()
    {
        // input $10/1M over 12,345 prompt tokens → 0.12345, which rounds away-from-zero to 0.1235 (4 dp). No output tokens.
        var response = Bucket("gpt-5", AgentUsageProviders.Codex, day: 86_400_000L, runs: 1, prompt: 12_345, completion: 0, reasoning: 0, total: 12_345)
            .ToResponse(Resolver(inputPer1M: 10, outputPer1M: 999));

        AssertEx.Equal(expected: 0.1235d, response.EstimatedCostUsd);
    }

    [Test]
    public void ToResponse_UnknownModelOnCloudProvider_IsUnpricedZero()
    {
        // A cloud provider but a model with neither an operator override nor a default-table entry is unpriced → $0.
        var response = Bucket("mystery-model", AgentUsageProviders.Codex, day: 86_400_000L, runs: 1, prompt: 1_000_000, completion: 1_000_000, reasoning: 0, total: 2_000_000)
            .ToResponse(Resolver(inputPer1M: 5, outputPer1M: 5));

        AssertEx.Equal(expected: 0d, response.EstimatedCostUsd);
    }

    [Test]
    public void ToByProvider_FoldsAcrossDaysAndModels_OrderedByTotalDescending()
    {
        var resolver = Resolver(inputPer1M: 1, outputPer1M: 1);

        // Two "local" buckets across different models AND days must fold into one provider row; codex is the biggest
        // consumer and must sort first; 'unknown' (backfilled rows) is a first-class provider in the rollup.
        var buckets = new[]
        {
            Bucket("llama-x", AgentUsageProviders.Local, day: 86_400_000L, runs: 2, prompt: 40, completion: 50, reasoning: 10, total: 100),
            Bucket("llama-y", AgentUsageProviders.Local, day: 172_800_000L, runs: 1, prompt: 20, completion: 25, reasoning: 5, total: 50),
            Bucket("gpt-5", AgentUsageProviders.Codex, day: 86_400_000L, runs: 3, prompt: 100, completion: 150, reasoning: 50, total: 300),
            Bucket("legacy", AgentUsageProviders.Unknown, day: 86_400_000L, runs: 1, prompt: 4, completion: 4, reasoning: 2, total: 10)
        };

        var byProvider = buckets.ToByProvider(resolver);

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
        // Local is free regardless of the resolver's rates.
        AssertEx.Equal(expected: 0d, local.EstimatedCostUsd);
        AssertEx.Equal("USD", local.Currency);

        AssertEx.Equal(AgentUsageProviders.Unknown, byProvider[2].Provider);
        AssertEx.Equal(expected: 10L, byProvider[2].TotalTokens);
    }

    [Test]
    public void CostFold_LocalIsFree_CloudIsPriced_AcrossProviderAndTotals()
    {
        // gpt-5 at $1/1M in and out: codex bucket = 1*(200/1e6) input + 1*((300+100)/1e6) output = 0.0002 + 0.0004 = 0.0006.
        var resolver = Resolver(inputPer1M: 1, outputPer1M: 1);
        var buckets = new[]
        {
            Bucket("llama-x", AgentUsageProviders.Local, day: 86_400_000L, runs: 1, prompt: 999_999, completion: 999_999, reasoning: 0, total: 1_999_998),
            Bucket("gpt-5", AgentUsageProviders.Codex, day: 86_400_000L, runs: 1, prompt: 200, completion: 300, reasoning: 100, total: 600)
        };

        var byProvider = buckets.ToByProvider(resolver);
        var totals = buckets.ToTotals(resolver);

        var codex = byProvider.Single(row => row.Provider == AgentUsageProviders.Codex);
        var local = byProvider.Single(row => row.Provider == AgentUsageProviders.Local);
        AssertEx.Equal(expected: 0.0006d, codex.EstimatedCostUsd);
        AssertEx.Equal(expected: 0d, local.EstimatedCostUsd);

        // Grand total = the codex cost alone (local is free).
        AssertEx.Equal(expected: 0.0006d, totals.EstimatedCostUsd);
        AssertEx.Equal("USD", totals.Currency);
    }

    [Test]
    public void ToByProvider_EmptyBuckets_YieldsEmptyRollup()
    {
        AssertEx.Empty(Array.Empty<TokenUsageAggregateRecord>().ToByProvider(Resolver(inputPer1M: 1, outputPer1M: 1)));
    }
}
