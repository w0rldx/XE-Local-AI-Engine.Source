namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

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
    IMemorySemanticDeduplicator semanticDeduplicator,
    IOptions<MemoryExtractionOptions> options,
    ILogger<MemoryExtractionService> logger) : IMemoryExtractionService
{
    private readonly IMemoryExtractionAgent _extractionAgent = extractionAgent ?? throw new ArgumentNullException(nameof(extractionAgent));
    private readonly ILogger<MemoryExtractionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly MemoryExtractionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly IPlaybookActionStore _playbookActionStore = playbookActionStore ?? throw new ArgumentNullException(nameof(playbookActionStore));
    private readonly IMemorySemanticDeduplicator _semanticDeduplicator = semanticDeduplicator ?? throw new ArgumentNullException(nameof(semanticDeduplicator));

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

        var duplicates = 0;
        var rejected = 0;

        // PASS 1 — secret scan + lexical dedup. The exact normalized-text key is the fast/robust baseline; survivors are
        // collected (not yet persisted) so PASS 2 can layer semantic dedup on top before anything is written.
        var accepted = new List<AcceptedCandidate>();
        foreach (var proposal in proposals)
        {
            if (string.IsNullOrWhiteSpace(proposal.Behavior))
            {
                continue;
            }

            // Secret scan BOTH free-text fields the model produced before they are persisted for human review — the
            // extraction agent reads raw conversation content, so a leaked PEM/PAT/JWT/high-entropy secret must be
            // rejected or redacted exactly as the AgentHome proposal path handles it. The scanner treats its `content`
            // argument as redactable; the metadata/evidence arguments are the reject-only channels, so pass empties.
            var behaviorScan = MemoryProposalSecretScanner.Scan(type: string.Empty,
                operation: string.Empty,
                proposal.Behavior,
                evidence: [],
                confidence: string.Empty);
            var triggerScan = MemoryProposalSecretScanner.Scan(type: string.Empty,
                operation: string.Empty,
                proposal.TriggerCondition ?? string.Empty,
                evidence: [],
                confidence: string.Empty);

            if (behaviorScan.ShouldReject || triggerScan.ShouldReject)
            {
                // Unredactable secret (PEM/service-account block) in either field — drop the whole candidate.
                rejected++;
                continue;
            }

            var behavior = behaviorScan.RedactedContent ?? proposal.Behavior;
            var triggerCondition = proposal.TriggerCondition is null ? null : (triggerScan.RedactedContent ?? proposal.TriggerCondition);

            if (!dedupKeys.Add(DedupKey(behavior, proposal.Scope)))
            {
                // Matches an existing Suggested/Enabled memory (or an earlier candidate in this same run) — skip it.
                duplicates++;
                continue;
            }

            accepted.Add(new AcceptedCandidate(behavior, triggerCondition, proposal.Scope, proposal.Confidence));
        }

        // PASS 2 — semantic (embedding-cosine) dedup ON TOP OF lexical: drop a lexically-distinct candidate that is a
        // paraphrase of an existing live memory. Gated on a confident node-local embedding model; on no model / any
        // embedding failure it returns NotApplied and every lexically-surviving candidate is kept (no mass-dedup on
        // outage). The embed text never leaves the node and is never persisted (see MemorySemanticDeduplicator).
        var semantic = await _semanticDeduplicator.FindSemanticDuplicatesAsync(BuildSemanticExisting(existing),
            [.. accepted.Select(static candidate => new MemoryDedupCandidate(candidate.Scope, candidate.Behavior))],
            cancellationToken).ConfigureAwait(false);

        var created = new List<PlaybookActionRecord>();
        var semanticDuplicates = 0;
        for (var index = 0; index < accepted.Count; index++)
        {
            if (semantic.Applied && semantic.DuplicateIndexes.Contains(index))
            {
                // A cosine-near paraphrase of an existing live memory (same scope) — treat as a duplicate, like lexical.
                duplicates++;
                semanticDuplicates++;
                continue;
            }

            var candidate = accepted[index];
            var record = await _playbookActionStore.AddAsync(new PlaybookActionInput(run.AgentDefinitionId,
                    PlaybookActionState.Suggested,
                    PlaybookActionSource.Extracted,
                    candidate.TriggerCondition,
                    candidate.Behavior,
                    candidate.Scope.ToString(),
                    _options.CandidatePriority,
                    sourceFeedbackIds,
                    candidate.Confidence,
                    MemoryScope: candidate.Scope),
                cancellationToken).ConfigureAwait(false);

            created.Add(record);
        }

        _logger.LogInformation("Memory extraction for agent {AgentId}: proposed {Proposed}, kept {Kept}, duplicates {Duplicates} (semantic {SemanticDuplicates}), secret-rejected {Rejected}.",
            run.AgentDefinitionId, proposals.Count, created.Count, duplicates, semanticDuplicates, rejected);

        return new MemoryExtractionOutcome(MemoryExcluded: false, ModelConfigured: true, created, proposals.Count, duplicates);
    }

    private static IReadOnlyList<MemoryDedupExisting> BuildSemanticExisting(IReadOnlyList<PlaybookActionRecord> existing)
    {
        // The semantic comparison set mirrors the lexical one: only live (Suggested/Enabled) actions gate re-proposal. A
        // legacy untyped action (null MemoryScope) keys under Procedural so it dedupes a procedural candidate, matching
        // BuildDedupKeys. Id+Version key its RAM-only cached vector; Behavior is the embedded text.
        return
        [
            .. existing
               .Where(static action => action.State is PlaybookActionState.Suggested or PlaybookActionState.Enabled)
               .Where(static action => !string.IsNullOrWhiteSpace(action.Behavior))
               .Select(static action => new MemoryDedupExisting(action.Id,
                   action.Version,
                   action.MemoryScope ?? MemoryScope.Procedural,
                   action.Behavior))
        ];
    }

    // One lexically-surviving candidate carried between PASS 1 (lexical) and PASS 2 (semantic) before persistence.
    private sealed record AcceptedCandidate(string Behavior, string? TriggerCondition, MemoryScope Scope, double? Confidence);

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
