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

    private static MemoryExtractionService CreateService(out IPlaybookActionStore store,
        IMemoryExtractionAgent agent,
        MemoryExtractionOptions? options = null)
    {
        store = Substitute.For<IPlaybookActionStore>();
        return new MemoryExtractionService(agent,
            store,
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

    private static ProposedMemory Candidate(string behavior, MemoryScope scope)
    {
        return new ProposedMemory(behavior, scope, TriggerCondition: null, Confidence: 0.8d);
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
