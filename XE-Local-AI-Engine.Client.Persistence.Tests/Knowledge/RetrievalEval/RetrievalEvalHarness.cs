namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Globalization;
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
    bool IsVectorOnly = false);

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
    double CitationCoverage);

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
    /// <summary>A one-line, culture-stable summary for test output / regression logs.</summary>
    public string Summarize() =>
        string.Create(CultureInfo.InvariantCulture,
            $"k={K} queries={QueryCount} recall@{K}={RecallAtK:F3} MRR={MeanReciprocalRank:F3} citationCoverage={CitationCoverage:F3}");
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
            var relevantDocumentId = documentIdsByKey[query.RelevantDocumentKey];
            var result = await search.SearchAsync(new KnowledgeSearchRequest(query.Text, Limit: k), cancellationToken).ConfigureAwait(false);
            perQuery.Add(EvaluateQuery(query, relevantDocumentId, result.Results));
        }

        var recall = perQuery.Count == 0 ? 0d : perQuery.Average(evaluation => evaluation.RelevantRetrieved ? 1d : 0d);
        var mrr = perQuery.Count == 0 ? 0d : perQuery.Average(evaluation => evaluation.ReciprocalRank);
        var citation = perQuery.Count == 0 ? 0d : perQuery.Average(evaluation => evaluation.CitationCoverage);
        return new RetrievalMetrics(k, perQuery.Count, recall, mrr, citation, perQuery);
    }

    private static QueryEvaluation EvaluateQuery(LabeledQuery query, Guid relevantDocumentId, IReadOnlyList<KnowledgeSearchHit> hits)
    {
        var firstRelevantRank = 0;
        for (var index = 0; index < hits.Count; index++)
        {
            if (hits[index].DocumentId == relevantDocumentId)
            {
                firstRelevantRank = index + 1;
                break;
            }
        }

        var relevantRetrieved = firstRelevantRank > 0;
        var reciprocalRank = relevantRetrieved ? 1d / firstRelevantRank : 0d;
        var coverage = ComputeCitationCoverage(query.CitationSnippet, hits);
        return new QueryEvaluation(query.Id, relevantRetrieved, firstRelevantRank, reciprocalRank, coverage);
    }

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
