namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Globalization;
using System.Diagnostics;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Shared, deterministic tokenizer + stable hash used by the retrieval-eval harness. Kept in one place so the
///     concept embedder (chunk/query vectors) and the metric computation (citation coverage) split text identically:
///     lowercase, and break on any non-letter/non-digit run.
/// </summary>
internal static class RetrievalTokens
{
    // The two intent prefixes the KnowledgeEmbeddingPrefixer prepends tokenize to these words; dropping them keeps a
    // query vector and a document vector comparable on their content concepts only.
    private static readonly HashSet<string> IntentPrefixStopwords = new(StringComparer.Ordinal)
    {
        "search",
        "query",
        "document"
    };

    public static IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                _ = current.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                _ = current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    public static bool IsIntentPrefixStopword(string token) =>
        IntentPrefixStopwords.Contains(token);

    // FNV-1a 32-bit — a process-stable hash (unlike string.GetHashCode, which is randomized per process), so a concept
    // always maps to the same vector dimension across runs and machines.
    public static uint Fnv1a(string value)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash;
    }
}

/// <summary>
///     One labeled evaluation query: its text, the fixture document it should retrieve, and a short supporting snippet
///     whose tokens the retrieved chunks are expected to cover (the lexical citation-coverage signal).
/// </summary>
/// <param name="Id">Stable identifier for reporting.</param>
/// <param name="Text">The query handed to the search service.</param>
/// <param name="RelevantDocumentKey">Fixture key of the document that answers the query (relevance label).</param>
/// <param name="CitationSnippet">A phrase expected to be supported by (lexically present in) the retrieved chunks.</param>
/// <param name="IsVectorOnly">
///     True when the query is designed to have NO lexical overlap with its relevant document (retrievable only via the
///     semantic/vector arm through the fixture's synonym map). Used to prove the vector arm contributes.
/// </param>
public sealed record LabeledQuery(
    string Id,
    string Text,
    string RelevantDocumentKey,
    string CitationSnippet,
    bool IsVectorOnly = false)
{
    /// <summary>
    ///     All documents that are relevant to this query. The default preserves the original single-label contract.
    /// </summary>
    public IReadOnlyList<string> RelevantDocumentKeys { get; init; } = [RelevantDocumentKey];

    /// <summary>True when a correct retriever should return no hits.</summary>
    public bool ExpectsNoAnswer { get; init; }

    /// <summary>Expected lexical anchors in hit title, section, or source metadata.</summary>
    public IReadOnlyList<string> SourceAnchors { get; init; } = [];

    /// <summary>Stable scenario family used to group representative eval cases.</summary>
    public string ScenarioGroup { get; init; } = "baseline";
}

/// <summary>Per-query evaluation outcome, retained so a caller can inspect exactly which query regressed.</summary>
/// <param name="QueryId">The <see cref="LabeledQuery.Id" />.</param>
/// <param name="RelevantRetrieved">Whether the relevant document appeared within the top-k hits.</param>
/// <param name="FirstRelevantRank">1-based rank of the first relevant hit, or 0 when none was retrieved.</param>
/// <param name="ReciprocalRank">1/<see cref="FirstRelevantRank" />, or 0 when the relevant document was not retrieved.</param>
/// <param name="CitationCoverage">Fraction of the citation snippet's tokens present in the retrieved chunk text.</param>
public sealed record QueryEvaluation(
    string QueryId,
    bool RelevantRetrieved,
    int FirstRelevantRank,
    double ReciprocalRank,
    double CitationCoverage)
{
    /// <summary>Number of distinct relevant documents retrieved within the cut-off.</summary>
    public int RetrievedRelevantCount { get; init; }

    /// <summary>Number of distinct documents labeled relevant for this query.</summary>
    public int RelevantDocumentCount { get; init; }

    /// <summary>Relevant documents divided by the fixed cut-off k.</summary>
    public double PrecisionAtK { get; init; }

    /// <summary>Binary-gain normalized discounted cumulative gain at the fixed cut-off k.</summary>
    public double NdcgAtK { get; init; }

    /// <summary>Fraction of expected anchors found in hit title, section, or source metadata.</summary>
    public double SourceAnchorCoverage { get; init; }

    /// <summary>True when the expected citation phrase occurs contiguously in at least one retrieved chunk.</summary>
    public bool CitationAnchorPresent { get; init; }

    /// <summary>True for an explicit no-answer label.</summary>
    public bool ExpectsNoAnswer { get; init; }

    /// <summary>True when a no-answer query produced zero hits; false for answerable queries.</summary>
    public bool NoAnswerCorrect { get; init; }

    /// <summary>Observed end-to-end search latency for this query, including query embedding and optional reranking.</summary>
    public double ElapsedMilliseconds { get; init; }
}

