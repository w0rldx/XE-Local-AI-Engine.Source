namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

public sealed class RetrievalEvalHarnessMetricTests : IDisposable
{
    private const int K = 5;

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
    public async Task EvaluateAsync_MultipleRelevantDocuments_ComputesPrecisionAndNdcgAtK()
    {
        var relevantA = Guid.NewGuid();
        var relevantB = Guid.NewGuid();
        var relevantBeyondCutoff = Guid.NewGuid();
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["a"] = relevantA,
            ["b"] = relevantB,
            ["c"] = relevantBeyondCutoff
        };
        var query = new LabeledQuery("multi", "retention audit", "a", "thirteen month retention schedule")
        {
            RelevantDocumentKeys = ["a", "b", "c"],
            SourceAnchors = ["Policy source", "Runbook source"]
        };
        var search = new FixedSearchService(new Dictionary<string, IReadOnlyList<KnowledgeSearchHit>>(StringComparer.Ordinal)
        {
            [query.Text] =
            [
                Hit(Guid.NewGuid(), "Distractor", "unrelated content"),
                Hit(relevantA, "Policy source", "the thirteen month retention schedule is mandatory"),
                Hit(Guid.NewGuid(), "Another distractor", "unrelated content"),
                Hit(relevantB, "Runbook source", "the runbook applies the retention schedule"),
                Hit(Guid.NewGuid(), "Cutoff distractor", "unrelated content"),
                Hit(relevantBeyondCutoff, "Late source", "must not count beyond k")
            ]
        });

        var metrics = await RetrievalEvalHarness.EvaluateAsync(search, [query], ids, K, CancellationToken.None).ConfigureAwait(false);

