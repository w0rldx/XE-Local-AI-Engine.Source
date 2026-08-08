namespace XE_Local_AI_Engine.Tests.Memory;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Memory;
using XE_Local_AI_Engine.Client.Services.Memory.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Behavioral tests for <see cref="MemoryExtractionService" />: the temp-chat write-only gate, the no-model disabled
///     gate, lesson/no-lesson candidate creation, Failure-scope eligibility, and dedup against existing live memories.
///     The model-touching agent is faked (mirrors the analysis-service test seam, so no Ollama in CI); the privacy
///     invariant (node-local resolution, never a cloud client) is covered by
///     <see cref="DefaultMemoryExtractionAgentTests" />.
/// </summary>
public sealed class MemoryExtractionServiceTests
{
    [Test]
    public async Task MemoryExtraction_WhenRunHasLesson_CreatesSuggestedExtractedCandidate()
    {
        var agentId = Guid.NewGuid();
        var agent = new FakeExtractionAgent(_ => [Candidate("Prefer the existing shared helper.", MemoryScope.Procedural)]);
        var service = CreateService(out var store, agent);
        store.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        EchoAddedAction(store);

        var outcome = await service.ExtractAsync(SuccessfulRun(agentId)).ConfigureAwait(false);

        AssertEx.False(outcome.MemoryExcluded);
        AssertEx.True(outcome.ModelConfigured);
        AssertEx.Equal(expected: 1, outcome.ProposedCount);
        AssertEx.Equal(expected: 1, outcome.CreatedCandidates.Count);
        var created = outcome.CreatedCandidates[0];
        AssertEx.Equal(PlaybookActionState.Suggested, created.State);
        AssertEx.Equal(PlaybookActionSource.Extracted, created.Source);
        AssertEx.Equal(MemoryScope.Procedural, created.MemoryScope);
    }

