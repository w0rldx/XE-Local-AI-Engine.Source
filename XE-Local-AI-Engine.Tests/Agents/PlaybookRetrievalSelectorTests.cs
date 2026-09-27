namespace XE_Local_AI_Engine.Tests.Agents;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class PlaybookRetrievalSelectorTests
{
    [Test]
    public async Task SelectAsync_WhenAtOrBelowThreshold_WithinBudget_ReturnsEnabledUnchanged_WithoutCallingRanker()
    {
        // The fast path is budget-capped, but a set within the default budgets must come back unchanged in store order,
        // so the composed prompt (and the config hash) stay byte-identical to the uncapped static prepend.
        var enabled = Candidates(3);
        var ranker = new RecordingRanker();
        var defaults = new PlaybookRetrievalOptions();

        var result = await PlaybookRetrievalSelector.SelectAsync(ranker,
            "deploy",
            enabled,
            retrievalThreshold: 3,
            topK: 2,
            CancellationToken.None,
            defaults.MaxInjectedMemoryTokens,
            defaults.MaxInjectedFailureMemoryTokens);

        AssertEx.Equal(expected: 0, ranker.CallCount, "At or below the threshold the ranker must not be consulted (no embedding client built).");
        AssertEx.True(enabled.Select(static action => action.Id).SequenceEqual(result.Select(static action => action.Id)),
            "A set within budget is returned whole and in store order.");
        AssertEx.Equal(PlaybookPromptComposer.Compose("Base.", enabled), PlaybookPromptComposer.Compose("Base.", result));
    }

    [Test]
    public async Task SelectAsync_WhenAtOrBelowThreshold_OverBudget_TrimsFromStoreOrderTail_WithoutCallingRanker()
    {
        // Below the retrieval threshold the set is not ranked, so the budget trim drops the LAST items in store order and
        // keeps the survivors in that order. Each behaviour is 8 chars => 2 tokens; a 4-token budget keeps two of three.
        var first = Scoped("aaaaaaaa", priority: 1, createdAtUtc: 1, scope: null);
        var second = Scoped("bbbbbbbb", priority: 2, createdAtUtc: 2, scope: null);
        var third = Scoped("cccccccc", priority: 3, createdAtUtc: 3, scope: null);
        var ranker = new RecordingRanker();

        var result = await PlaybookRetrievalSelector.SelectAsync(ranker,
            "deploy",
            [first, second, third],
            retrievalThreshold: 8,
            topK: 8,
            CancellationToken.None,
            maxInjectedMemoryTokens: 4);

        AssertEx.Equal(expected: 0, ranker.CallCount, "The fast path never consults the ranker, even when it trims.");
        AssertEx.Equal(expected: 2, result.Count);
        AssertEx.Equal(first.Id, result[0].Id);
        AssertEx.Equal(second.Id, result[1].Id);
    }

    [Test]
    public async Task SelectAsync_WhenQueryBlank_OverFailureSubBudget_TrimsFailureOnly()
    {
        // A blank query is the other fast-path trigger; the Failure sub-budget applies there too, leaving positive items.
        var failureFirst = Scoped("ffff", priority: 1, createdAtUtc: 1, MemoryScope.Failure);
        var failureSecond = Scoped("gggg", priority: 2, createdAtUtc: 2, MemoryScope.Failure);
        var procedural = Scoped("pppp", priority: 3, createdAtUtc: 3, MemoryScope.Procedural);

        var result = await PlaybookRetrievalSelector.SelectAsync(new RecordingRanker(),
            retrievalQuery: null,
            [failureFirst, failureSecond, procedural],
            retrievalThreshold: 0,
            topK: 8,
            CancellationToken.None,
            maxInjectedMemoryTokens: 100,
            maxInjectedFailureMemoryTokens: 1);

        AssertEx.True(result.Select(static action => action.Id).SequenceEqual([failureFirst.Id, procedural.Id]),
            "The later Failure item is dropped; the first Failure item and the procedural one survive in store order.");
    }

    [Test]
    public async Task SelectAsync_WhenFastPathFirstItemExceedsBudget_StillKeepsIt()
    {
        // The first counted item is always kept, so a single oversized memory still injects; the per-action length
        // limit is what bounds it.
        var oversized = Scoped(new string('a', 40), priority: 1, createdAtUtc: 1, scope: null);
        var next = Scoped("bbbb", priority: 2, createdAtUtc: 2, scope: null);

        var result = await PlaybookRetrievalSelector.SelectAsync(new RecordingRanker(), "deploy", [oversized, next], retrievalThreshold: 8, topK: 8, CancellationToken.None,
            maxInjectedMemoryTokens: 2);

        AssertEx.Equal(expected: 1, result.Count);
        AssertEx.Equal(oversized.Id, result[0].Id);
    }

    [Test]
    public async Task SelectAsync_WhenQueryBlank_AndBudgetUnbounded_ReturnsEnabledAsIs_WithoutCallingRanker()
    {
        var enabled = Candidates(5);
        var ranker = new RecordingRanker();

        var result = await PlaybookRetrievalSelector.SelectAsync(ranker, "   ", enabled, retrievalThreshold: 2, topK: 2, CancellationToken.None);

        AssertEx.Equal(expected: 0, ranker.CallCount, "A blank query short-circuits before the ranker is consulted.");
        AssertEx.True(ReferenceEquals(enabled, result), "A blank query returns the full Enabled set unchanged.");
    }

    [Test]
    public async Task SelectAsync_WhenQueryNull_AndBudgetUnbounded_ReturnsEnabledAsIs_WithoutCallingRanker()
    {
        var enabled = Candidates(5);
        var ranker = new RecordingRanker();

        var result = await PlaybookRetrievalSelector.SelectAsync(ranker, retrievalQuery: null, enabled, retrievalThreshold: 2, topK: 2, CancellationToken.None);

        AssertEx.Equal(expected: 0, ranker.CallCount, "A null query short-circuits before the ranker is consulted.");
        AssertEx.True(ReferenceEquals(enabled, result), "A null query returns the full Enabled set unchanged.");
    }

    [Test]
    public async Task SelectAsync_WhenAboveThresholdWithQuery_InvokesRanker_AndReOrdersByPriorityThenCreatedAtUtc()
    {
        // Three Enabled actions, threshold 2 => retrieval engages. The ranker returns a deliberately out-of-order
        // subset; the selector must re-impose Priority-then-CreatedAtUtc so the composer's store-order contract holds.
        var high = Candidate(priority: 10, createdAtUtc: 5);
        var mid = Candidate(priority: 20, createdAtUtc: 3);
        var low = Candidate(priority: 20, createdAtUtc: 9);
        var ranker = new RecordingRanker([low, high, mid]);

        var result = await PlaybookRetrievalSelector.SelectAsync(ranker, "deploy", [high, mid, low], retrievalThreshold: 2, topK: 3, CancellationToken.None);

        AssertEx.Equal(expected: 1, ranker.CallCount, "Above the threshold with a non-blank query the ranker is consulted exactly once.");
        AssertEx.Equal(high.Id, result[0].Id, "Lowest Priority first after re-order.");
        AssertEx.Equal(mid.Id, result[1].Id, "Equal Priority breaks by older CreatedAtUtc.");
        AssertEx.Equal(low.Id, result[2].Id, "Newer CreatedAtUtc sorts last on a Priority tie.");
    }

    [Test]
    public async Task SelectAsync_WhenOverTokenBudget_TrimsLowestRanked()
    {
        // Three enabled actions above the threshold with a query => retrieval engages. The ranker returns them in
        // relevance order (most relevant first). With a 2-token budget and each behaviour estimating 1 token (4 chars),
        // only the top-two-ranked survive; the lowest-ranked is dropped. The selector then re-imposes Priority order on the
        // survivors, so the assertion checks BOTH that the lowest-ranked was dropped and the survivors are store-ordered.
        var mostRelevant = Scoped("aaaa", priority: 30, createdAtUtc: 1, scope: null);
        var secondRelevant = Scoped("bbbb", priority: 10, createdAtUtc: 2, scope: null);
        var leastRelevant = Scoped("cccc", priority: 20, createdAtUtc: 3, scope: null);

        var ranker = new RecordingRanker([mostRelevant, secondRelevant, leastRelevant]);

        var result = await PlaybookRetrievalSelector.SelectAsync(ranker,
            "deploy",
            [mostRelevant, secondRelevant, leastRelevant],
            retrievalThreshold: 2,
            topK: 3,
            CancellationToken.None,
            maxInjectedMemoryTokens: 2);

        AssertEx.Equal(expected: 2, result.Count, "The 2-token budget keeps only the two highest-ranked memories.");
        AssertEx.False(result.Any(action => action.Id == leastRelevant.Id), "The lowest-ranked memory is dropped first.");
        AssertEx.Equal(secondRelevant.Id, result[0].Id, "Survivors are re-ordered by Priority ascending (10 before 30).");
        AssertEx.Equal(mostRelevant.Id, result[1].Id);
    }

    [Test]
    public async Task SelectAsync_TokenEstimateIsDeterministic_SameBudgetTrimsSameSet()
    {
        // The trim is a pure function of the stored behaviour text and the budget, so two identical inputs must trim to
        // the identical surviving set every time (resume-safety: a fixed memory set always composes the same prompt).
        var first = Scoped("aaaaaaaa", priority: 1, createdAtUtc: 1, scope: null); // 8 chars => 2 tokens
        var second = Scoped("bbbbbbbb", priority: 2, createdAtUtc: 2, scope: null); // 8 chars => 2 tokens
        var third = Scoped("cccccccc", priority: 3, createdAtUtc: 3, scope: null); // 8 chars => 2 tokens
        IReadOnlyList<PlaybookActionRecord> ordered = [first, second, third];

        var runA = await PlaybookRetrievalSelector.SelectAsync(new RecordingRanker(ordered), "deploy", ordered, retrievalThreshold: 0, topK: 3, CancellationToken.None, maxInjectedMemoryTokens: 4);
        var runB = await PlaybookRetrievalSelector.SelectAsync(new RecordingRanker(ordered), "deploy", ordered, retrievalThreshold: 0, topK: 3, CancellationToken.None, maxInjectedMemoryTokens: 4);

        // 4-token budget over 2-token items => exactly the first two ranked survive, both runs.
        AssertEx.Equal(expected: 2, runA.Count);
        AssertEx.True(runA.Select(action => action.Id).SequenceEqual(runB.Select(action => action.Id)),
            "The deterministic trim must produce the identical surviving set across runs.");
        AssertEx.False(runA.Any(action => action.Id == third.Id), "The third (lowest-ranked) item exceeds the 4-token budget and is dropped.");
    }

    [Test]
    public async Task SelectAsync_FailureSubBudget_CapsFailureWithoutDroppingPositive()
    {
        // A Failure sub-budget of 1 token keeps only the single highest-ranked Failure item; positive items always pass
        // through (they do not count against the Failure sub-budget) so negative guidance cannot crowd them out.
        var failureHigh = Scoped("ffff", priority: 5, createdAtUtc: 1, MemoryScope.Failure);
        var failureLow = Scoped("gggg", priority: 6, createdAtUtc: 2, MemoryScope.Failure);
        var procedural = Scoped("pppp", priority: 1, createdAtUtc: 3, MemoryScope.Procedural);

        var ranker = new RecordingRanker([failureHigh, failureLow, procedural]);

        var result = await PlaybookRetrievalSelector.SelectAsync(ranker,
            "deploy",
            [failureHigh, failureLow, procedural],
            retrievalThreshold: 2,
            topK: 3,
            CancellationToken.None,
            maxInjectedMemoryTokens: 100,
            maxInjectedFailureMemoryTokens: 1);

        AssertEx.Equal(expected: 2, result.Count, "One Failure item dropped by the sub-budget; the second Failure and the procedural survive.");
        AssertEx.True(result.Any(action => action.Id == procedural.Id), "Positive guidance is never dropped by the Failure sub-budget.");
        AssertEx.True(result.Any(action => action.Id == failureHigh.Id), "The highest-ranked Failure item survives.");
        AssertEx.False(result.Any(action => action.Id == failureLow.Id), "The lower-ranked Failure item is dropped by the sub-budget.");
    }

    [Test]
    public async Task SelectAsync_WhenBudgetUnbounded_KeepsAllRankedTopK()
    {
        // A zero (unbounded) budget is the legacy behaviour: no trimming, every top-k item survives.
        var first = Scoped("aaaa", priority: 1, createdAtUtc: 1, scope: null);
        var second = Scoped("bbbb", priority: 2, createdAtUtc: 2, scope: null);
        var third = Scoped("cccc", priority: 3, createdAtUtc: 3, scope: null);
        IReadOnlyList<PlaybookActionRecord> ordered = [first, second, third];

        var result = await PlaybookRetrievalSelector.SelectAsync(new RecordingRanker(ordered), "deploy", ordered, retrievalThreshold: 2, topK: 3, CancellationToken.None);

        AssertEx.Equal(expected: 3, result.Count, "An unbounded (0) budget trims nothing.");
    }

    private static PlaybookActionRecord Scoped(string behavior, int priority, long createdAtUtc, MemoryScope? scope)
    {
        return new PlaybookActionRecord
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            State = PlaybookActionState.Enabled,
            Source = PlaybookActionSource.Manual,
            TriggerCondition = null,
            Behavior = behavior,
            Scope = null,
            Priority = priority,
            Version = 1,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc,
            MemoryScope = scope
        };
    }

    private static IReadOnlyList<PlaybookActionRecord> Candidates(int count)
    {
        var actions = new List<PlaybookActionRecord>(count);
        for (var index = 0; index < count; index++)
        {
            actions.Add(Candidate(index, index));
        }

        return actions;
    }

    private static PlaybookActionRecord Candidate(int priority, long createdAtUtc)
    {
        return new PlaybookActionRecord
        {
            Id = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            State = PlaybookActionState.Enabled,
            Source = PlaybookActionSource.Manual,
            TriggerCondition = "deploy",
            Behavior = "behaviour",
            Scope = null,
            Priority = priority,
            Version = 1,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
    }

    // Records how many times it is consulted and returns a fixed (out-of-order) subset so the gate and re-order can
    // both be asserted; mirrors the resolver-test fakes but exercises the selector directly.
    private sealed class RecordingRanker : IPlaybookRetrievalRanker
    {
        private readonly IReadOnlyList<PlaybookActionRecord>? _selection;

        public RecordingRanker(IReadOnlyList<PlaybookActionRecord>? selection = null)
        {
            _selection = selection;
        }

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<PlaybookActionRecord>> SelectTopKAsync(string query,
            IReadOnlyList<PlaybookActionRecord> candidates,
            int k,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_selection ?? candidates);
        }
    }
}
