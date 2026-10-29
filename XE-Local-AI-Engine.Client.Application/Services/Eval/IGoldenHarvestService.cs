namespace XE_Local_AI_Engine.Client.Services.Eval;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     On-demand golden harvester, deterministic and model-free: it scans an agent's most-recent thumbs-up assistant
///     turns and proposes <see cref="GoldenConversationSource.Harvested" /> candidates, staged inert.
/// </summary>
/// <remarks>
///     A candidate is the lead-up turns plus the operator-approved answer seeded as a rubric, on the judge path, and
///     the operator approves it into the active set. It dedups against already-harvested source messages and caps
///     the run server-side. No data leaves the node and no LLM is invoked; only counts and ids are ever logged.
/// </remarks>
public interface IGoldenHarvestService
{
    /// <summary>
    ///     Harvests golden candidates for <paramref name="agentId" />, returning a per-run <see cref="GoldenHarvestOutcome" />.
    /// </summary>
    /// <exception cref="Agents.AgentDefinitionNotFoundException">No agent definition has <paramref name="agentId" />.</exception>
    Task<GoldenHarvestOutcome> HarvestAsync(Guid agentId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Per-run summary of a golden harvest: how many thumbs-up sources were scanned, and
///     how the candidates split across created, already-harvested and skipped. Counts only, never turn text.
/// </summary>
public sealed class GoldenHarvestOutcome
{
    public required int ThumbsUpScanned { get; init; }

    public required int CreatedCount { get; init; }

    public required int DuplicateCount { get; init; }

    public required int SkippedCount { get; init; }
}
