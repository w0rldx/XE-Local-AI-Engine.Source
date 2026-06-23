namespace XE_Local_AI_Engine.Client.Services.Analysis.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Insights;

internal sealed class PlaybookAnalysisService(
    IFeedbackInsightsService insightsService,
    IPlaybookAnalysisAgent analysisAgent,
    IPlaybookActionService playbookActionService,
    IOptions<PlaybookAnalysisOptions> options,
    ILogger<PlaybookAnalysisService> logger) : IPlaybookAnalysisService
{
    private readonly IPlaybookAnalysisAgent _analysisAgent = analysisAgent ?? throw new ArgumentNullException(nameof(analysisAgent));
    private readonly IFeedbackInsightsService _insightsService = insightsService ?? throw new ArgumentNullException(nameof(insightsService));
    private readonly ILogger<PlaybookAnalysisService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly PlaybookAnalysisOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IPlaybookActionService _playbookActionService = playbookActionService ?? throw new ArgumentNullException(nameof(playbookActionService));

    public async Task<PlaybookAnalysisOutcome> AnalyzeAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default)
    {
        // Read the feedback-insights aggregate (reuse — never re-derive). Null means the agent does not exist → the endpoint 404s.
        var insights = await _insightsService.GetAgentFeedbackInsightsAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        if (insights is null)
        {
            return new PlaybookAnalysisOutcome(AgentExists: false, MeetsThreshold: false, [], ProposedCount: 0, RejectedCount: 0, DuplicateCount: 0);
        }

        // Never act on a single signal: if the aggregate is below the occurrence threshold, don't even invoke the
        // model. A sub-threshold run writes nothing.
        if (!insights.Overall.MeetsThreshold)
        {
            _logger.LogInformation("Skipping playbook analysis for agent {AgentId}: feedback below the occurrence threshold.", agentDefinitionId);
            return new PlaybookAnalysisOutcome(AgentExists: true, MeetsThreshold: false, [], ProposedCount: 0, RejectedCount: 0, DuplicateCount: 0);
        }

        var proposals = await _analysisAgent.ProposeAsync(insights, cancellationToken).ConfigureAwait(false);

        // Enforce the proposal cap server-side as a hard limit (the prompt only requests it). This bounds review load
        // and prompt bloat regardless of what any agent implementation returns.
        if (proposals.Count > _options.MaxProposals)
        {
            _logger.LogWarning("Analysis agent returned {Count} proposals for agent {AgentId}; capping to {Max}.", proposals.Count, agentDefinitionId, _options.MaxProposals);
            proposals = [.. proposals.Take(_options.MaxProposals)];
        }

        var evidenceIds = BuildEvidenceIdSet(insights);
        var existing = await _playbookActionService.ListByAgentAsync(agentDefinitionId, cancellationToken).ConfigureAwait(false);
        var dedupKeys = BuildDedupKeys(existing);

        var created = new List<PlaybookActionRecord>();
        var rejected = 0;
        var duplicates = 0;

        foreach (var proposal in proposals)
        {
            if (!IsValidProposal(proposal, evidenceIds))
            {
                rejected++;
                // An action with no (or invented) evidence hallucinates a root cause; drop it, never store it.
                _logger.LogWarning("Rejected an analysis proposal for agent {AgentId} (missing/invalid evidence or confidence).", agentDefinitionId);
                continue;
            }

            if (!dedupKeys.Add(DedupKey(proposal.Behavior, proposal.Scope)))
            {
                // Matches an existing Suggested/Enabled action (or an earlier proposal in this same run) — skip it so
                // repeat analysis runs don't flood the staging list.
                duplicates++;
                continue;
            }

            var record = await _playbookActionService.CreateAnalysisSuggestionAsync(new PlaybookAnalysisSuggestionInput(agentDefinitionId,
                    proposal.Behavior,
                    proposal.TriggerCondition,
                    proposal.Scope,
                    _options.SuggestionPriority,
                    proposal.SourceFeedbackIds,
                    proposal.Confidence),
                cancellationToken).ConfigureAwait(false);

            created.Add(record);
        }

        _logger.LogInformation("Playbook analysis for agent {AgentId}: proposed {Proposed}, kept {Kept}, rejected {Rejected}, duplicates {Duplicates}.",
            agentDefinitionId, proposals.Count, created.Count, rejected, duplicates);

        return new PlaybookAnalysisOutcome(AgentExists: true, MeetsThreshold: true, created, proposals.Count, rejected, duplicates);
    }

    private static bool IsValidProposal(ProposedPlaybookAction proposal, HashSet<Guid> evidenceIds)
    {
        if (string.IsNullOrWhiteSpace(proposal.Behavior))
        {
            return false;
        }

        if (proposal.SourceFeedbackIds is null || proposal.SourceFeedbackIds.Count == 0)
        {
            return false;
        }

        if (double.IsNaN(proposal.Confidence) || proposal.Confidence is < 0d or > 1d)
        {
            return false;
        }

        // Anti-hallucination: every cited id must be present in the aggregate the agent was handed. The model cannot
        // invent evidence it was never shown.
        return proposal.SourceFeedbackIds.All(evidenceIds.Contains);
    }

    private static HashSet<Guid> BuildEvidenceIdSet(FeedbackInsightsResult insights)
    {
        return insights.Exemplars
                       .SelectMany(static exemplar => new[]
                       {
                           exemplar.MessageId,
                           exemplar.ConversationId
                       })
                       .ToHashSet();
    }

    private static HashSet<(string Scope, string Behavior)> BuildDedupKeys(IReadOnlyList<PlaybookActionRecord> existing)
    {
        // Only live actions matter for dedup: a rejected (Archived) or disabled action should not block re-proposing.
        return existing
               .Where(static action => action.State is PlaybookActionState.Suggested or PlaybookActionState.Enabled)
               .Select(static action => DedupKey(action.Behavior, action.Scope))
               .ToHashSet();
    }

    private static (string Scope, string Behavior) DedupKey(string behavior, string? scope)
    {
        // A (scope, behavior) tuple keys the dedup set, so there is no separator char to collide with content.
        return (NormalizeText(scope), NormalizeText(behavior));
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Uppercase-normalize (CA1308) and collapse all whitespace so trivially-different phrasings dedupe.
        return string.Join(separator: ' ', value.ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
