namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Default <see cref="IMemoryExtractionService" />. Gates temporary conversations BEFORE any model call (the single
///     write-only suppression point — the retrieval path is never gated), no-ops when no node-local extraction model is
///     configured, asks the node-local extraction agent for candidate memories, drops near-duplicates of the agent's
///     existing <c>Suggested</c>/<c>Enabled</c> memories, and persists the survivors as <c>Suggested</c>/<c>Extracted</c>
///     actions for human review. Candidates are inert by construction (the resolver injects only <c>Enabled</c> actions);
///     promotion stays an eval-gated, human step.
/// </summary>
internal sealed class MemoryExtractionService(
    IMemoryExtractionAgent extractionAgent,
    IPlaybookActionStore playbookActionStore,
    IOptions<MemoryExtractionOptions> options,
    ILogger<MemoryExtractionService> logger) : IMemoryExtractionService
{
    private readonly IMemoryExtractionAgent _extractionAgent = extractionAgent ?? throw new ArgumentNullException(nameof(extractionAgent));
    private readonly ILogger<MemoryExtractionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly MemoryExtractionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IPlaybookActionStore _playbookActionStore = playbookActionStore ?? throw new ArgumentNullException(nameof(playbookActionStore));

    public async Task<MemoryExtractionOutcome> ExtractAsync(MemoryExtractionRunInput run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Temp-chat gate FIRST: a memory-excluded conversation never extracts — no model call, no candidate.
        // This is the SINGLE write-only enforcement point; retrieval is never gated on this flag, so a temp chat still
        // gets existing enabled memory injected.
        if (run.MemoryExcluded)
        {
            return MemoryExtractionOutcome.SuppressedByTempChat();
        }

        // Disabled gate: no node-local extraction model configured => clean no-op (CI-safe, mirrors the embedding
        // ranker). The agent guards this too, but short-circuiting here also avoids the existing-actions read.
        if (string.IsNullOrWhiteSpace(_options.ExtractionModelName))
        {
            return MemoryExtractionOutcome.NoModelConfigured();
        }

        var proposals = await _extractionAgent.ProposeAsync(run, cancellationToken).ConfigureAwait(false);

        // Enforce the candidate cap server-side as a hard limit (the prompt only requests it). This bounds review load
        // regardless of what the model returns.
        if (proposals.Count > _options.MaxCandidates)
        {
            proposals = [.. proposals.Take(_options.MaxCandidates)];
        }

        if (proposals.Count == 0)
        {
            return new MemoryExtractionOutcome(MemoryExcluded: false, ModelConfigured: true, [], ProposedCount: 0, DuplicateCount: 0);
        }

        // Dedup against the agent's existing live memories so repeat runs don't flood the staging list. The lessons text
        // is held only in memory for this compare — never written to the execution log.
        var existing = await _playbookActionStore.ListByAgentAsync(run.AgentDefinitionId, cancellationToken).ConfigureAwait(false);
        var dedupKeys = BuildDedupKeys(existing);

        var sourceFeedbackIds = new[]
        {
            run.ConversationId,
            run.AssistantMessageId
        };

        var created = new List<PlaybookActionRecord>();
        var duplicates = 0;

        foreach (var proposal in proposals)
        {
            if (string.IsNullOrWhiteSpace(proposal.Behavior))
            {
                continue;
            }

            if (!dedupKeys.Add(DedupKey(proposal.Behavior, proposal.Scope)))
            {
                // Matches an existing Suggested/Enabled memory (or an earlier candidate in this same run) — skip it.
                duplicates++;
                continue;
            }

            var record = await _playbookActionStore.AddAsync(new PlaybookActionInput(run.AgentDefinitionId,
                    PlaybookActionState.Suggested,
                    PlaybookActionSource.Extracted,
                    proposal.TriggerCondition,
                    proposal.Behavior,
                    proposal.Scope.ToString(),
                    _options.CandidatePriority,
                    sourceFeedbackIds,
                    proposal.Confidence,
                    MemoryScope: proposal.Scope),
                cancellationToken).ConfigureAwait(false);

            created.Add(record);
        }

        _logger.LogInformation("Memory extraction for agent {AgentId}: proposed {Proposed}, kept {Kept}, duplicates {Duplicates}.",
            run.AgentDefinitionId, proposals.Count, created.Count, duplicates);

        return new MemoryExtractionOutcome(MemoryExcluded: false, ModelConfigured: true, created, proposals.Count, duplicates);
    }

    private static HashSet<(MemoryScope Scope, string Behavior)> BuildDedupKeys(IReadOnlyList<PlaybookActionRecord> existing)
    {
        // Only live actions matter for dedup: a rejected (Archived) or disabled action should not block re-proposing.
        // A legacy untyped action (null MemoryScope) keys under Procedural so a manually-authored equivalent still
        // dedupes a procedural candidate.
        return existing
               .Where(static action => action.State is PlaybookActionState.Suggested or PlaybookActionState.Enabled)
               .Select(static action => DedupKey(action.Behavior, action.MemoryScope ?? MemoryScope.Procedural))
               .ToHashSet();
    }

    private static (MemoryScope Scope, string Behavior) DedupKey(string behavior, MemoryScope scope)
    {
        // A (scope, behavior) tuple keys the dedup set, so there is no separator char to collide with content.
        return (scope, NormalizeText(behavior));
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
