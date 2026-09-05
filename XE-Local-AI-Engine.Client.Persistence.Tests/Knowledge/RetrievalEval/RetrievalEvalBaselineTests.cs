namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Globalization;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Baseline retrieval-quality assertions for the CURRENT knowledge-base retrieval stack. The corpus is
///     ingested through the real pipeline and queried through the real <see cref="KnowledgeSearchService" />; the
///     thresholds asserted here are the measured baseline on the current tree — a code regression in chunking, FTS,
///     vector search, RRF, or reranker integration drops a metric below its bar and fails.
///     <para>
///         SCOPE: deterministic retrieval MECHANICS + lexical/concept quality. These numbers do NOT reflect real
///         embedding-model semantic quality (the fake concept embedder has none beyond the fixture synonym map). A
///         model-backed semantic eval is a separate, opt-in run and is intentionally out of scope here.
///     </para>
/// </summary>
public sealed class RetrievalEvalBaselineTests : IDisposable
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
    public async Task HybridSearch_MeetsBaselineRetrievalThresholds()
    {
        using var fixture = await BuildFixtureAsync("hybrid.sqlite").ConfigureAwait(false);
        var search = fixture.CreateHybridSearchService();

        var metrics = await RetrievalEvalHarness.EvaluateAsync(search, RetrievalEvalFixture.Queries, fixture.DocumentIdsByKey, K, CancellationToken.None).ConfigureAwait(false);
        Report("hybrid", metrics);

        // Baseline bar on the current tree: every labeled query's relevant document is retrieved within the top-k.
        AssertEx.True(metrics.RecallAtK >= 1.0, $"recall@{K} regressed below the baseline. {metrics.Summarize()}");
        AssertEx.True(metrics.MeanReciprocalRank >= 0.90, $"MRR regressed below the baseline. {metrics.Summarize()}");
        AssertEx.True(metrics.CitationCoverage >= 0.90, $"citation coverage regressed below the baseline. {metrics.Summarize()}");
    }

    [Test]
    public async Task VectorArm_RetrievesLexicallyDisjointQuery_ThatLexicalOnlyCannot()
    {
        using var fixture = await BuildFixtureAsync("vector-arm.sqlite").ConfigureAwait(false);
        var vehicleQuery = RetrievalEvalFixture.Queries.Single(query => query.IsVectorOnly);
        var singleQuery = new[]
        {
            vehicleQuery
        };

        var hybrid = await RetrievalEvalHarness.EvaluateAsync(fixture.CreateHybridSearchService(), singleQuery, fixture.DocumentIdsByKey, K, CancellationToken.None).ConfigureAwait(false);
        var lexicalOnly = await RetrievalEvalHarness.EvaluateAsync(fixture.CreateLexicalOnlySearchService(), singleQuery, fixture.DocumentIdsByKey, K, CancellationToken.None).ConfigureAwait(false);
        Report("vector-arm-hybrid", hybrid);
        Report("vector-arm-lexical", lexicalOnly);

        // The vector arm finds the synonym-linked document; the lexical arm alone cannot (zero surface-token overlap).
        AssertEx.True(hybrid.PerQuery[0].RelevantRetrieved, $"The vector arm must retrieve the lexically-disjoint query. {hybrid.Summarize()}");
        AssertEx.False(lexicalOnly.PerQuery[0].RelevantRetrieved, $"The lexical arm must NOT retrieve the lexically-disjoint query. {lexicalOnly.Summarize()}");
    }

    [Test]
    public async Task LexicalOnly_DegradesGracefully_AndStillAnswersLexicalQueries()
    {
        using var fixture = await BuildFixtureAsync("lexical-only.sqlite").ConfigureAwait(false);
        var lexicalQueries = RetrievalEvalFixture.Queries.Where(query => !query.IsVectorOnly).ToList();
        var search = fixture.CreateLexicalOnlySearchService();

        var metrics = await RetrievalEvalHarness.EvaluateAsync(search, lexicalQueries, fixture.DocumentIdsByKey, K, CancellationToken.None).ConfigureAwait(false);
        Report("lexical-only", metrics);

        // With the vector arm disabled, RRF is the lexical (FTS) ranking alone — the topical queries still resolve.
        AssertEx.True(metrics.RecallAtK >= 1.0, $"lexical-only recall@{K} regressed below the baseline. {metrics.Summarize()}");
        AssertEx.True(metrics.MeanReciprocalRank >= 0.90, $"lexical-only MRR regressed below the baseline. {metrics.Summarize()}");
    }

    [Test]
    public async Task RerankerPath_IsExercised_AndKeepsRetrievalCorrect()
    {
        using var fixture = await BuildFixtureAsync("reranked.sqlite").ConfigureAwait(false);

        // A deterministic token-overlap reranker standing in for the local cross-encoder: it rescores each candidate by
        // how many query tokens the candidate text contains, proving the rerank stage is wired end to end.
        var invocations = 0;
        var reranker = Substitute.For<IRerankerClient>();
        reranker.RerankAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    Interlocked.Increment(ref invocations);
                    var query = callInfo.ArgAt<string>(1);
                    var documents = callInfo.ArgAt<IReadOnlyList<string>>(2);
                    return (IReadOnlyList<double>?)documents.Select(document => TokenOverlap(query, document)).ToList();
                });

        // A generous budget on purpose: this test proves the reranker is WIRED, not that it survives a deadline,
        // and the fixture default of 500 ms is spent on real SQLite I/O before rerank is reached — under box load
        // one query overruns it, RerankWithinBudgetAsync skips the call, and the per-query invocation count fails.
        // Reranker_WhenRemainingRetrievalBudgetExpires_DegradesToFusionOrder owns the tight-budget path.
        var search = fixture.CreateRerankedSearchService(reranker, retrievalLatencyBudgetMilliseconds: 30_000);
        var metrics = await RetrievalEvalHarness.EvaluateAsync(search, RetrievalEvalFixture.Queries, fixture.DocumentIdsByKey, K, CancellationToken.None).ConfigureAwait(false);
        Report("reranked", metrics);

        AssertEx.True(invocations >= RetrievalEvalFixture.Queries.Count, $"The reranker must be invoked for every query (was {invocations}).");
        AssertEx.True(metrics.RecallAtK >= 1.0, $"reranked recall@{K} regressed below the baseline. {metrics.Summarize()}");
    }

    [Test]
    public async Task Reranker_WhenRemainingRetrievalBudgetExpires_DegradesToFusionOrder()
    {
        using var fixture = await BuildFixtureAsync("reranker-budget.sqlite").ConfigureAwait(false);
        var reranker = new BudgetObservingReranker();
        var search = fixture.CreateRerankedSearchService(reranker, retrievalLatencyBudgetMilliseconds: 500);

        var result = await search.SearchAsync(new KnowledgeSearchRequest("retention period", Limit: 3), CancellationToken.None).ConfigureAwait(false);

        if (reranker.InvocationObserved)
        {
            AssertEx.True(await reranker.WaitForCancellationAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false),
                "When optional reranking starts, the remaining per-search deadline must cancel model acquisition/scoring rather than permitting the provider's multi-second timeout.");
        }

        AssertEx.True(result.Results.Count > 0, "A reranker budget expiry must preserve the fused retrieval results.");
    }

    // Number of distinct query tokens present in the candidate document text — a deterministic reranker relevance score.
    private static double TokenOverlap(string query, string document)
    {
        var documentTokens = RetrievalTokens.Split(document).ToHashSet(StringComparer.Ordinal);
        return RetrievalTokens.Split(query).Distinct(StringComparer.Ordinal).Count(documentTokens.Contains);
    }

    private sealed class BudgetObservingReranker : IRerankerClient
    {
        private readonly TaskCompletionSource _cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocationObserved;

        public bool InvocationObserved => Volatile.Read(ref _invocationObserved) != 0;

        public async Task<bool> WaitForCancellationAsync(TimeSpan timeout)
        {
            try
            {
                await _cancellationObserved.Task.WaitAsync(timeout).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public async Task<IReadOnlyList<double>?> RerankAsync(string modelName,
            string query,
            IReadOnlyList<string> documents,
            CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _invocationObserved, 1);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException)
            {
                _cancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    private Task<RetrievalEvalFixture> BuildFixtureAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return RetrievalEvalFixture.BuildAsync(Path.Combine(_rootPath, fileName), _keyHolder, CancellationToken.None);
    }

    // Best-effort baseline capture for the report: append the measured metrics to a temp file. Never fails a test.
    private static void Report(string scenario, RetrievalMetrics metrics)
    {
        try
        {
            var line = string.Create(CultureInfo.InvariantCulture, $"[{scenario}] {metrics.Summarize()}{Environment.NewLine}");
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "rag01-baseline.txt"), line);
        }
        catch (IOException)
        {
            // Reporting is diagnostic only.
        }
    }
}
