namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Runs the committed live corpus end to end through the deterministic fixture and harness, so the corpus cannot
///     rot into something the real ingestion pipeline or the harness rejects.
/// </summary>
/// <remarks>
///     No quality floor on purpose: the concept embedder is not a semantic model, so its cross-language or paraphrase
///     scores say nothing about quality. Real-model numbers come from the opt-in live run.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class RetrievalEvalLiveCorpusSmokeTests : IDisposable
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
    public async Task CommittedCorpus_IngestsAndEvaluatesEveryQuery()
    {
        var corpus = RetrievalEvalLiveCorpus.Load(RetrievalEvalLiveCorpus.CommittedDirectory);
        Directory.CreateDirectory(_rootPath);
        using var fixture = await RetrievalEvalFixture.BuildAsync(Path.Combine(_rootPath, "live-corpus.sqlite"),
            _keyHolder,
            corpus.Documents,
            RetrievalEvalCorpus.ScoreFusionSynonyms,
            CancellationToken.None);

        var metrics = await RetrievalEvalHarness.EvaluateAsync(fixture.CreateHybridSearchService(),
            corpus.LabeledQueries,
            fixture.DocumentIdsByKey,
            K,
            CancellationToken.None);

        AssertEx.Equal(corpus.Queries.Count, metrics.QueryCount);
        AssertEx.Equal(corpus.Queries.Count(static query => query.Labeled.ExpectsNoAnswer), metrics.NoAnswerQueryCount);
        var evaluatedIds = metrics.PerQuery.Select(static evaluation => evaluation.QueryId).ToHashSet(StringComparer.Ordinal);
        AssertEx.True(corpus.Queries.All(query => evaluatedIds.Contains(query.Labeled.Id)), "Every corpus query must be evaluated.");
    }
}
