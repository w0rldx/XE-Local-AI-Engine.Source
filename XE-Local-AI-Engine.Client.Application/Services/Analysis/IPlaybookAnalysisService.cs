namespace XE_Local_AI_Engine.Client.Services.Analysis;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     analysis staging orchestration: on demand, read the per-agent feedback aggregate (feedback insights), gate it on the
///     "never act on n=1" threshold, ask the analysis agent for candidate actions, reject any without valid evidence,
///     drop near-duplicates of existing actions, and persist the survivors as <c>Suggested</c>/<c>Analysis</c> actions
///     for human review. Suggestions are inert by construction (the resolver injects only <c>Enabled</c> actions);
///     promotion to <c>Enabled</c> stays a separate human step.
/// </summary>
public interface IPlaybookAnalysisService
{
    /// <summary>
    ///     Runs analysis for the agent and persists the resulting <c>Suggested</c> actions. Returns an outcome whose
    ///     <see cref="PlaybookAnalysisOutcome.AgentExists" /> is <c>false</c> when no agent has that id (the endpoint
    ///     maps that to 404), and whose <see cref="PlaybookAnalysisOutcome.MeetsThreshold" /> is <c>false</c> when the
    ///     feedback is below the occurrence threshold (no agent is invoked and nothing is written).
    /// </summary>
    Task<PlaybookAnalysisOutcome> AnalyzeAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);
}

/// <summary>The result of an analysis run. The counts let the operator see what was proposed vs kept vs filtered.</summary>
public sealed record PlaybookAnalysisOutcome(
    bool AgentExists,
    bool MeetsThreshold,
    IReadOnlyList<PlaybookActionRecord> CreatedSuggestions,
    int ProposedCount,
    int RejectedCount,
    int DuplicateCount);
