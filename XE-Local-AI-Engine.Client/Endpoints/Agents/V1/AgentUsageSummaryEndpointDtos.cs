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
///     One aggregation bucket: summed token usage for a single (model, UTC day) pair. Metadata ONLY — token counts, no
///     message content. The <c>agent_execution_logs</c> table has no provider column, so <see cref="ModelName" /> is the
///     only usage dimension (see <see cref="AgentUsageSummaryResponse.GroupedByModelOnly" />).
/// </summary>
public sealed class AgentUsageSummaryBucketResponse
{
    /// <summary>Model the runs executed on (the group key); may be empty for an envelope written without one.</summary>
    public required string ModelName { get; init; }

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
}

/// <summary>Grand totals across every returned bucket, so a caller need not re-sum the page client-side.</summary>
public sealed class AgentUsageSummaryTotalsResponse
{
    public required int RunCount { get; init; }

    public required long PromptTokens { get; init; }

    public required long CompletionTokens { get; init; }

    public required long ReasoningTokens { get; init; }

    public required long TotalTokens { get; init; }
}

/// <summary>
///     Response for <c>GET agents/usage-summary</c>. Buckets are newest-day-first, then model name. Metadata ONLY — token
///     counts, no message content.
/// </summary>
public sealed class AgentUsageSummaryResponse
{
    /// <summary>Per-(model, UTC day) usage buckets, ordered newest day first then model name.</summary>
    public required IReadOnlyList<AgentUsageSummaryBucketResponse> Items { get; init; }

    /// <summary>Grand totals across <see cref="Items" />.</summary>
    public required AgentUsageSummaryTotalsResponse Totals { get; init; }

    /// <summary>
    ///     Always <see langword="true" />: the store has no provider column, so usage is grouped by model id only. A
    ///     per-provider breakdown is a follow-up that would require persisting the provider on the run-envelope row.
    /// </summary>
    public required bool GroupedByModelOnly { get; init; }

    /// <summary>
    ///     The retention window (days) after which run-envelope rows are aged out of <c>agent_execution_logs</c>. This
    ///     summary only covers the retained horizon; a longer-term durable rollup is a follow-up.
    /// </summary>
    public required int RetentionDays { get; init; }
}
