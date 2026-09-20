namespace XE_Local_AI_Engine.Client.Services.Memory.Implementation;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     Default <see cref="IMemoryExtractionService" />: it asks the node-local agent for candidate memories, drops
///     near-duplicates of existing live ones and persists the survivors for human review.
/// </summary>
/// <remarks>
///     It gates temporary conversations BEFORE any model call, the single write-only suppression point, since the
///     retrieval path is never gated, and no-ops when no node-local extraction model is configured. Candidates are
///     inert by construction, because the resolver injects only <c>Enabled</c> actions, so promotion stays an
///     eval-gated human step.
/// </remarks>
internal sealed class MemoryExtractionService : IMemoryExtractionService
{
    private readonly IMemoryExtractionAgent _extractionAgent;
    private readonly ILogger<MemoryExtractionService> _logger;
    private readonly MemoryExtractionOptions _options;
    private readonly IPlaybookActionStore _playbookActionStore;
    private readonly IMemorySemanticDeduplicator _semanticDeduplicator;

    public MemoryExtractionService(
        IMemoryExtractionAgent extractionAgent,
        IPlaybookActionStore playbookActionStore,
        IMemorySemanticDeduplicator semanticDeduplicator,
        IOptions<MemoryExtractionOptions> options,
        ILogger<MemoryExtractionService> logger)
    {
        ArgumentNullException.ThrowIfNull(extractionAgent);
        _extractionAgent = extractionAgent;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(playbookActionStore);
        _playbookActionStore = playbookActionStore;
        ArgumentNullException.ThrowIfNull(semanticDeduplicator);
        _semanticDeduplicator = semanticDeduplicator;
    }

    public async Task<MemoryExtractionOutcome> ExtractAsync(MemoryExtractionRunInput run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Temp-chat gate FIRST: a memory-excluded conversation never extracts. This is the SINGLE write-only
        // enforcement point — retrieval is never gated, so a temp chat still gets existing enabled memory injected.
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

        var proposals = await _extractionAgent.ProposeAsync(run, cancellationToken);

        // Enforce the candidate cap server-side as a hard limit (the prompt only requests it). This bounds review load
        // regardless of what the model returns.
        if (proposals.Count > _options.MaxCandidates)
        {
            proposals = [.. proposals.Take(_options.MaxCandidates)];
        }

        if (proposals.Count == 0)
        {
            return new MemoryExtractionOutcome { MemoryExcluded = false, ModelConfigured = true, CreatedCandidates = [], ProposedCount = 0, DuplicateCount = 0 };
        }

        // Dedup against the agent's existing live memories so repeat runs don't flood the staging list. The lessons text
        // is held only in memory for this compare — never written to the execution log.
        var existing = await _playbookActionStore.ListByAgentAsync(run.AgentDefinitionId, cancellationToken);
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

            // Secret-scan BOTH model-produced free-text fields before persisting them, since the agent reads raw
            // conversation content. The scanner's `content` argument is redactable and the others are reject-only.
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

            if (!dedupKeys.Add(ToDedupKey(behavior, proposal.Scope)))
            {
                // Matches an existing Suggested/Enabled memory (or an earlier candidate in this same run) — skip it.
                duplicates++;
                continue;
            }

            accepted.Add(new AcceptedCandidate { Behavior = behavior, TriggerCondition = triggerCondition, Scope = proposal.Scope, Confidence = proposal.Confidence });
        }

        // PASS 2 — semantic dedup ON TOP OF lexical, dropping a lexically-distinct paraphrase of a live memory. With
        // no confident embedding model, or any failure, it returns NotApplied and every survivor is kept.
        var semantic = await _semanticDeduplicator.FindSemanticDuplicatesAsync(BuildSemanticExisting(existing),
            [.. accepted.Select(static candidate => new MemoryDedupCandidate { Scope = candidate.Scope, Behavior = candidate.Behavior })],
            cancellationToken);

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
            var record = await _playbookActionStore.AddAsync(new PlaybookActionInput
            {
                AgentDefinitionId = run.AgentDefinitionId,
                State = PlaybookActionState.Suggested,
                Source = PlaybookActionSource.Extracted,
                TriggerCondition = candidate.TriggerCondition,
                Behavior = candidate.Behavior,
                Scope = candidate.Scope.ToString(),
                Priority = _options.CandidatePriority,
                SourceFeedbackIds = sourceFeedbackIds,
                Confidence = candidate.Confidence,
                MemoryScope = candidate.Scope
            },
                cancellationToken);

            created.Add(record);
        }

        _logger.LogInformation("Memory extraction for agent {AgentId}: proposed {Proposed}, kept {Kept}, duplicates {Duplicates} (semantic {SemanticDuplicates}), secret-rejected {Rejected}.",
            run.AgentDefinitionId, proposals.Count, created.Count, duplicates, semanticDuplicates, rejected);

        return new MemoryExtractionOutcome { MemoryExcluded = false, ModelConfigured = true, CreatedCandidates = created, ProposedCount = proposals.Count, DuplicateCount = duplicates };
    }

    private static IReadOnlyList<MemoryDedupExisting> BuildSemanticExisting(IReadOnlyList<PlaybookActionRecord> existing)
    {
        // The semantic comparison set mirrors the lexical one: only live actions gate re-proposal, and a legacy
        // untyped action keys under Procedural. Id and version key its cached vector; Behavior is the embedded text.
        return
        [
            .. existing
               .Where(static action => action.State is PlaybookActionState.Suggested or PlaybookActionState.Enabled)
               .Where(static action => !string.IsNullOrWhiteSpace(action.Behavior))
               .Select(static action => new MemoryDedupExisting
               {
                   Id = action.Id,
                   Version = action.Version,
                   Scope = action.MemoryScope ?? MemoryScope.Procedural,
                   Behavior = action.Behavior
               })
        ];
    }

    // One lexically-surviving candidate carried between PASS 1 (lexical) and PASS 2 (semantic) before persistence.
    private sealed record AcceptedCandidate
    {
        public required string Behavior { get; init; }

        public required string? TriggerCondition { get; init; }

        public required MemoryScope Scope { get; init; }

        public required double? Confidence { get; init; }
    }

    private static HashSet<DedupKey> BuildDedupKeys(IReadOnlyList<PlaybookActionRecord> existing)
    {
        // Only live actions matter for dedup, so an archived or disabled one never blocks re-proposing. A legacy
        // untyped action keys under Procedural, so a manually-authored equivalent still dedupes a candidate.
        return existing
               .Where(static action => action.State is PlaybookActionState.Suggested or PlaybookActionState.Enabled)
               .Select(static action => ToDedupKey(action.Behavior, action.MemoryScope ?? MemoryScope.Procedural))
               .ToHashSet();
    }

    private static DedupKey ToDedupKey(string behavior, MemoryScope scope)
    {
        // A (scope, behavior) pair keys the dedup set, so there is no separator char to collide with content.
        return new DedupKey(scope, NormalizeText(behavior));
    }

    // The normalized identity a candidate dedupes on. Deliberately not shared with PlaybookAnalysisService's
    // same-named key, which scopes by an untyped string rather than the MemoryScope enum.
    private readonly record struct DedupKey(MemoryScope Scope, string Behavior);

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
