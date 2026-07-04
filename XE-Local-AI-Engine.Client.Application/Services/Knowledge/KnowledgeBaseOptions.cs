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

    /// <summary>
    ///     Provider key for the embedding model; must match a registered node-local provider (default "llamacpp"). The
    ///     default keeps embedding on-device: both the chunk text (at ingestion) and the query text (at search) are sent
    ///     only to the local llama.cpp process and never leave the node. Pointing this at a cloud embedding provider
    ///     would send that same chunk and query text off-node to a third party — a privacy tradeoff the operator
    ///     explicitly accepts by changing this value. Leave it on the local provider to keep knowledge-base content
    ///     private to the machine.
    /// </summary>
    public string EmbeddingProviderName { get; set; } = "llamacpp";

    /// <summary>
    ///     Node-local cross-encoder reranker model that rescores the fused candidate pool at search time. Empty
    ///     (default) turns reranking OFF — the search returns the Reciprocal-Rank-Fusion order unchanged. When set to an
    ///     installed reranker model name (for example <c>bge-reranker-v2-m3</c>), the search hydrates the fused candidate
    ///     pool, scores each candidate against the query on the local rerank-role llama-server (<c>/v1/rerank</c>), and
    ///     reorders by descending relevance before taking the top results. Like the embedding model this keeps
    ///     retrieval on-device: the query and chunk text are sent only to the local rerank process. If the model is not
    ///     installed or the rerank runtime is unavailable, the search silently degrades to the fusion order. Seeded from
    ///     the node settings store (stored value &gt; this config value &gt; off) so an operator can enable it without a
    ///     rebuild.
    /// </summary>
    public string RerankerModelName { get; set; } = string.Empty;

    /// <summary>
    ///     Whether the read-only knowledge-base agent tools (<c>search_knowledge_base</c>, <c>read_document</c>,
    ///     <c>read_surrounding_chunks</c>) are offered to agents and executed. Default <see langword="true" /> (the
    ///     feature is built); set to <see langword="false" /> to turn the tools off node-wide, in which case each handler
    ///     returns a short "tools are disabled" message instead of running a retrieval.
    /// </summary>
    public bool AgentToolsEnabled { get; set; } = true;

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
}
