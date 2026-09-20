namespace XE_Local_AI_Engine.Client.Services.Analysis;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     analysis staging orchestration: read the per-agent feedback aggregate, gate it, ask the analysis agent for
///     candidate actions, reject and de-duplicate, and persist the survivors for human review.
/// </summary>
/// <remarks>
///     The aggregate comes from feedback insights and is gated on the "never act on n=1" threshold; a candidate without valid evidence, or one that
///     near-duplicates an existing action, is rejected. Survivors are persisted as <c>Suggested</c>/<c>Analysis</c> actions and are inert by
///     construction — the resolver injects only <c>Enabled</c> actions — so promotion to <c>Enabled</c> stays a separate human step.
/// </remarks>
public interface IPlaybookAnalysisService
{
    /// <summary>Runs analysis for the agent and persists the resulting <c>Suggested</c> actions.</summary>
    /// <remarks>
    ///     Returns an outcome whose <see cref="PlaybookAnalysisOutcome.AgentExists" /> is <c>false</c> when no agent has that id (the
    ///     endpoint maps that to 404), and whose <see cref="PlaybookAnalysisOutcome.MeetsThreshold" /> is <c>false</c> when the feedback is
    ///     below the occurrence threshold (no agent is invoked and nothing is written).
    /// </remarks>
    Task<PlaybookAnalysisOutcome> AnalyzeAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);
}

/// <summary>The result of an analysis run. The counts let the operator see what was proposed vs kept vs filtered.</summary>
public sealed class PlaybookAnalysisOutcome
{
    public required bool AgentExists { get; init; }

    public required bool MeetsThreshold { get; init; }

    public required IReadOnlyList<PlaybookActionRecord> CreatedSuggestions { get; init; }

    public required int ProposedCount { get; init; }

    public required int RejectedCount { get; init; }

    public required int DuplicateCount { get; init; }
}
