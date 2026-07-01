namespace XE_Local_AI_Engine.Tests.Knowledge;

using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Reciprocal Rank Fusion merges the lexical and semantic arms: a chunk's fused score is the sum over every list it
///     appears in of <c>1 / (k + rank)</c> with <c>k = 60</c> (1-based rank). The result is the union of every chunk id,
///     ordered by descending fused score with a deterministic tiebreak on the chunk id.
/// </summary>
public sealed class ReciprocalRankFusionTests
{
    private readonly ReciprocalRankFusion _fusion = new();

    [Test]
    public void Fuse_WhenChunkIsRankedFirstInOneList_ScoresItByTheRrfFormula()
    {
        var chunk = Guid.NewGuid();

        var result = _fusion.Fuse([[chunk]]);

        // A single rank-one hit contributes one over k plus one, with k of sixty.
        AssertEx.True(Math.Abs(result.Single().Score - (1d / 61d)) < 1e-12,
            "A single rank-1 hit should score 1 / (k + 1) with k = 60.");
    }

    [Test]
    public void Fuse_WhenChunkAppearsInBothLists_SumsTheContributionAcrossLists()
    {
        var shared = Guid.NewGuid();
        var other = Guid.NewGuid();

        // The shared chunk ranks first in list A and second in list B, while the other chunk ranks first in list B.
        var result = _fusion.Fuse([[shared], [other, shared]]);

        var sharedEntry = result.Single(entry => entry.ChunkId == shared);
        var expected = (1d / 61d) + (1d / 62d);
        AssertEx.True(Math.Abs(sharedEntry.Score - expected) < 1e-12,
            "A chunk present in two lists should accumulate 1/(k+rank) from each.");
    }

    [Test]
    public void Fuse_WhenListsOverlap_ReturnsTheUnionOfEveryChunkId()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var result = _fusion.Fuse([[a, b], [b, c]]);

        AssertEx.Equal(expected: 3, result.Count);
    }

    [Test]
    public void Fuse_WhenScoresDiffer_OrdersEntriesByDescendingFusedScore()
    {
        var top = Guid.NewGuid();
        var lower = Guid.NewGuid();

        // The top chunk ranks first in both lists and the lower chunk ranks second in one list.
        var result = _fusion.Fuse([[top, lower], [top]]);

        AssertEx.Equal(top, result[0].ChunkId);
    }

    [Test]
    public void Fuse_WhenTwoChunksTieOnScore_BreaksTheTieByAscendingChunkId()
    {
        // Each chunk ranks first in its own list so their fused scores are identical and the tiebreak orders by chunk id.
        var first = new Guid("00000000-0000-0000-0000-000000000001");
        var second = new Guid("00000000-0000-0000-0000-000000000002");

        var result = _fusion.Fuse([[second], [first]]);

        AssertEx.Equal(first, result[0].ChunkId);
    }
}
