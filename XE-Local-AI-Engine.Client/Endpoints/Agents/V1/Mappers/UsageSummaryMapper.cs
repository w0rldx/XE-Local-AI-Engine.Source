namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

internal static class UsageSummaryMapper
{
    /// <summary>ISO 4217 currency all cost estimates are quoted in (rates are USD per 1M tokens).</summary>
    internal const string CurrencyUsd = "USD";

    /// <summary>Cost estimates are rounded to this many decimal places server-side (fractional cents).</summary>
    private const int CostDecimals = 4;

    public static AgentUsageSummaryBucketResponse ToResponse(this TokenUsageAggregateRecord record, IUsageRateResolver rateResolver)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(rateResolver);

        // Pass-through of the aggregate bucket plus the server-computed cost estimate. All fields are token counts / ids /
        // a category label / a derived cost — no content to redact.
        return new AgentUsageSummaryBucketResponse
        {
            ModelName = record.ModelName,
            Provider = record.Provider,
            DayStartUtcMs = record.DayStartUtcMs,
            RunCount = record.RunCount,
            PromptTokens = record.PromptTokens,
            CompletionTokens = record.CompletionTokens,
            ReasoningTokens = record.ReasoningTokens,
            TotalTokens = record.TotalTokens,
            EstimatedCostUsd = Round(RawCost(record, rateResolver)),
            Currency = CurrencyUsd
        };
    }

    public static AgentUsageSummaryTotalsResponse ToTotals(this IReadOnlyList<TokenUsageAggregateRecord> records, IUsageRateResolver rateResolver)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(rateResolver);

        // Fold the buckets once so the caller need not re-sum. Token sums stay long (a wide range can exceed int); the
        // cost accumulates as a raw (unrounded) double and is rounded ONCE at the end, so the grand total does not
        // compound per-bucket rounding error.
        var runCount = 0;
        var promptTokens = 0L;
        var completionTokens = 0L;
        var reasoningTokens = 0L;
        var totalTokens = 0L;
        var cost = 0d;

        foreach (var record in records)
        {
            runCount += record.RunCount;
            promptTokens += record.PromptTokens;
            completionTokens += record.CompletionTokens;
            reasoningTokens += record.ReasoningTokens;
            totalTokens += record.TotalTokens;
            cost += RawCost(record, rateResolver);
        }

        return new AgentUsageSummaryTotalsResponse
        {
            RunCount = runCount,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            ReasoningTokens = reasoningTokens,
            TotalTokens = totalTokens,
            EstimatedCostUsd = Round(cost),
            Currency = CurrencyUsd
        };
    }

    public static IReadOnlyList<AgentUsageProviderTotalsResponse> ToByProvider(this IReadOnlyList<TokenUsageAggregateRecord> records, IUsageRateResolver rateResolver)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(rateResolver);

        // Fold the (model, provider, day) buckets down to one row per provider — summed across days and models. Ordinal
        // keying keeps the canonical lowercase provider labels distinct without culture surprises; token sums stay long (a
        // wide range can exceed int). The bucket list is small (bounded by retention horizon x models x providers), so the
        // in-memory group is cheap. Per-provider cost sums the raw per-bucket costs then rounds once (no compounding).
        // Ordered biggest-consumer first (total tokens), then provider name for a stable tie order.
        return records
              .GroupBy(record => record.Provider, StringComparer.Ordinal)
              .Select(group => new AgentUsageProviderTotalsResponse
              {
                  Provider = group.Key,
                  RunCount = group.Sum(record => record.RunCount),
                  PromptTokens = group.Sum(record => record.PromptTokens),
                  CompletionTokens = group.Sum(record => record.CompletionTokens),
                  ReasoningTokens = group.Sum(record => record.ReasoningTokens),
                  TotalTokens = group.Sum(record => record.TotalTokens),
                  EstimatedCostUsd = Round(group.Sum(record => RawCost(record, rateResolver))),
                  Currency = CurrencyUsd
              })
              .OrderByDescending(totals => totals.TotalTokens)
              .ThenBy(totals => totals.Provider, StringComparer.Ordinal)
              .ToArray();
    }

    /// <summary>
    ///     Raw (unrounded) USD cost of one bucket: reasoning tokens bill at the OUTPUT rate (they are model output), and
    ///     rates are per 1M tokens so each term divides by 1,000,000. A free / unpriced (provider, model) resolves to a
    ///     zero rate → zero cost. Kept unrounded so callers can accumulate and round once.
    /// </summary>
    private static double RawCost(TokenUsageAggregateRecord record, IUsageRateResolver rateResolver)
    {
        var rate = rateResolver.Resolve(record.Provider, record.ModelName);
        return (rate.InputPer1M / 1_000_000d * record.PromptTokens)
               + (rate.OutputPer1M / 1_000_000d * (record.CompletionTokens + record.ReasoningTokens));
    }

    private static double Round(double cost)
    {
        return Math.Round(cost, CostDecimals, MidpointRounding.AwayFromZero);
    }
}