/// <summary>
///     Structured retrieval-quality metrics over a labeled query set at a fixed cut-off <c>k</c>. Macro-averaged across
///     queries. Reusable across implementations: a fusion/reranker change invokes the same harness before and
///     after to prove a measured gain.
/// </summary>
/// <param name="K">The top-k cut-off the metrics were computed at.</param>
/// <param name="QueryCount">Number of labeled queries evaluated.</param>
/// <param name="RecallAtK">Fraction of queries whose relevant document appeared within the top-k hits.</param>
/// <param name="MeanReciprocalRank">Mean over queries of 1/(rank of the first relevant hit).</param>
/// <param name="CitationCoverage">Mean over queries of the citation-snippet token coverage in the retrieved chunks.</param>
/// <param name="PerQuery">Per-query breakdown.</param>
public sealed record RetrievalMetrics(
    int K,
    int QueryCount,
    double RecallAtK,
    double MeanReciprocalRank,
    double CitationCoverage,
    IReadOnlyList<QueryEvaluation> PerQuery)
{
    /// <summary>Macro-averaged precision@k over answerable queries.</summary>
    public double PrecisionAtK { get; init; }

    /// <summary>Macro-averaged nDCG@k over answerable queries.</summary>
    public double NdcgAtK { get; init; }

    /// <summary>Mean source-anchor coverage over answerable queries.</summary>
    public double SourceAnchorCoverage { get; init; }

    /// <summary>Fraction of answerable queries with a contiguous supporting citation phrase.</summary>
    public double CitationAnchorRate { get; init; }

    /// <summary>Fraction of explicit no-answer queries for which the retriever returned zero hits.</summary>
    public double NoAnswerAccuracy { get; init; }

    /// <summary>Number of answerable queries included in retrieval-quality averages.</summary>
    public int AnswerableQueryCount { get; init; }

    /// <summary>Number of explicit no-answer queries.</summary>
    public int NoAnswerQueryCount { get; init; }

    /// <summary>Nearest-rank median end-to-end query latency.</summary>
    public double QueryLatencyP50Milliseconds { get; init; }

    /// <summary>Nearest-rank p95 end-to-end query latency.</summary>
    public double QueryLatencyP95Milliseconds { get; init; }

    /// <summary>Slowest observed end-to-end query latency.</summary>
    public double QueryLatencyMaxMilliseconds { get; init; }

    /// <summary>A one-line, culture-stable summary for test output / regression logs.</summary>
    public string Summarize() =>
        string.Create(CultureInfo.InvariantCulture,
            $"k={K} queries={QueryCount} recall@{K}={RecallAtK:F3} precision@{K}={PrecisionAtK:F3} nDCG@{K}={NdcgAtK:F3} MRR={MeanReciprocalRank:F3} citationCoverage={CitationCoverage:F3} sourceAnchors={SourceAnchorCoverage:F3} noAnswer={NoAnswerAccuracy:F3} latencyP50Ms={QueryLatencyP50Milliseconds:F1} latencyP95Ms={QueryLatencyP95Milliseconds:F1}");
}

