namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Globalization;

/// <summary>
///     The labeled synthetic corpus for the retrieval-eval harness. Each document is a short markdown body with a
///     distinctive, mostly non-overlapping vocabulary; each query is labeled with the single document that answers it and
///     a supporting snippet used for citation coverage. The vocabulary is chosen so the deterministic-concept embedder
///     and the FTS/BM25 lexical arm agree on the relevant document for the topical queries, while one deliberately
///     lexically-disjoint query ("automobile") is retrievable ONLY through the synonym map's semantic arm.
/// </summary>
internal static class RetrievalEvalCorpus
{
    /// <summary>One synthetic corpus document.</summary>
    /// <param name="Key">Stable relevance-label key.</param>
    /// <param name="Body">The markdown body ingested through the real pipeline.</param>
    internal sealed record FixtureDocument(string Key, string Body);

    /// <summary>
    ///     Explicit synonym → concept map handed to the deterministic embedder. It is the harness's ONLY semantic
    ///     knowledge: "automobile" and "car" share the concept "car", so a query using one retrieves a document using the
    ///     other even with zero surface-token overlap — the signal that proves the vector arm contributes.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SynonymToConcept { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["automobile"] = "car"
    };

    public static IReadOnlyList<FixtureDocument> Documents { get; } =
    [
        new("vectordb",
            """
            # Vector databases

            Cosine similarity ranks dense embeddings by direction so a vector database can serve nearest
            neighbor lookups over high dimensional embedding vectors. Dense retrieval stores each passage as an
            embedding and scores approximate nearest neighbor candidates by cosine distance between the query
            embedding and every stored embedding vector in the index.
            """),
        new("fts",
            """
            # Full text search

            An inverted index powers keyword full text search: BM25 scores each document by term frequency across
            the tokenized lexical terms. SQLite FTS5 builds the inverted index and ranks matching rows with BM25 so a
            keyword query retrieves documents whose tokens overlap the lexical query terms.
            """),
        new("fusion",
            """
            # Reciprocal rank fusion

            Reciprocal rank fusion combines several ranked lists into one hybrid ranking by summing a reciprocal
            rank contribution per list. The rank aggregation constant damps top ranks so no single ranked list
            dominates the fused hybrid retrieval order across the combined candidate lists.
            """),
        new("gpu",
            """
            # GPU acceleration

            GPU acceleration offloads transformer layers onto CUDA cores so inference runs on the graphics card.
            VRAM holds the offloaded model weights and the llama runtime chooses how many layers to offload given
            the available VRAM budget on the CUDA device for fast tensor inference.
            """),
        new("chunking",
            """
            # Document chunking

            Chunking splits a document into overlapping windows at passage boundaries so a retrieval unit keeps
            local context. Each overlapping window carries trailing characters from the previous segment so a fact
            split across a boundary stays retrievable inside its own passage chunk.
            """),
        new("reranker",
            """
            # Cross encoder reranker

            A cross encoder reranker rescores candidate passages for relevance and reorders them before the final
            cut. The reranker scores each query candidate pair jointly, so a strong but lexically weak candidate is
            pulled up by the relevance rescoring during candidate reordering.
            """),
        new("vehicle",
            """
            # Car safety

            Car safety features protect occupants during a collision. The seatbelt restrains the passenger while the
            airbag cushions the impact, and anti lock braking shortens the stopping distance so the car avoids a
            crash. Modern car safety ratings reward strong collision protection.
            """)
    ];

    public static IReadOnlyList<LabeledQuery> Queries { get; } =
    [
        new("q-vectordb", "cosine similarity dense embeddings nearest neighbor", "vectordb", "cosine similarity embeddings"),
        new("q-fts", "BM25 inverted index keyword tokenized lexical terms", "fts", "inverted index BM25"),
        new("q-fusion", "reciprocal rank fusion combining ranked lists", "fusion", "reciprocal rank fusion"),
        new("q-gpu", "CUDA VRAM offload transformer layers graphics card", "gpu", "offload layers VRAM"),
        new("q-chunking", "overlapping windows passage boundaries retrieval unit", "chunking", "overlapping windows boundaries"),
        new("q-reranker", "cross encoder relevance rescoring candidate reordering", "reranker", "cross encoder rescoring"),

        // Vector-only: "automobile" shares no surface token with the "car safety" document; only the synonym map
        // (automobile → car) links them, so the lexical arm cannot retrieve it and the vector arm must.
        new("q-vehicle", "automobile", "vehicle", "car safety collision", IsVectorOnly: true)
    ];


    /// <summary>
    ///     An empty synonym map: the score-fusion scenario needs no semantic aliasing — it engineers the two arms purely
    ///     through term frequency (BM25) and concept-bag direction (cosine), so the embedder maps every token to itself.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ScoreFusionSynonyms { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    ///     A corpus deliberately built so classic (score-agnostic) Reciprocal Rank Fusion mis-orders the relevant
    ///     document while score-aware fusion recovers it. For the query <c>"aa bb"</c>:
    ///     <list type="bullet">
    ///         <item>
    ///             The two arms DISAGREE by rank in mirror image — the lexical (BM25) arm ranks
    ///             <c>lexspoiler &gt; relevant &gt; vecspoiler</c> (driven by <c>bb</c> term frequency: 4 &gt; 2 &gt; 1);
    ///             the semantic (cosine) arm ranks <c>vecspoiler &gt; relevant &gt; lexspoiler</c> (driven by concept-bag
    ///             balance: <c>vecspoiler</c> is exactly the query direction, <c>lexspoiler</c> is skewed toward <c>bb</c>).
    ///         </item>
    ///         <item>
    ///             So <c>relevant</c> is rank-2 in BOTH arms while each spoiler is rank-1 in one arm and rank-3 in the
    ///             other. Under pure RRF the two spoilers each score <c>1/(k+1)+1/(k+3)</c> and <c>relevant</c> scores
    ///             <c>2·1/(k+2)</c> — strictly LESS — so classic RRF pushes the relevant document to rank 3 (a result that
    ///             does not depend on any GUID tie-break). Score-aware fusion sees that <c>relevant</c>'s arm scores sit
    ///             far above each arm's floor and lifts it back to rank 1.
    ///         </item>
    ///     </list>
    ///     <c>aa</c> is a filler term present in every document (≈ zero IDF); <c>bb</c> appears in only the three candidate
    ///     documents out of fifteen, so it has a positive IDF and is the BM25 discriminator. The twelve <c>f*</c> filler
    ///     documents exist to make <c>bb</c> rare enough for FTS5's <c>bm25()</c> to score it above zero.
    /// </summary>
    public static IReadOnlyList<FixtureDocument> ScoreFusionDocuments { get; } = BuildScoreFusionDocuments();

    /// <summary>The single labeled query for the score-fusion scenario; its relevant document is <c>relevant</c>.</summary>
    public static IReadOnlyList<LabeledQuery> ScoreFusionQueries { get; } =
    [
        new("q-scorefusion-hard", "aa bb", "relevant", "aa bb")
    ];

    private static IReadOnlyList<FixtureDocument> BuildScoreFusionDocuments()
    {
        var documents = new List<FixtureDocument>
        {
            // bb x4, short: strongest BM25 (highest bb term frequency) but concept bag skewed toward bb → weakest cosine.
            new("lexspoiler", "aa bb bb bb bb"),
            // bb x2, balanced: rank-2 in BOTH arms — the chunk pure RRF drops and score-aware must recover.
            new("relevant", "aa bb bb"),
            // bb x1, exactly the query direction: strongest cosine but weakest BM25 of the three candidates.
            new("vecspoiler", "aa bb"),
        };

        // Filler documents carry the common term only (never bb), so bb stays rare (3 of 15) and keeps a positive IDF.
        for (var index = 0; index < 12; index++)
        {
            documents.Add(new FixtureDocument(string.Create(CultureInfo.InvariantCulture, $"f{index}"),
                string.Create(CultureInfo.InvariantCulture, $"aa c{index}x c{index}y")));
        }

        return documents;
    }
}
