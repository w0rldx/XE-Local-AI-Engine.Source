namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal static class UsageSummaryMapper
{
    public static AgentUsageSummaryBucketResponse ToResponse(this TokenUsageAggregateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Pass-through of the aggregate bucket. All fields are token counts / ids — no content to redact.
        return new AgentUsageSummaryBucketResponse
        {
            ModelName = record.ModelName,
            DayStartUtcMs = record.DayStartUtcMs,
            RunCount = record.RunCount,
            PromptTokens = record.PromptTokens,
            CompletionTokens = record.CompletionTokens,
            ReasoningTokens = record.ReasoningTokens,
            TotalTokens = record.TotalTokens
        };
    }

    public static AgentUsageSummaryTotalsResponse ToTotals(this IReadOnlyList<TokenUsageAggregateRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        // Fold the buckets once so the caller need not re-sum. Token sums stay long (a wide range can exceed int).
        var runCount = 0;
        var promptTokens = 0L;
        var completionTokens = 0L;
        var reasoningTokens = 0L;
        var totalTokens = 0L;

        foreach (var record in records)
        {
            runCount += record.RunCount;
            promptTokens += record.PromptTokens;
            completionTokens += record.CompletionTokens;
            reasoningTokens += record.ReasoningTokens;
            totalTokens += record.TotalTokens;
        }

        return new AgentUsageSummaryTotalsResponse
        {
            RunCount = runCount,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            ReasoningTokens = reasoningTokens,
            TotalTokens = totalTokens
        };
    }
}