        var expectedNdcg = ((1d / Math.Log2(3d)) + (1d / Math.Log2(5d))) /
                           (1d + (1d / Math.Log2(3d)) + (1d / Math.Log2(4d)));
        AssertClose(2d / 3d, metrics.RecallAtK);
        AssertClose(0.4d, metrics.PrecisionAtK);
        AssertClose(expectedNdcg, metrics.NdcgAtK);
        AssertClose(0.5d, metrics.MeanReciprocalRank);
        AssertClose(1d, metrics.SourceAnchorCoverage);
        AssertClose(1d, metrics.CitationAnchorRate);
        AssertEx.Equal(expected: 2, metrics.PerQuery[0].RetrievedRelevantCount);
        AssertEx.Equal(expected: 3, metrics.PerQuery[0].RelevantDocumentCount);
        AssertEx.True(metrics.QueryLatencyP50Milliseconds >= 0d);
        AssertEx.True(metrics.QueryLatencyP95Milliseconds >= metrics.QueryLatencyP50Milliseconds);
        AssertEx.True(metrics.Summarize().Contains("latencyP95Ms=", StringComparison.Ordinal));
    }

    [Test]
    public async Task EvaluateAsync_NoAnswerQueries_ReportAbstentionAccuracy_WithoutDilutingAnswerableMetrics()
    {
        var relevant = Guid.NewGuid();
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["answer"] = relevant
        };
        var answerable = new LabeledQuery("answerable", "known", "answer", "grounded answer");
        var correctAbstention = NoAnswerQuery("empty", "unknown-empty");
        var falsePositive = NoAnswerQuery("noise", "unknown-noise");
        var search = new FixedSearchService(new Dictionary<string, IReadOnlyList<KnowledgeSearchHit>>(StringComparer.Ordinal)
        {
            [answerable.Text] = [Hit(relevant, "Answer", "grounded answer")],
            [correctAbstention.Text] = [],
            [falsePositive.Text] = [Hit(Guid.NewGuid(), "Noise", "unsupported guess")]
        });

        var metrics = await RetrievalEvalHarness.EvaluateAsync(search,
            [answerable, correctAbstention, falsePositive], ids, K, CancellationToken.None).ConfigureAwait(false);

        AssertClose(1d, metrics.RecallAtK);
        AssertClose(1d, metrics.MeanReciprocalRank);
        AssertClose(0.5d, metrics.NoAnswerAccuracy);
        AssertEx.Equal(expected: 1, metrics.AnswerableQueryCount);
        AssertEx.Equal(expected: 2, metrics.NoAnswerQueryCount);
        AssertEx.True(metrics.PerQuery.Single(result => result.QueryId == "empty").NoAnswerCorrect);
        AssertEx.False(metrics.PerQuery.Single(result => result.QueryId == "noise").NoAnswerCorrect);
    }

    [Test]
    public void RepresentativeCorpus_DefinesEveryRequiredDeterministicScenarioGroup()
    {
        var groups = RetrievalEvalRepresentativeCorpus.AnswerableQueries
                                                      .Concat(RetrievalEvalRepresentativeCorpus.NoAnswerQueries)
                                                      .Select(query => query.ScenarioGroup)
                                                      .ToHashSet(StringComparer.Ordinal);

        AssertEx.True(groups.SetEquals([
            "english",
            "german",
            "code-exact-identifier-path",
            "distractor",
            "long-document-boundary",
            "multi-source",
            "no-answer"
        ]));
    }

    [Test]
    public async Task RepresentativeCorpus_RealPipeline_RetrievesAnswerableGroups_AndAbstainsWhenLexicalEvidenceIsAbsent()
    {
        Directory.CreateDirectory(_rootPath);
        using var fixture = await RetrievalEvalFixture.BuildAsync(Path.Combine(_rootPath, "representative.sqlite"),
            _keyHolder,
            RetrievalEvalRepresentativeCorpus.Documents,
            RetrievalEvalCorpus.ScoreFusionSynonyms,
            CancellationToken.None).ConfigureAwait(false);

        var answerable = await RetrievalEvalHarness.EvaluateAsync(fixture.CreateHybridSearchService(),
            RetrievalEvalRepresentativeCorpus.AnswerableQueries,
            fixture.DocumentIdsByKey,
            K,
            CancellationToken.None).ConfigureAwait(false);
        var noAnswer = await RetrievalEvalHarness.EvaluateAsync(fixture.CreateLexicalOnlySearchService(),
            RetrievalEvalRepresentativeCorpus.NoAnswerQueries,
            fixture.DocumentIdsByKey,
            K,
            CancellationToken.None).ConfigureAwait(false);

        AssertClose(1d, answerable.RecallAtK);
        AssertEx.True(answerable.NdcgAtK >= 0.80d, $"Representative ordering regressed. {answerable.Summarize()}");
        AssertEx.True(answerable.CitationCoverage >= 0.90d, $"Representative citation coverage regressed. {answerable.Summarize()}");
        AssertClose(1d, noAnswer.NoAnswerAccuracy);
    }

    private static LabeledQuery NoAnswerQuery(string id, string text) =>
        new(id, text, string.Empty, string.Empty)
        {
            RelevantDocumentKeys = [],
            ExpectsNoAnswer = true,
            ScenarioGroup = "no-answer"
        };

    private static KnowledgeSearchHit Hit(Guid documentId, string title, string content) =>
        new(documentId,
            Guid.NewGuid(),
            title,
            title,
            content,
            "knowledge-base",
            1d,
            0,
            KnowledgeDocumentStatus.Indexed,
            ServingLastKnownGood: false);

    private static void AssertClose(double expected, double actual) =>
        AssertEx.True(Math.Abs(expected - actual) < 1e-12, $"Expected {expected:R}, actual {actual:R}.");

    private sealed class FixedSearchService : IKnowledgeSearchService
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<KnowledgeSearchHit>> _hitsByQuery;

        public FixedSearchService(IReadOnlyDictionary<string, IReadOnlyList<KnowledgeSearchHit>> hitsByQuery) =>
            _hitsByQuery = hitsByQuery;

        public Task<KnowledgeSearchResult> SearchAsync(KnowledgeSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new KnowledgeSearchResult(_hitsByQuery[request.Query]));
    }
}
