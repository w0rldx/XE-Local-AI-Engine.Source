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
    ///     The single opt-in governing whether a CLOUD-hosted model (Codex OAuth, Azure Foundry) may receive ANY
    ///     node-local private data: the read-only knowledge-base tools, the coder workspace file tools
    ///     (<c>list_files</c> / <c>read_file</c> / <c>search_text</c>), AND conversation attachments (inlined text or
    ///     staged files). Default <see langword="false" />: all of that is offered/composed ONLY for a node-local
    ///     effective model (llama.cpp / Ollama); for a cloud effective model the tools are withheld from the offer and
    ///     attachments are neither staged nor inlined (the user gets a visible turn notice). The gate keys on the
    ///     EFFECTIVE model (after any agent/profile pin), so a cloud-pinned agent on a local-active turn is gated too.
    ///     Setting this to <see langword="true" /> is an explicit acknowledgement that a third-party cloud provider may
    ///     then receive that node-local content. Named under <c>KnowledgeBase</c> for continuity; its scope is broader.
    ///     Independent of <see cref="AgentToolsEnabled" /> (which turns the knowledge tools off node-wide).
    /// </summary>
    public bool AllowCloudModelAccess { get; set; }

    /// <summary>
    ///     Maximum number of documents ingested concurrently by the background worker. Bounded (default 1) so N uploads
    ///     do not spin up N unbounded embedding pipelines and contend for the provider's loaded-process budget.
    /// </summary>
    public int MaxConcurrentIngestions { get; set; } = 1;

    /// <summary>
    ///     Maximum time (seconds) the background worker waits at host shutdown for the documents it is currently ingesting
    ///     to reach a terminal state before abandoning them. During the window each in-flight document runs uncancelled so
    ///     a near-complete index write still lands; once the window elapses the shared drain token is cancelled so a hung
    ///     document cannot block shutdown, and any document not finished is left non-terminal and re-queued on the next
    ///     start. Default 30s. Clamped to at least 1s.
    /// </summary>
    public int ShutdownDrainTimeoutSeconds { get; set; } = 30;

    /// <summary>
    ///     Maximum number of chunk texts sent to the embedding generator in a single <c>GenerateAsync</c> call. A large
    ///     document yields thousands of chunks; batching bounds each round-trip instead of one unbounded call.
    /// </summary>
    public int MaxEmbeddingBatchSize { get; set; } = 64;

    /// <summary>
    ///     Hard upper bound on the number of query embeddings held in the RAM-only query-embedding cache. Bounded so a
    ///     long-lived process cannot grow the cache without limit; keyed by (resolved model, query hash) so a model swap
    ///     never returns a stale cross-model vector. Default 128; a value of 0 or less still clamps to 1.
    /// </summary>
    public int QueryEmbeddingCacheMaxEntries { get; set; } = 128;

    /// <summary>
    ///     Time-to-live (seconds) for a cached query embedding. A repeated query within this window skips the embedding
    ///     round trip (the dominant retrieval latency). Default 300s; 0 disables the cache (every query is re-embedded).
    /// </summary>
    public int QueryEmbeddingCacheTtlSeconds { get; set; } = 300;

    /// <summary>
    ///     Which fusion combines the lexical (BM25) and semantic (cosine) arms on the DEFAULT no-reranker retrieval path.
    ///     <see cref="RankFusionStrategy.Rrf" /> is classic score-agnostic Reciprocal Rank Fusion (rank position only).
    ///     <see cref="RankFusionStrategy.ScoreAware" /> (default) additionally tilts each fused contribution by the arm's
    ///     min-max normalized relevance score, so a marginal rank-1 hit no longer fuses identically to a strong one; it
    ///     degrades to pure RRF whenever an arm carries no usable score spread, so it is never worse than
    ///     <see cref="RankFusionStrategy.Rrf" /> on a failure/degenerate path. Independent of the reranker: when a reranker
    ///     model is configured it still rescores the fused pool afterwards.
    /// </summary>
    public RankFusionStrategy FusionStrategy { get; set; } = RankFusionStrategy.ScoreAware;

    /// <summary>
    ///     Maximum multiplicative score tilt applied under <see cref="RankFusionStrategy.ScoreAware" />: an arm's
    ///     top-normalized entry has its <c>1/(k+rank)</c> contribution scaled by <c>1 + FusionScoreWeight</c>, its weakest
    ///     by <c>1</c> (unchanged). <c>0</c> reduces score-aware fusion to pure RRF. Default <c>1.0</c>. Clamped to be
    ///     non-negative.
    /// </summary>
    public double FusionScoreWeight { get; set; } = 1.0;

    /// <summary>Upper bound on the plaintext length of a single chunk (characters), before overlap.</summary>
    public int MaxChunkChars { get; set; } = 2000;

    /// <summary>
    ///     Number of trailing characters carried from the end of one chunk into the start of the next, so a fact split
    ///     across a chunk boundary stays retrievable. Must be smaller than <see cref="MaxChunkChars" />.
    /// </summary>
    public int ChunkOverlapChars { get; set; } = 200;
}
