namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

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
}
