namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Two windowed feedback counts for one agent's cohort monitoring, split at an action's <c>EnabledAtUtc</c>.
/// </summary>
/// <remarks>
///     Feedback created before the action was enabled is the baseline, after it the cohort. Down-rate
///     (<c>Down / Total</c>) is derived in the application service so the store stays a pure count source (÷0 → 0 is
///     a service concern). Plaintext only — no encrypted column is read.
/// </remarks>
public sealed class CohortComparison
{
    public required int BeforeTotal { get; init; }

    public required int BeforeDown { get; init; }

    public required int AfterTotal { get; init; }

    public required int AfterDown { get; init; }
}

/// <summary>
///     Read-only cohort monitor over the node-local per-message feedback.
/// </summary>
/// <remarks>
///     Splits the <c>message_feedback</c> rows for one agent (joined to <c>conversations.agent_definition_id</c>) into
///     a before/after window relative to an action's <c>EnabledAtUtc</c>, computed on read — no snapshot table.
///     Mirrors <see cref="IFeedbackInsightsStore" />: pure analytics, node-local, nothing is written.
/// </remarks>
public interface IPlaybookMonitorStore
{
    /// <summary>
    ///     Returns the before/after feedback counts for <paramref name="agentDefinitionId" />, split at
    ///     <paramref name="enabledAtUtc" />: <c>created_at_utc &lt; enabledAtUtc</c> is "before", the rest is "after".
    /// </summary>
    /// <remarks>
    ///     A non-null <paramref name="toolScope" /> restricts the counts to conversations that recorded a
    ///     <c>tool_events</c> row for that tool, using <c>COUNT(DISTINCT message_id)</c> — the conversation-level
    ///     attribution limit. Only non-purged conversations are counted; archived are included. All columns read are
    ///     plaintext, so no decryption is involved.
    /// </remarks>
    Task<CohortComparison> GetCohortComparisonAsync(Guid agentDefinitionId, long enabledAtUtc, string? toolScope, CancellationToken cancellationToken = default);
}
