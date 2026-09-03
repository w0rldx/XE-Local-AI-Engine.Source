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

    [Test]
    public void FuseScored_WithRrfStrategy_IgnoresScores_AndMatchesClassicFuse()
    {
        var a = new Guid("00000000-0000-0000-0000-0000000000a1");
        var b = new Guid("00000000-0000-0000-0000-0000000000b2");
        var c = new Guid("00000000-0000-0000-0000-0000000000c3");

        // Wildly different score magnitudes must not matter under Rrf: only the id order counts.
        var scored = _fusion.FuseScored([
            [new RankFusionInput(a, 1000d), new RankFusionInput(b, -5d)],
            [new RankFusionInput(b, 0.001d), new RankFusionInput(c, 999d)]
        ], RankFusionStrategy.Rrf, scoreWeight: 5d);

        var classic = _fusion.Fuse([[a, b], [b, c]]);

        AssertEx.Equal(classic.Count, scored.Count);
        for (var index = 0; index < classic.Count; index++)
        {
            AssertEx.Equal(classic[index].ChunkId, scored[index].ChunkId);
            AssertEx.True(Math.Abs(classic[index].Score - scored[index].Score) < 1e-12,
                "Rrf-strategy FuseScored must produce the identical fused score as the classic Fuse.");
        }
    }

    [Test]
    public void FuseScored_WithZeroWeight_ReducesToPureRrf()
    {
        var a = new Guid("00000000-0000-0000-0000-0000000000a1");
        var b = new Guid("00000000-0000-0000-0000-0000000000b2");

        var scored = _fusion.FuseScored([
            [new RankFusionInput(a, 100d), new RankFusionInput(b, 1d)]
        ], RankFusionStrategy.ScoreAware, scoreWeight: 0d);

        // a rank-1 → 1/61, b rank-2 → 1/62, regardless of the score spread, because the weight is zero.
        AssertEx.True(Math.Abs(scored[0].Score - (1d / 61d)) < 1e-12, "Zero-weight score-aware must equal pure RRF (rank-1).");
        AssertEx.True(Math.Abs(scored[1].Score - (1d / 62d)) < 1e-12, "Zero-weight score-aware must equal pure RRF (rank-2).");
    }

    [Test]
    public void FuseScored_ScoreAware_BreaksAnRrfTie_TowardTheHigherMagnitudeChunk()
    {
        var strong = new Guid("00000000-0000-0000-0000-00000000000a");
        var weak = new Guid("00000000-0000-0000-0000-00000000000b");
        var filler = new Guid("00000000-0000-0000-0000-00000000000c");

        // Arm 1: weak edges strong by a hair (rank 1 vs 2). Arm 2: strong crushes weak (rank 1 vs 2). Ranks are the
        // mirror image, so pure RRF ties strong and weak; the arm-2 blowout is the signal only score-aware can see.
        IReadOnlyList<IReadOnlyList<RankFusionInput>?> arms =
        [
            [new RankFusionInput(weak, 1.00d), new RankFusionInput(strong, 0.99d), new RankFusionInput(filler, 0.10d)],
            [new RankFusionInput(strong, 1.00d), new RankFusionInput(weak, 0.20d), new RankFusionInput(filler, 0.10d)]
        ];

        var pure = _fusion.FuseScored(arms, RankFusionStrategy.Rrf, scoreWeight: 1d);
        var aware = _fusion.FuseScored(arms, RankFusionStrategy.ScoreAware, scoreWeight: 1d);

        // Pure RRF ties strong and weak on score (mirror ranks) — the order is only a GUID coin-flip.
        var pureStrong = pure.Single(entry => entry.ChunkId == strong);
        var pureWeak = pure.Single(entry => entry.ChunkId == weak);
        AssertEx.True(Math.Abs(pureStrong.Score - pureWeak.Score) < 1e-12, "Pure RRF should tie the mirror-ranked chunks on score.");

        // Score-aware breaks the tie toward the chunk with the far larger arm-2 magnitude.
        AssertEx.Equal(strong, aware[0].ChunkId);
        var awareStrong = aware.Single(entry => entry.ChunkId == strong);
        var awareWeak = aware.Single(entry => entry.ChunkId == weak);
        AssertEx.True(awareStrong.Score > awareWeak.Score, "Score-aware fusion must rank the higher-magnitude chunk strictly above the marginal one.");
    }

    [Test]
    public void FuseScored_ScoreAware_WithConstantArmScores_DegradesToPureRrfOrder()
    {
        var a = new Guid("00000000-0000-0000-0000-0000000000a1");
        var b = new Guid("00000000-0000-0000-0000-0000000000b2");

        // Every score identical → no usable spread → the arm normalizes to neutral → pure RRF.
        var aware = _fusion.FuseScored([
            [new RankFusionInput(a, 7d), new RankFusionInput(b, 7d)]
        ], RankFusionStrategy.ScoreAware, scoreWeight: 3d);

        AssertEx.True(Math.Abs(aware[0].Score - (1d / 61d)) < 1e-12, "Constant-score arm must degrade to pure RRF (rank-1).");
        AssertEx.True(Math.Abs(aware[1].Score - (1d / 62d)) < 1e-12, "Constant-score arm must degrade to pure RRF (rank-2).");
    }

    [Test]
    public void FuseScored_ScoreAware_WithSingleEntryArm_AppliesNoTilt()
    {
        var only = new Guid("00000000-0000-0000-0000-0000000000a1");

        var aware = _fusion.FuseScored([
            [new RankFusionInput(only, 12345d)]
        ], RankFusionStrategy.ScoreAware, scoreWeight: 4d);

        // A single entry has no spread to normalize — the contribution is exactly the pure RRF rank-1 value.
        AssertEx.True(Math.Abs(aware.Single().Score - (1d / 61d)) < 1e-12, "A single-entry arm must apply no tilt.");
    }

    [Test]
    public void FuseScored_ScoreAware_WithNonFiniteScore_DegradesArmToPureRrf()
    {
        var a = new Guid("00000000-0000-0000-0000-0000000000a1");
        var b = new Guid("00000000-0000-0000-0000-0000000000b2");

        // A NaN score would poison min/max; the arm must fall back to neutral rather than produce NaN fused scores.
        var aware = _fusion.FuseScored([
            [new RankFusionInput(a, double.NaN), new RankFusionInput(b, 1d)]
        ], RankFusionStrategy.ScoreAware, scoreWeight: 2d);

        AssertEx.True(Math.Abs(aware[0].Score - (1d / 61d)) < 1e-12, "Non-finite score must degrade the arm to pure RRF (rank-1).");
        AssertEx.True(Math.Abs(aware[1].Score - (1d / 62d)) < 1e-12, "Non-finite score must degrade the arm to pure RRF (rank-2).");
    }

    [Test]
    public void FuseScored_SkipsNullAndEmptyArms_AndReturnsTheUnion()
    {
        var a = new Guid("00000000-0000-0000-0000-0000000000a1");
        var b = new Guid("00000000-0000-0000-0000-0000000000b2");

        var aware = _fusion.FuseScored([
            null,
            [],
            [new RankFusionInput(a, 1d), new RankFusionInput(b, 0d)]
        ], RankFusionStrategy.ScoreAware, scoreWeight: 1d);

        AssertEx.Equal(expected: 2, aware.Count);
        AssertEx.Equal(a, aware[0].ChunkId);
    }
}