/// <summary>
///     Runs a labeled query set through a REAL <see cref="IKnowledgeSearchService" /> and computes
///     <see cref="RetrievalMetrics" /> (recall@k, MRR, lexical citation coverage). It is deliberately agnostic to how the
///     search service is wired — hybrid, lexical-only, or reranked — so the same call measures any variant. This is the
///     reusable entry point a fusion/reranker change invokes to obtain before/after numbers.
/// </summary>
public static class RetrievalEvalHarness
{
    public static async Task<RetrievalMetrics> EvaluateAsync(IKnowledgeSearchService search,
        IReadOnlyList<LabeledQuery> queries,
        IReadOnlyDictionary<string, Guid> documentIdsByKey,
        int k,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(documentIdsByKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        var perQuery = new List<QueryEvaluation>(queries.Count);
        foreach (var query in queries)
        {
            var relevantDocumentIds = ResolveRelevantDocumentIds(query, documentIdsByKey);
            var startedAt = Stopwatch.GetTimestamp();
            var result = await search.SearchAsync(new KnowledgeSearchRequest(query.Text, Limit: k), cancellationToken).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            perQuery.Add(EvaluateQuery(query, relevantDocumentIds, result.Results, k) with { ElapsedMilliseconds = elapsed });
        }

        var answerable = perQuery.Where(evaluation => !evaluation.ExpectsNoAnswer).ToList();
        var noAnswer = perQuery.Where(evaluation => evaluation.ExpectsNoAnswer).ToList();
        var recall = AverageOrZero(answerable, evaluation => evaluation.RelevantDocumentCount == 0
            ? 0d
            : (double)evaluation.RetrievedRelevantCount / evaluation.RelevantDocumentCount);
        var mrr = AverageOrZero(answerable, evaluation => evaluation.ReciprocalRank);
        var citation = AverageOrZero(answerable, evaluation => evaluation.CitationCoverage);
        return new RetrievalMetrics(k, perQuery.Count, recall, mrr, citation, perQuery)
        {
            PrecisionAtK = AverageOrZero(answerable, evaluation => evaluation.PrecisionAtK),
            NdcgAtK = AverageOrZero(answerable, evaluation => evaluation.NdcgAtK),
            SourceAnchorCoverage = AverageOrZero(answerable, evaluation => evaluation.SourceAnchorCoverage),
            CitationAnchorRate = AverageOrZero(answerable, evaluation => evaluation.CitationAnchorPresent ? 1d : 0d),
            NoAnswerAccuracy = AverageOrZero(noAnswer, evaluation => evaluation.NoAnswerCorrect ? 1d : 0d),
            AnswerableQueryCount = answerable.Count,
            NoAnswerQueryCount = noAnswer.Count,
            QueryLatencyP50Milliseconds = Percentile(perQuery, 0.50d),
            QueryLatencyP95Milliseconds = Percentile(perQuery, 0.95d),
            QueryLatencyMaxMilliseconds = perQuery.Count == 0 ? 0d : perQuery.Max(static evaluation => evaluation.ElapsedMilliseconds)
        };
    }

    private static IReadOnlySet<Guid> ResolveRelevantDocumentIds(LabeledQuery query,
        IReadOnlyDictionary<string, Guid> documentIdsByKey)
    {
        if (query.ExpectsNoAnswer)
        {
            return new HashSet<Guid>();
        }

        if (query.RelevantDocumentKeys.Count == 0)
        {
            throw new ArgumentException($"Answerable query '{query.Id}' must label at least one relevant document.", nameof(query));
        }

        return query.RelevantDocumentKeys.Select(key => documentIdsByKey[key]).ToHashSet();
    }

    private static QueryEvaluation EvaluateQuery(LabeledQuery query,
        IReadOnlySet<Guid> relevantDocumentIds,
        IReadOnlyList<KnowledgeSearchHit> hits,
        int k)
    {
        var evaluatedHits = hits.Take(k).ToList();
        var firstRelevantRank = 0;
        for (var index = 0; index < evaluatedHits.Count; index++)
        {
            if (relevantDocumentIds.Contains(evaluatedHits[index].DocumentId))
            {
                firstRelevantRank = index + 1;
                break;
            }
        }

        var relevantRetrieved = firstRelevantRank > 0;
        var reciprocalRank = relevantRetrieved ? 1d / firstRelevantRank : 0d;
        var coverage = ComputeCitationCoverage(query.CitationSnippet, evaluatedHits);
        var retrievedRelevantCount = evaluatedHits.Select(hit => hit.DocumentId).Distinct().Count(relevantDocumentIds.Contains);
        return new QueryEvaluation(query.Id, relevantRetrieved, firstRelevantRank, reciprocalRank, coverage)
        {
            RetrievedRelevantCount = retrievedRelevantCount,
            RelevantDocumentCount = relevantDocumentIds.Count,
            PrecisionAtK = (double)retrievedRelevantCount / k,
            NdcgAtK = ComputeNdcgAtK(relevantDocumentIds, evaluatedHits, k),
            SourceAnchorCoverage = ComputeSourceAnchorCoverage(query.SourceAnchors, evaluatedHits),
            CitationAnchorPresent = ContainsCitationAnchor(query.CitationSnippet, evaluatedHits),
            ExpectsNoAnswer = query.ExpectsNoAnswer,
            NoAnswerCorrect = query.ExpectsNoAnswer && evaluatedHits.Count == 0
        };
    }

    private static double AverageOrZero(IReadOnlyCollection<QueryEvaluation> evaluations,
        Func<QueryEvaluation, double> selector) =>
        evaluations.Count == 0 ? 0d : evaluations.Average(selector);

    private static double Percentile(IReadOnlyCollection<QueryEvaluation> evaluations, double percentile)
    {
        if (evaluations.Count == 0)
        {
            return 0d;
        }

        var ordered = evaluations.Select(static evaluation => evaluation.ElapsedMilliseconds).Order().ToArray();
        var index = Math.Max(0, (int)Math.Ceiling(percentile * ordered.Length) - 1);
        return ordered[index];
    }

    private static double ComputeNdcgAtK(IReadOnlySet<Guid> relevantDocumentIds,
        IReadOnlyList<KnowledgeSearchHit> hits,
        int k)
    {
        if (relevantDocumentIds.Count == 0)
        {
            return 0d;
        }

        var seen = new HashSet<Guid>();
        var dcg = 0d;
        for (var index = 0; index < Math.Min(k, hits.Count); index++)
        {
            if (relevantDocumentIds.Contains(hits[index].DocumentId) && seen.Add(hits[index].DocumentId))
            {
                dcg += 1d / Math.Log2(index + 2d);
            }
        }

        var idealRelevantCount = Math.Min(k, relevantDocumentIds.Count);
        var idealDcg = Enumerable.Range(0, idealRelevantCount).Sum(index => 1d / Math.Log2(index + 2d));
        return dcg / idealDcg;
    }

    private static double ComputeSourceAnchorCoverage(IReadOnlyList<string> anchors, IReadOnlyList<KnowledgeSearchHit> hits)
    {
        if (anchors.Count == 0)
        {
            return 1d;
        }

        var metadata = string.Join(' ', hits.Select(hit => string.Join(' ', hit.Title, hit.Section, hit.Source)));
        var normalizedMetadata = NormalizeAnchor(metadata);
        var covered = anchors.Count(anchor => normalizedMetadata.Contains(NormalizeAnchor(anchor), StringComparison.Ordinal));
        return (double)covered / anchors.Count;
    }

    private static bool ContainsCitationAnchor(string snippet, IReadOnlyList<KnowledgeSearchHit> hits)
    {
        if (string.IsNullOrWhiteSpace(snippet))
        {
            return true;
        }

        var normalizedSnippet = NormalizeAnchor(snippet);
        return hits.Any(hit => NormalizeAnchor(hit.Content).Contains(normalizedSnippet, StringComparison.Ordinal));
    }

    private static string NormalizeAnchor(string? value) =>
        string.Join(' ', RetrievalTokens.Split(value ?? string.Empty));

    // Lexical faithfulness signal: the fraction of the expected supporting snippet's distinct tokens that appear
    // anywhere in the retrieved chunk text. A high value means the retrieved chunks actually contain the words a citation
    // would quote; it is a deterministic proxy, NOT a semantic-entailment check.
    private static double ComputeCitationCoverage(string snippet, IReadOnlyList<KnowledgeSearchHit> hits)
    {
        var snippetTokens = RetrievalTokens.Split(snippet).ToHashSet(StringComparer.Ordinal);
        if (snippetTokens.Count == 0)
        {
            return 1d;
        }

        var retrievedTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in hits)
        {
            foreach (var token in RetrievalTokens.Split(hit.Content))
            {
                _ = retrievedTokens.Add(token);
            }
        }

        var covered = snippetTokens.Count(retrievedTokens.Contains);
        return (double)covered / snippetTokens.Count;
    }
}
