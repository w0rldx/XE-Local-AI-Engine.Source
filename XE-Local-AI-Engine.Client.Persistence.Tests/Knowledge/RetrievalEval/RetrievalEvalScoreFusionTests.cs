namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Evidence for score-aware hybrid fusion vs classic (score-agnostic) Reciprocal Rank Fusion on the SAME ingested
///     indexes. Two claims are proven with the real <see cref="RetrievalEvalHarness" /> over the real
///     <see cref="KnowledgeSearchService" />:
///     <list type="number">
///         <item>
///             <b>Gain on a discriminating scenario.</b> A corpus engineered so the two arms disagree by rank in mirror
///             image pushes the relevant document to rank 3 under pure RRF; score-aware fusion recovers it to rank 1
///             (MRR 0.333 → 1.000), so the measured retrieval quality strictly improves.
///         </item>
///         <item>
///             <b>No regression on the saturated baseline.</b> The baseline corpus is already at recall@5 = MRR =
///             1.0, so score-aware fusion — the shipped default — must hold every metric at or above its bar and never
///             below the pure-RRF number. This is what justifies defaulting the feature ON.
///         </item>
///     </list>
/// </summary>
public sealed class RetrievalEvalScoreFusionTests : IDisposable
{
    private const int K = 5;
    private const double ScoreWeight = 1.0;

    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task ScoreAwareFusion_RecoversRelevantChunk_ThatPureRrfMisorders()
    {
        Directory.CreateDirectory(_rootPath);
        using var fixture = await RetrievalEvalFixture.BuildAsync(Path.Combine(_rootPath, "scorefusion-hard.sqlite"),
            _keyHolder,
            RetrievalEvalCorpus.ScoreFusionDocuments,
            RetrievalEvalCorpus.ScoreFusionSynonyms,
            CancellationToken.None).ConfigureAwait(false);

        var queries = RetrievalEvalCorpus.ScoreFusionQueries;

        var rrf = await RetrievalEvalHarness.EvaluateAsync(fixture.CreateHybridSearchService(RankFusionStrategy.Rrf, ScoreWeight), queries, fixture.DocumentIdsByKey, K, CancellationToken.None)
                                            .ConfigureAwait(false);
        var aware = await RetrievalEvalHarness.EvaluateAsync(
            fixture.CreateHybridSearchService(RankFusionStrategy.ScoreAware, ScoreWeight), queries, fixture.DocumentIdsByKey, K, CancellationToken.None).ConfigureAwait(false);

        Report("scorefusion-hard-rrf", rrf);
        Report("scorefusion-hard-aware", aware);

        // BEFORE: classic RRF ranks the relevant document behind BOTH spoilers (rank 3) — the score-agnostic mis-order.
        AssertEx.Equal(expected: 3, rrf.PerQuery[0].FirstRelevantRank);

        // AFTER: score-aware fusion pulls the relevant document to rank 1 (MRR 0.333 → 1.000), a strict, measured gain.
        AssertEx.Equal(expected: 1, aware.PerQuery[0].FirstRelevantRank);
        AssertEx.True(aware.MeanReciprocalRank > rrf.MeanReciprocalRank,
            $"Score-aware fusion must measurably improve MRR on the hard scenario. rrf={rrf.Summarize()} aware={aware.Summarize()}");

        // Recall is unchanged (the relevant document is inside the top-k either way) — the win is in the ORDER.
        AssertEx.True(Math.Abs(aware.RecallAtK - rrf.RecallAtK) < 1e-12, "Recall@k should be identical; the scenario isolates ordering.");
        AssertEx.True(aware.RecallAtK >= 1.0, $"Score-aware recall@{K} must stay saturated. {aware.Summarize()}");
    }

    [Test]
    public async Task ScoreAwareFusion_DoesNotRegress_TheSaturatedBaseline()
    {
        Directory.CreateDirectory(_rootPath);
        using var fixture = await RetrievalEvalFixture.BuildAsync(Path.Combine(_rootPath, "scorefusion-baseline.sqlite"), _keyHolder, CancellationToken.None).ConfigureAwait(false);

        var queries = RetrievalEvalFixture.Queries;

        var rrf = await RetrievalEvalHarness.EvaluateAsync(fixture.CreateHybridSearchService(RankFusionStrategy.Rrf, ScoreWeight), queries, fixture.DocumentIdsByKey, K, CancellationToken.None)
                                            .ConfigureAwait(false);
        var aware = await RetrievalEvalHarness.EvaluateAsync(
            fixture.CreateHybridSearchService(RankFusionStrategy.ScoreAware, ScoreWeight), queries, fixture.DocumentIdsByKey, K, CancellationToken.None).ConfigureAwait(false);

        Report("baseline-rrf", rrf);
        Report("baseline-aware", aware);

        // The default (score-aware) must clear the same bars the baseline asserts.
        AssertEx.True(aware.RecallAtK >= 1.0, $"score-aware recall@{K} regressed below the baseline. {aware.Summarize()}");
        AssertEx.True(aware.MeanReciprocalRank >= 0.90, $"score-aware MRR regressed below the baseline. {aware.Summarize()}");
        AssertEx.True(aware.CitationCoverage >= 0.90, $"score-aware citation coverage regressed below the baseline. {aware.Summarize()}");

        // And it must never come out BELOW pure RRF on this saturated corpus (small float tolerance).
        AssertEx.True(aware.RecallAtK >= rrf.RecallAtK - 1e-12, $"score-aware recall must not drop below RRF. rrf={rrf.Summarize()} aware={aware.Summarize()}");
        AssertEx.True(aware.MeanReciprocalRank >= rrf.MeanReciprocalRank - 1e-12, $"score-aware MRR must not drop below RRF. rrf={rrf.Summarize()} aware={aware.Summarize()}");
    }

    // Best-effort evidence capture for the report: append the measured metrics to a temp file. Never fails a test.
    private static void Report(string scenario, RetrievalMetrics metrics)
    {
        try
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"[{scenario}] {metrics.Summarize()} firstRelevantRank={metrics.PerQuery[0].FirstRelevantRank}{Environment.NewLine}");
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "rag04-scorefusion.txt"), line);
        }
        catch (IOException)
        {
            // Reporting is diagnostic only.
        }
    }
}
