namespace XE_Local_AI_Engine.Client.Endpoints.Agents.V1;

/// <summary>
///     Query for the token-usage summary. The optional half-open range bounds the aggregation on the row's
///     <c>CreatedAtUtc</c> (unix-ms): <see cref="FromEpochMs" /> is lower-inclusive, <see cref="ToEpochMs" /> is
///     upper-exclusive; either may be omitted for an open end (subject to the retention horizon). A body-less GET with no
///     range summarizes every retained run-envelope row. Both bind from the query string.
/// </summary>
public sealed class AgentUsageSummaryRequest
{
    /// <summary>Lower-inclusive bound on <c>CreatedAtUtc</c> (unix-ms). Null leaves the lower end open.</summary>
    public long? FromEpochMs { get; init; }

    /// <summary>Upper-exclusive bound on <c>CreatedAtUtc</c> (unix-ms). Null leaves the upper end open.</summary>
    public long? ToEpochMs { get; init; }
}

/// <summary>
///     One aggregation bucket: summed token usage for a single (model, provider, UTC day) triple. Metadata ONLY — token
///     counts, no message content.
/// </summary>
public sealed class AgentUsageSummaryBucketResponse
{
    /// <summary>Model the runs executed on (part of the group key); may be empty for an envelope written without one.</summary>
    public required string ModelName { get; init; }

    /// <summary>
    ///     Fine-grained runtime provider that served the runs (part of the group key): <c>local</c>, <c>ollama</c>,
    ///     <c>codex</c>, <c>azure</c>, or <c>unknown</c>.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>Unix-ms timestamp of the UTC midnight opening the day bucket (the day-truncated group key).</summary>
    public required long DayStartUtcMs { get; init; }

    /// <summary>Number of run-envelope rows in the bucket.</summary>
    public required int RunCount { get; init; }

    /// <summary>Summed prompt/input tokens (a run reporting no usage contributes 0).</summary>
    public required long PromptTokens { get; init; }

    /// <summary>Summed completion/output tokens (a run reporting no usage contributes 0).</summary>
    public required long CompletionTokens { get; init; }

    /// <summary>Summed reasoning tokens (a run reporting no usage contributes 0).</summary>
    public required long ReasoningTokens { get; init; }

    /// <summary>Summed total tokens reported by the model (a run reporting no usage contributes 0).</summary>
    public required long TotalTokens { get; init; }

    /// <summary>
    ///     Server-computed estimated cost of the bucket in <see cref="Currency" />, rounded to 4 decimals. Priced as
    ///     <c>inputRate * promptTokens + outputRate * (completionTokens + reasoningTokens)</c> (reasoning bills as output),
    ///     with rates from the operator override or the built-in default table. Local runtimes and unpriced models are 0.
    /// </summary>
    public required double EstimatedCostUsd { get; init; }

    /// <summary>ISO 4217 currency of <see cref="EstimatedCostUsd" />. Always <c>USD</c>.</summary>
    public required string Currency { get; init; }
}

/// <summary>Grand totals across every returned bucket, so a caller need not re-sum the page client-side.</summary>
public sealed class AgentUsageSummaryTotalsResponse
{
    public required int RunCount { get; init; }

    public required long PromptTokens { get; init; }

    public required long CompletionTokens { get; init; }

    public required long ReasoningTokens { get; init; }

    public required long TotalTokens { get; init; }

    /// <summary>Estimated cost across every bucket in <see cref="Currency" />, rounded to 4 decimals (0 when unpriced).</summary>
    public required double EstimatedCostUsd { get; init; }

    /// <summary>ISO 4217 currency of <see cref="EstimatedCostUsd" />. Always <c>USD</c>.</summary>
    public required string Currency { get; init; }
}

/// <summary>
///     Per-provider rollup: token usage summed across every day and model for one fine-grained provider, so a caller can
///     render a provider breakdown without re-folding the day buckets. Metadata ONLY — token counts, no message content.
/// </summary>
public sealed class AgentUsageProviderTotalsResponse
{
    /// <summary>Fine-grained runtime provider: <c>local</c>, <c>ollama</c>, <c>codex</c>, <c>azure</c>, or <c>unknown</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>Number of run-envelope rows attributed to the provider.</summary>
    public required int RunCount { get; init; }

    /// <summary>Summed prompt/input tokens for the provider (a run reporting no usage contributes 0).</summary>
    public required long PromptTokens { get; init; }

    /// <summary>Summed completion/output tokens for the provider (a run reporting no usage contributes 0).</summary>
    public required long CompletionTokens { get; init; }

    /// <summary>Summed reasoning tokens for the provider (a run reporting no usage contributes 0).</summary>
    public required long ReasoningTokens { get; init; }

    /// <summary>Summed total tokens for the provider (a run reporting no usage contributes 0).</summary>
    public required long TotalTokens { get; init; }

    /// <summary>Estimated cost for the provider in <see cref="Currency" />, rounded to 4 decimals (0 for free/unpriced providers).</summary>
    public required double EstimatedCostUsd { get; init; }

    /// <summary>ISO 4217 currency of <see cref="EstimatedCostUsd" />. Always <c>USD</c>.</summary>
    public required string Currency { get; init; }
}

/// <summary>
///     Response for <c>GET agents/usage-summary</c>. Buckets are newest-day-first, then provider, then model name.
///     Metadata ONLY — token counts, no message content.
/// </summary>
public sealed class AgentUsageSummaryResponse
{
    /// <summary>Per-(model, provider, UTC day) usage buckets, ordered newest day first, then provider, then model name.</summary>
    public required IReadOnlyList<AgentUsageSummaryBucketResponse> Items { get; init; }

    /// <summary>Grand totals across <see cref="Items" />.</summary>
    public required AgentUsageSummaryTotalsResponse Totals { get; init; }

    /// <summary>Per-provider rollup across <see cref="Items" />, ordered by descending total tokens then provider name.</summary>
    public required IReadOnlyList<AgentUsageProviderTotalsResponse> ByProvider { get; init; }

    /// <summary>
    ///     The retention window (days) after which run-envelope rows are aged out of <c>agent_execution_logs</c>. This
    ///     summary only covers the retained horizon; a longer-term durable rollup is a follow-up.
    /// </summary>
    public required int RetentionDays { get; init; }
}
