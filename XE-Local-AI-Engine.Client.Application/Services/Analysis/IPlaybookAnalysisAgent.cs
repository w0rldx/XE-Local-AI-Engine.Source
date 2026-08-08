namespace XE_Local_AI_Engine.Client.Services.Analysis;

using XE_Local_AI_Engine.Client.Services.Insights;

/// <summary>
///     The analysis staging analysis agent (the AI surface). Reads the per-agent feedback aggregate (feedback insights) and
///     proposes structured playbook actions, each forced to cite which feedback drove it (
///     <see
///         cref="ProposedPlaybookAction.SourceFeedbackIds" />
///     ) and how confident it is. The agent only PROPOSES — it
///     persists nothing and decides nothing; the service validates the evidence and writes <c>Suggested</c> actions for
///     human review. Implementations run a node-local model (never the cloud-capable shared chat client) so feedback
///     comments never leave the node (privacy invariant). The seam keeps the model off the hot send path and lets
///     tests substitute a deterministic fake (no Ollama in CI).
/// </summary>
public interface IPlaybookAnalysisAgent
{
    /// <summary>Proposes candidate playbook actions for the agent described by <paramref name="aggregate" />. May return an empty list.</summary>
    Task<IReadOnlyList<ProposedPlaybookAction>> ProposeAsync(FeedbackInsightsResult aggregate, CancellationToken cancellationToken = default);
}

/// <summary>
///     A single proposed action from the analysis agent — structured (trigger + behavior + scope) so it can be measured,
///     deduped, and shown with provenance. <see cref="SourceFeedbackIds" /> is the evidence the service validates
///     against the aggregate it handed the agent (an action citing ids the aggregate does not contain is rejected).
/// </summary>
public sealed record ProposedPlaybookAction(
    string Behavior,
    string? TriggerCondition,
    string? Scope,
    IReadOnlyList<Guid> SourceFeedbackIds,
    double Confidence);
