namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Two windowed feedback counts (Playbook P5 cohort monitoring) for one agent, split at an action's
///     <c>EnabledAtUtc</c>: feedback created before the action was enabled (the baseline) versus after (the cohort).
///     Down-rate (<c>Down / Total</c>) is derived in the application service so the store stays a pure count source
///     (÷0 → 0 is a service concern). Plaintext only — no encrypted column is read.
/// </summary>
public sealed record CohortComparison(int BeforeTotal, int BeforeDown, int AfterTotal, int AfterDown);

/// <summary>
///     Read-only cohort monitor over the node-local per-message feedback (Playbook P5). Splits the
///     <c>message_feedback</c> rows for one agent (joined to <c>conversations.agent_definition_id</c>) into a
///     before/after window relative to an action's <c>EnabledAtUtc</c>, computed on read — no snapshot table.
///     Mirrors <see cref="IFeedbackInsightsStore" />: pure analytics, node-local, nothing is written.
/// </summary>
public interface IPlaybookMonitorStore
{
    /// <summary>
    ///     Returns the before/after feedback counts for <paramref name="agentDefinitionId" /> split at
    ///     <paramref name="enabledAtUtc" /> (feedback with <c>created_at_utc &lt; enabledAtUtc</c> is "before", the rest
    ///     is "after"). When <paramref name="toolScope" /> is non-null the counts are restricted to conversations that
    ///     recorded a <c>tool_events</c> row for that tool, using <c>COUNT(DISTINCT message_id)</c> (the documented P2
    ///     conversation-level attribution limit). Only non-purged conversations are counted; archived are included. All
    ///     columns read are plaintext, so no decryption is involved.
    /// </summary>
    Task<CohortComparison> GetCohortComparisonAsync(Guid agentDefinitionId, long enabledAtUtc, string? toolScope, CancellationToken cancellationToken = default);
}
