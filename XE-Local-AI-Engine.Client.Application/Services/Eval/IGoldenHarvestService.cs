namespace XE_Local_AI_Engine.Client.Services.Eval;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     On-demand golden harvester (deterministic, no model — D1). Scans an agent's most-recent thumbs-up assistant
///     turns and proposes <see cref="GoldenConversationSource.Harvested" /> candidates (the lead-up turns + the
///     operator-approved answer seeded as a rubric, D2), staged inert until the operator approves them into the active
///     golden set. Dedups against already-harvested source messages and caps the run server-side. No data leaves the
///     node and no LLM is invoked; only counts/ids are ever logged.
/// </summary>
public interface IGoldenHarvestService
{
    /// <summary>
    ///     Harvests golden candidates for <paramref name="agentId" />, returning a per-run <see cref="GoldenHarvestOutcome" />.
    ///     When the agent does not exist the outcome reports <see cref="GoldenHarvestOutcome.AgentExists" /> = <c>false</c>
    ///     with zero counts (the endpoint maps it to 404).
    /// </summary>
    Task<GoldenHarvestOutcome> HarvestAsync(Guid agentId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Per-run summary of a golden harvest: whether the agent existed, how many thumbs-up sources were scanned, and how
///     the candidates split across created / already-harvested (duplicate) / skipped (no lead-up user turn or rejected at
///     the create boundary). Counts only — no turn/answer text.
/// </summary>
public sealed record GoldenHarvestOutcome(
    bool AgentExists,
    int ThumbsUpScanned,
    int CreatedCount,
    int DuplicateCount,
    int SkippedCount);
