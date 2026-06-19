namespace XE_Local_AI_Engine.Tests.Agents;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class PlaybookRetrievalSelectorTests
{
    [Test]
    public async Task SelectAsync_WhenAtOrBelowThreshold_ReturnsEnabledAsIs_WithoutCallingRanker()
    {
        var enabled = Candidates(3);
        var ranker = new RecordingRanker();

        var result = await PlaybookRetrievalSelector.SelectAsync(ranker, "deploy", enabled, 3, 2, CancellationToken.None);

        AssertEx.Equal(0, ranker.CallCount, "At or below the threshold the ranker must not be consulted (no embedding client built).");
        AssertEx.True(ReferenceEquals(enabled, result), "The full Enabled set is returned unchanged so the prompt stays byte-identical.");
    }

    [Test]
    public async Task SelectAsync_WhenQueryBlank_ReturnsEnabledAsIs_WithoutCallingRanker()
    {
        var enabled = Candidates(5);
        var ranker = new RecordingRanker();

        var result = await PlaybookRetrievalSelector.SelectAsync(ranker, "   ", enabled, 2, 2, CancellationToken.None);

        AssertEx.Equal(0, ranker.CallCount, "A blank query short-circuits before the ranker is consulted.");
        AssertEx.True(ReferenceEquals(enabled, result), "A blank query returns the full Enabled set unchanged.");
    }

    [Test]
    public async Task SelectAsync_WhenQueryNull_ReturnsEnabledAsIs_WithoutCallingRanker()
    {
        var enabled = Candidates(5);
        var ranker = new RecordingRanker();

        var result = await PlaybookRetrievalSelector.SelectAsync(ranker, null, enabled, 2, 2, CancellationToken.None);

        AssertEx.Equal(0, ranker.CallCount, "A null query short-circuits before the ranker is consulted.");
        AssertEx.True(ReferenceEquals(enabled, result), "A null query returns the full Enabled set unchanged.");
    }

    [Test]
    public async Task SelectAsync_WhenAboveThresholdWithQuery_InvokesRanker_AndReOrdersByPriorityThenCreatedAtUtc()
    {
        // Three Enabled actions, threshold 2 => retrieval engages. The ranker returns a deliberately out-of-order
        // subset; the selector must re-impose Priority-then-CreatedAtUtc so the composer's store-order contract holds.
        var high = Candidate(10, 5);
        var mid = Candidate(20, 3);
        var low = Candidate(20, 9);
        var ranker = new RecordingRanker([low, high, mid]);

        var result = await PlaybookRetrievalSelector.SelectAsync(ranker, "deploy", [high, mid, low], 2, 3, CancellationToken.None);

        AssertEx.Equal(1, ranker.CallCount, "Above the threshold with a non-blank query the ranker is consulted exactly once.");
        AssertEx.Equal(high.Id, result[0].Id, "Lowest Priority first after re-order.");
        AssertEx.Equal(mid.Id, result[1].Id, "Equal Priority breaks by older CreatedAtUtc.");
        AssertEx.Equal(low.Id, result[2].Id, "Newer CreatedAtUtc sorts last on a Priority tie.");
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
        return new PlaybookActionRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            "deploy",
            "behaviour",
            null,
            priority,
            1,
            createdAtUtc,
            createdAtUtc);
    }

    // Records how many times it is consulted and returns a fixed (out-of-order) subset so the gate and re-order can
    // both be asserted; mirrors the resolver-test fakes but exercises the selector directly.
    private sealed class RecordingRanker(IReadOnlyList<PlaybookActionRecord>? selection = null) : IPlaybookRetrievalRanker
    {
        private readonly IReadOnlyList<PlaybookActionRecord>? _selection = selection;

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