    [Test]
    public async Task MemoryExtraction_WhenRunHasNoLesson_CreatesNoCandidate()
    {
        var agentId = Guid.NewGuid();
        var agent = new FakeExtractionAgent(_ => []);
        var service = CreateService(out var store, agent);
        store.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));

        var outcome = await service.ExtractAsync(SuccessfulRun(agentId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, outcome.ProposedCount);
        AssertEx.Equal(expected: 0, outcome.CreatedCandidates.Count);
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task MemoryExtraction_WhenInvokeExceptionPresent_CanProduceFailureScope()
    {
        var agentId = Guid.NewGuid();
        var agent = new FakeExtractionAgent(run =>
            run.Failed ? [Candidate("Avoid calling the tool without an argument.", MemoryScope.Failure)] : []);
        var service = CreateService(out var store, agent);
        store.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        EchoAddedAction(store);

        var outcome = await service.ExtractAsync(FailedRun(agentId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcome.CreatedCandidates.Count);
        AssertEx.Equal(MemoryScope.Failure, outcome.CreatedCandidates[0].MemoryScope);
    }

    [Test]
    public async Task MemoryExtraction_WhenDuplicateOfExisting_Deduplicates()
    {
        var agentId = Guid.NewGuid();
        // The candidate matches an existing live action after whitespace/case normalization → it must be skipped.
        var existing = EnabledAction(agentId, "Prefer the existing shared helper.", MemoryScope.Procedural);
        var agent = new FakeExtractionAgent(_ => [Candidate("  prefer   THE existing shared HELPER.  ", MemoryScope.Procedural)]);
        var service = CreateService(out var store, agent);
        store.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([existing]));
        EchoAddedAction(store);

        var outcome = await service.ExtractAsync(SuccessfulRun(agentId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcome.ProposedCount);
        AssertEx.Equal(expected: 0, outcome.CreatedCandidates.Count);
        AssertEx.Equal(expected: 1, outcome.DuplicateCount, "A near-duplicate of an existing live action must be skipped.");
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task MemoryExtraction_WhenSemanticDedupFlagsCandidate_DropsOnlyThatCandidate()
    {
        var agentId = Guid.NewGuid();
        // Two lexically-distinct candidates survive the lexical pass; the semantic layer flags the SECOND (index 1) as a
        // paraphrase of an existing memory. Only the first must be persisted, and the flagged one counts as a duplicate.
        var agent = new FakeExtractionAgent(_ =>
        [
            Candidate("Prefer the shared HTTP helper for outbound calls.", MemoryScope.Procedural),
            Candidate("Reach for the common HTTP utility when making requests.", MemoryScope.Procedural)
        ]);
        var deduplicator = StubSemanticDeduplicator.Flagging(1);
        var service = CreateService(out var store, agent, semanticDeduplicator: deduplicator);
        store.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        EchoAddedAction(store);

        var outcome = await service.ExtractAsync(SuccessfulRun(agentId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, outcome.ProposedCount);
        AssertEx.Equal(expected: 1, outcome.CreatedCandidates.Count, "The semantic paraphrase must be dropped, leaving one candidate.");
        AssertEx.Equal(expected: 1, outcome.DuplicateCount, "A semantic near-duplicate counts toward the duplicate total.");
        AssertEx.Equal("Prefer the shared HTTP helper for outbound calls.", outcome.CreatedCandidates[0].Behavior);
    }

    [Test]
    public async Task MemoryExtraction_WhenSemanticDedupNotApplied_KeepsEveryLexicalSurvivor()
    {
        var agentId = Guid.NewGuid();
        // Embedder unavailable / not confident => the deduplicator returns NotApplied. NO candidate may be dropped by the
        // semantic layer — this is the proof that a provider outage never mass-dedups legitimate new memories.
        var agent = new FakeExtractionAgent(_ =>
        [
            Candidate("Prefer the shared HTTP helper for outbound calls.", MemoryScope.Procedural),
            Candidate("Reach for the common HTTP utility when making requests.", MemoryScope.Procedural)
        ]);
        var service = CreateService(out var store, agent, semanticDeduplicator: StubSemanticDeduplicator.NotApplied());
        store.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        EchoAddedAction(store);

        var outcome = await service.ExtractAsync(SuccessfulRun(agentId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, outcome.CreatedCandidates.Count, "With semantic dedup not applied, both lexical survivors persist.");
        AssertEx.Equal(expected: 0, outcome.DuplicateCount, "No candidate is dropped when semantic dedup does not run.");
    }

    [Test]
    public async Task Extraction_WhenConversationMemoryExcluded_DoesNothing()
    {
        var agentId = Guid.NewGuid();
        var agent = new FakeExtractionAgent(_ => [Candidate("This should never be proposed.", MemoryScope.Procedural)]);
        var service = CreateService(out var store, agent);

        var outcome = await service.ExtractAsync(SuccessfulRun(agentId) with
        {
            MemoryExcluded = true
        }).ConfigureAwait(false);

        AssertEx.True(outcome.MemoryExcluded, "A temp conversation must short-circuit as suppressed.");
        AssertEx.Equal(expected: 0, outcome.ProposedCount);
        AssertEx.Equal(expected: 0, outcome.CreatedCandidates.Count);
        AssertEx.Equal(expected: 0, agent.InvocationCount, "The temp gate must run BEFORE any model call.");
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await store.DidNotReceive().ListByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task Extraction_WhenNoModelConfigured_DoesNothing()
    {
        var agentId = Guid.NewGuid();
        var agent = new FakeExtractionAgent(_ => [Candidate("Never reached.", MemoryScope.Procedural)]);
        // Empty ExtractionModelName = the CI-safe disabled gate (mirrors the embedding ranker).
        var service = CreateService(out var store, agent, new MemoryExtractionOptions
        {
            ExtractionModelName = string.Empty
        });

        var outcome = await service.ExtractAsync(SuccessfulRun(agentId)).ConfigureAwait(false);

        AssertEx.False(outcome.ModelConfigured, "No model configured must report ModelConfigured == false.");
        AssertEx.Equal(expected: 0, agent.InvocationCount, "No model => no model call.");
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task MemoryExtraction_WhenBehaviorCarriesPemPrivateKey_RejectsCandidate()
    {
        var agentId = Guid.NewGuid();
        const string pem = "Remember this: -----BEGIN RSA PRIVATE KEY-----\nMIIEvwIBADANBg\n-----END RSA PRIVATE KEY-----";
        var agent = new FakeExtractionAgent(_ => [Candidate(pem, MemoryScope.Procedural)]);
        var service = CreateService(out var store, agent);
        store.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        EchoAddedAction(store);

        var outcome = await service.ExtractAsync(SuccessfulRun(agentId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, outcome.CreatedCandidates.Count, "A candidate carrying a PEM private key must be rejected outright.");
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task MemoryExtraction_WhenTriggerConditionCarriesPemPrivateKey_RejectsCandidate()
    {
        var agentId = Guid.NewGuid();
        const string pem = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1r\n-----END OPENSSH PRIVATE KEY-----";
        var agent = new FakeExtractionAgent(_ => [Candidate("Prefer the shared helper.", MemoryScope.Procedural, trigger: pem)]);
        var service = CreateService(out var store, agent);
        store.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        EchoAddedAction(store);

        var outcome = await service.ExtractAsync(SuccessfulRun(agentId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, outcome.CreatedCandidates.Count, "A candidate whose trigger condition carries a PEM private key must be rejected outright.");
        await store.DidNotReceive().AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task MemoryExtraction_WhenBehaviorCarriesJwt_RedactsInBothFields()
    {
        var agentId = Guid.NewGuid();
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N";
        var agent = new FakeExtractionAgent(_ =>
            [Candidate($"Use the token {jwt} for auth.", MemoryScope.Procedural, trigger: $"When calling with {jwt}.")]);
        var service = CreateService(out var store, agent);
        store.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        EchoAddedAction(store);

        var outcome = await service.ExtractAsync(SuccessfulRun(agentId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcome.CreatedCandidates.Count, "A JWT is redactable, not reject-worthy — the candidate persists.");
        var created = outcome.CreatedCandidates[0];
        AssertEx.False(created.Behavior.Contains(jwt, StringComparison.Ordinal), "The JWT must be redacted out of the persisted behavior.");
        AssertEx.True(created.Behavior.Contains("[REDACTED", StringComparison.Ordinal), "The behavior must carry a redaction marker.");
        AssertEx.False((created.TriggerCondition ?? string.Empty).Contains(jwt, StringComparison.Ordinal), "The JWT must be redacted out of the persisted trigger condition.");
    }

    [Test]
    public async Task MemoryExtraction_WhenCleanProposal_PersistsUnmodified()
    {
        var agentId = Guid.NewGuid();
        var agent = new FakeExtractionAgent(_ => [Candidate("Prefer the existing shared helper.", MemoryScope.Procedural, trigger: "When adding a feature.")]);
        var service = CreateService(out var store, agent);
        store.ListByAgentAsync(agentId, Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        EchoAddedAction(store);

        var outcome = await service.ExtractAsync(SuccessfulRun(agentId)).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, outcome.CreatedCandidates.Count);
        var created = outcome.CreatedCandidates[0];
        AssertEx.Equal("Prefer the existing shared helper.", created.Behavior);
        AssertEx.Equal("When adding a feature.", created.TriggerCondition);
    }

    private static MemoryExtractionService CreateService(out IPlaybookActionStore store,
        IMemoryExtractionAgent agent,
        MemoryExtractionOptions? options = null,
        IMemorySemanticDeduplicator? semanticDeduplicator = null)
    {
        store = Substitute.For<IPlaybookActionStore>();
        return new MemoryExtractionService(agent,
            store,
            // Default: a NOT-applied deduplicator so these baseline tests exercise the lexical-only path exactly as
            // before (the no-confident-embedding-model / outage fallback). Semantic behaviour is covered by the
            // semantic-specific tests below and by MemorySemanticDeduplicatorTests.
            semanticDeduplicator ?? StubSemanticDeduplicator.NotApplied(),
            Options.Create(options ?? new MemoryExtractionOptions
            {
                ExtractionModelName = "qwen3:8b"
            }),
            NullLogger<MemoryExtractionService>.Instance);
    }

    private static void EchoAddedAction(IPlaybookActionStore store)
    {
        // Echo each persisted input back as a stored record so the service can accumulate CreatedCandidates.
        store.AddAsync(Arg.Any<PlaybookActionInput>(), Arg.Any<CancellationToken>())
             .Returns(callInfo =>
             {
                 var input = callInfo.Arg<PlaybookActionInput>();
                 return Task.FromResult(new PlaybookActionRecord(Guid.NewGuid(),
                     input.AgentDefinitionId,
                     input.State,
                     input.Source,
                     input.TriggerCondition,
                     input.Behavior,
                     input.Scope,
                     input.Priority,
                     Version: 1,
                     CreatedAtUtc: 10,
                     UpdatedAtUtc: 10,
                     input.SourceFeedbackIds,
                     input.Confidence,
                     input.EvalResult,
                     input.EnabledAtUtc,
                     input.MemoryScope));
             });
    }

    private static MemoryExtractionRunInput SuccessfulRun(Guid agentId)
    {
        return new MemoryExtractionRunInput(agentId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            [new MemoryExtractionTurn("How do I add a feature?")],
            "Use the shared helper.",
            Failed: false,
            Error: null,
            MemoryExcluded: false);
    }

    private static MemoryExtractionRunInput FailedRun(Guid agentId)
    {
        return SuccessfulRun(agentId) with
        {
            Failed = true,
            Error = "tool-failed"
        };
    }

    private static ProposedMemory Candidate(string behavior, MemoryScope scope, string? trigger = null)
    {
        return new ProposedMemory(behavior, scope, trigger, Confidence: 0.8d);
    }

    private static PlaybookActionRecord EnabledAction(Guid agentId, string behavior, MemoryScope scope)
    {
        return new PlaybookActionRecord(Guid.NewGuid(),
            agentId,
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            TriggerCondition: null,
            behavior,
            scope.ToString(),
            Priority: 10,
            Version: 1,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10,
            SourceFeedbackIds: null,
            Confidence: null,
            EvalResult: null,
            EnabledAtUtc: 10,
            scope);
    }

    // A controllable semantic-dedup stub: either NOT-applied (the lexical-only fallback the service must honour) or
    // applied while flagging a fixed set of candidate indexes as semantic duplicates. Lets the service integration be
    // verified without an embedder; the real cosine/IsConfident behaviour is covered by MemorySemanticDeduplicatorTests.
    private sealed class StubSemanticDeduplicator : IMemorySemanticDeduplicator
    {
        private readonly bool _applied;
        private readonly IReadOnlySet<int> _duplicateIndexes;

        private StubSemanticDeduplicator(bool applied, IReadOnlySet<int> duplicateIndexes)
        {
            _applied = applied;
            _duplicateIndexes = duplicateIndexes;
        }

        public static StubSemanticDeduplicator NotApplied()
        {
            return new StubSemanticDeduplicator(applied: false, new HashSet<int>());
        }

        public static StubSemanticDeduplicator Flagging(params int[] duplicateIndexes)
        {
            return new StubSemanticDeduplicator(applied: true, new HashSet<int>(duplicateIndexes));
        }

        public Task<MemorySemanticDedupResult> FindSemanticDuplicatesAsync(IReadOnlyList<MemoryDedupExisting> existing,
            IReadOnlyList<MemoryDedupCandidate> candidates,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new MemorySemanticDedupResult(_applied, _duplicateIndexes));
        }
    }

    private sealed class FakeExtractionAgent(Func<MemoryExtractionRunInput, IReadOnlyList<ProposedMemory>> propose) : IMemoryExtractionAgent
    {
        public int InvocationCount { get; private set; }

        public Task<IReadOnlyList<ProposedMemory>> ProposeAsync(MemoryExtractionRunInput run, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult(propose(run));
        }
    }
}
