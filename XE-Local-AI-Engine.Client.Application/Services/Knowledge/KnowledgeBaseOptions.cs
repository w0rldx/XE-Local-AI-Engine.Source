namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Node-local knowledge-base ingestion and embedding options, bound from the <c>KnowledgeBase</c> configuration
///     section. Defaults target the shipped node-local embedding model (<c>nomic-embed-text</c> via the llama.cpp
///     provider) and a conservative single-document-at-a-time ingestion budget so a batch upload cannot exhaust
///     CPU/RAM/VRAM. The search lane (Lane C) reuses <see cref="EmbeddingModelName" />/<see cref="EmbeddingProviderName" />
///     to build the query vector and to filter same-model chunk vectors.
/// </summary>
public sealed class KnowledgeBaseOptions
{
    public const string Section = "KnowledgeBase";

    /// <summary>Node-local embedding model that builds chunk vectors and the search query vector.</summary>
    public string EmbeddingModelName { get; set; } = "nomic-embed-text";

    /// <summary>Provider key for the embedding model; must match a registered node-local provider (default "llamacpp").</summary>
    public string EmbeddingProviderName { get; set; } = "llamacpp";

    /// <summary>
    ///     Maximum number of documents ingested concurrently by the background worker. Bounded (default 1) so N uploads
    ///     do not spin up N unbounded embedding pipelines and contend for the provider's loaded-process budget.
    /// </summary>
    public int MaxConcurrentIngestions { get; set; } = 1;

    /// <summary>
    ///     Maximum number of chunk texts sent to the embedding generator in a single <c>GenerateAsync</c> call. A large
    ///     document yields thousands of chunks; batching bounds each round-trip instead of one unbounded call.
    /// </summary>
    public int MaxEmbeddingBatchSize { get; set; } = 64;

    /// <summary>Upper bound on the plaintext length of a single chunk (characters), before overlap.</summary>
    public int MaxChunkChars { get; set; } = 2000;

    /// <summary>
    ///     Number of trailing characters carried from the end of one chunk into the start of the next, so a fact split
    ///     across a chunk boundary stays retrievable. Must be smaller than <see cref="MaxChunkChars" />.
    /// </summary>
    public int ChunkOverlapChars { get; set; } = 200;

    /// <summary>
    ///     Expected embedding vector dimensionality (e.g. 768 for <c>nomic-embed-text</c>). A generated vector whose
    ///     length differs marks the document <c>Failed</c> (model mismatch) rather than storing an incomparable vector.
    /// </summary>
    public int EmbeddingDimension { get; set; } = 768;
}
