namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Node-local knowledge-base ingestion and embedding options, bound from the <c>KnowledgeBase</c> configuration
///     section.
/// </summary>
/// <remarks>
///     Defaults target the shipped node-local embedding model (<c>nomic-embed-text</c> via the llama.cpp provider) and a
///     conservative single-document-at-a-time ingestion budget, so a batch upload cannot exhaust CPU, RAM or VRAM. The
///     search path reuses <see cref="EmbeddingModelName" /> and <see cref="EmbeddingProviderName" /> to build the query
///     vector and to filter same-model chunk vectors.
/// </remarks>
public sealed class KnowledgeBaseOptions
{
    public const string Section = "KnowledgeBase";

    /// <summary>Node-local embedding model that builds chunk vectors and the search query vector.</summary>
    public string EmbeddingModelName { get; set; } = "nomic-embed-text";

    /// <summary>
    ///     Provider key for the embedding model; must match a registered node-local provider, default <c>llamacpp</c>.
    /// </summary>
    /// <remarks>
    ///     The default keeps embedding on-device: chunk text at ingestion and query text at search go only to the local
    ///     llama.cpp process and never leave the node. Pointing this at a cloud embedding provider sends that same chunk
    ///     and query text off-node to a third party — a privacy tradeoff the operator explicitly accepts by changing
    ///     this value. Leave it on the local provider to keep knowledge-base content private to the machine.
    /// </remarks>
    public string EmbeddingProviderName { get; set; } = "llamacpp";

    /// <summary>Post-provider vector policy for the knowledge index.</summary>
    /// <remarks>
    ///     <see cref="KnowledgeEmbeddingVectorMode.Matryoshka512" /> (default) applies the versioned Nomic v1.5
    ///     Matryoshka transform only when the resolver confidently identifies <c>nomic-embed-text-v1.5</c>; every other
    ///     model remains at its native width. Set <see cref="KnowledgeEmbeddingVectorMode.Native" /> for operational
    ///     rollback, then fully reindex the corpus so every stored vector receives the corresponding native identity and
    ///     width.
    /// </remarks>
    public KnowledgeEmbeddingVectorMode EmbeddingVectorMode { get; set; } = KnowledgeEmbeddingVectorMode.Matryoshka512;

    /// <summary>
    ///     Node-local cross-encoder reranker model that rescores the fused candidate pool at search time.
    /// </summary>
    /// <remarks>
    ///     Empty (default) turns reranking OFF and the search returns the Reciprocal-Rank-Fusion order unchanged. An installed
    ///     reranker name (for example <c>bge-reranker-v2-m3</c>) makes the search hydrate the fused pool, score each candidate
    ///     against the query on the local rerank-role llama-server (<c>/v1/rerank</c>), and reorder by descending relevance
    ///     before the top-k cut; like the embedding model, this keeps retrieval on-device. A missing model or unavailable
    ///     runtime degrades silently to fusion order. Seeded from the node settings store: stored value, config value, off.
    /// </remarks>
    public string RerankerModelName { get; set; } = string.Empty;

    /// <summary>When true, a configured reranker runs only for ambiguous candidate sets.</summary>
    /// <remarks>
    ///     Agreement between the lexical and dense top hit is treated as high confidence, and optional reranking is
    ///     skipped once 80% of the retrieval latency budget has already elapsed. Disable to force reranking for
    ///     controlled benchmarks while time remains; the hard remaining per-search deadline still applies.
    /// </remarks>
    public bool AdaptiveRerankingEnabled { get; set; } = true;

    /// <summary>Soft end-to-end retrieval target used to skip optional stages; defaults to 500 ms.</summary>
    public int RetrievalLatencyBudgetMilliseconds { get; set; } = 500;

    /// <summary>Periodically enqueue stale-vector documents after a scheduled embedding-model change.</summary>
    public bool ScheduledModelReindexEnabled { get; set; } = true;

    /// <summary>Polling interval for stale-vector discovery. Clamped to at least one minute.</summary>
    public int ScheduledModelReindexIntervalMinutes { get; set; } = 60;

    /// <summary>
    ///     Whether the read-only knowledge-base agent tools (<c>search_knowledge_base</c>, <c>read_document</c>,
    ///     <c>read_surrounding_chunks</c>) are offered to agents and executed.
    /// </summary>
    /// <remarks>
    ///     Default <see langword="true" />. Set to <see langword="false" /> to turn the tools off node-wide, in which
    ///     case each handler returns a short "tools are disabled" message instead of running a retrieval.
    /// </remarks>
    public bool AgentToolsEnabled { get; set; } = true;

    /// <summary>
    ///     The single opt-in governing whether a CLOUD-hosted model may receive ANY node-local private data.
    /// </summary>
    /// <remarks>
    ///     Covers the read-only knowledge-base tools, the coder workspace file tools (<c>list_files</c>, <c>read_file</c>,
    ///     <c>search_text</c>) and conversation attachments, inlined or staged. Default <see langword="false" />: all of it is
    ///     offered only for a node-local effective model; for a cloud effective model the tools are withheld and attachments
    ///     are neither staged nor inlined, with a visible turn notice. The gate keys on the EFFECTIVE model after any
    ///     agent/profile pin. Scope: <c>docs/wiki/15-knowledge-base.md</c>. Independent of <see cref="AgentToolsEnabled" />.
    /// </remarks>
    public bool AllowCloudModelAccess { get; set; }

    /// <summary>
    ///     Maximum number of documents ingested concurrently by the background worker. Bounded (default 1) so N uploads
    ///     do not spin up N unbounded embedding pipelines and contend for the provider's loaded-process budget.
    /// </summary>
    public int MaxConcurrentIngestions { get; set; } = 1;

    /// <summary>
    ///     Maximum time, in seconds, the background worker waits at host shutdown for the documents it is currently
    ///     ingesting to reach a terminal state before abandoning them.
    /// </summary>
    /// <remarks>
    ///     During the window each in-flight document runs uncancelled, so a near-complete index write still lands; once
    ///     it elapses the shared drain token is cancelled so a hung document cannot block shutdown, and any unfinished
    ///     document is left non-terminal and re-queued on the next start. Default 30 s, clamped to at least 1 s.
    /// </remarks>
    public int ShutdownDrainTimeoutSeconds { get; set; } = 30;

    /// <summary>
    ///     Maximum number of chunk texts sent to the embedding generator in a single <c>GenerateAsync</c> call. A large
    ///     document yields thousands of chunks; batching bounds each round-trip instead of one unbounded call.
    /// </summary>
    public int MaxEmbeddingBatchSize { get; set; } = 64;

    /// <summary>
    ///     Hard upper bound on the number of query embeddings held in the RAM-only query-embedding cache.
    /// </summary>
    /// <remarks>
    ///     Bounded so a long-lived process cannot grow the cache without limit. Keyed by model/policy family plus query
    ///     hash, with the exact canonical vector identity and width validated from each entry, so a model,
    ///     transform-policy, or width change never returns a stale vector. Default 128; a value of 0 or less clamps to 1.
    /// </remarks>
    public int QueryEmbeddingCacheMaxEntries { get; set; } = 128;

    /// <summary>
    ///     Time-to-live (seconds) for a cached query embedding. A repeated query within this window skips the embedding
    ///     round trip (the dominant retrieval latency). Default 300s; 0 disables the cache (every query is re-embedded).
    /// </summary>
    public int QueryEmbeddingCacheTtlSeconds { get; set; } = 300;

    /// <summary>
    ///     Maximum number of content-addressed document-chunk embeddings retained in the process-local reuse layer.
    /// </summary>
    /// <remarks>
    ///     The durable layer reads already-committed vectors from the knowledge index, so this bound applies only to the
    ///     hot RAM working set. Default 4096; values below one clamp to one.
    /// </remarks>
    public int ChunkEmbeddingCacheMaxEntries { get; set; } = 4096;

    /// <summary>
    ///     Approximate RAM ceiling, in MiB, for the process-local chunk-embedding reuse layer. Applied alongside
    ///     <see cref="ChunkEmbeddingCacheMaxEntries" /> so a wider embedding model cannot silently multiply memory use.
    ///     Default 64 MiB; values below one clamp to one.
    /// </summary>
    public int ChunkEmbeddingCacheMaxMegabytes { get; set; } = 64;

    /// <summary>
    ///     Time-to-live, in seconds, for both the RAM working set and eligibility of already-committed vectors used as a
    ///     durable cache. Zero disables chunk-embedding reuse. Default seven days.
    /// </summary>
    public int ChunkEmbeddingCacheTtlSeconds { get; set; } = 7 * 24 * 60 * 60;

    /// <summary>
    ///     Which fusion combines the lexical (BM25) and semantic (cosine) arms on the DEFAULT no-reranker retrieval path.
    /// </summary>
    /// <remarks>
    ///     <see cref="RankFusionStrategy.Rrf" /> is classic score-agnostic Reciprocal Rank Fusion, rank position only.
    ///     <see cref="RankFusionStrategy.ScoreAware" /> (default) additionally tilts each fused contribution by the arm's
    ///     min-max normalized relevance score, so a marginal rank-1 hit no longer fuses identically to a strong one, and
    ///     degrades to pure RRF whenever an arm carries no usable score spread — never worse than
    ///     <see cref="RankFusionStrategy.Rrf" />. Independent of the reranker, which still rescores the fused pool.
    /// </remarks>
    public RankFusionStrategy FusionStrategy { get; set; } = RankFusionStrategy.ScoreAware;

    /// <summary>
    ///     Maximum multiplicative score tilt applied under <see cref="RankFusionStrategy.ScoreAware" />.
    /// </summary>
    /// <remarks>
    ///     An arm's top-normalized entry has its <c>1/(k+rank)</c> contribution scaled by <c>1 + FusionScoreWeight</c>
    ///     and its weakest by <c>1</c>, unchanged. <c>0</c> reduces score-aware fusion to pure RRF. Default <c>1.0</c>,
    ///     clamped non-negative.
    /// </remarks>
    public double FusionScoreWeight { get; set; } = 1.0;

    /// <summary>Upper bound on the plaintext length of a single chunk (characters), before overlap.</summary>
    public int MaxChunkChars { get; set; } = 2000;

    /// <summary>
    ///     Upper bound on the estimated TOKEN footprint of a single chunk's embedded, heading-trail-prefixed text — the
    ///     primary size lever, with <see cref="MaxChunkChars" /> kept as a hard character ceiling.
    /// </summary>
    /// <remarks>
    ///     Default 512 fits the shipped <c>nomic-embed-text</c> embedder's 2048-token window with generous margin for the
    ///     heading prefix and the model's own special tokens, and preserves the ~2000-character / ~500-token ASCII chunk
    ///     granularity so existing corpora chunk identically. A discoverable resolved context window overrides it
    ///     downward; a larger one never enlarges chunks past it. Changing it affects only newly ingested or reindexed
    ///     documents. Sizing rules: <c>docs/wiki/15-knowledge-base.md</c> ("Ingestion pipeline").
    /// </remarks>
    public int MaxChunkTokens { get; set; } = 512;

    /// <summary>
    ///     Number of trailing characters carried from the end of one chunk into the start of the next, so a fact split
    ///     across a chunk boundary stays retrievable. Must be smaller than <see cref="MaxChunkChars" />.
    /// </summary>
    public int ChunkOverlapChars { get; set; } = 200;

    /// <summary>Maximum supported files admitted by one repository import.</summary>
    public int MaxRepositoryImportFiles { get; set; } = 50_000;

    /// <summary>Maximum aggregate source bytes admitted by one repository import.</summary>
    public long MaxRepositoryImportBytes { get; set; } = 512L * 1024L * 1024L;

    /// <summary>Hard maximum raw size of one repository file before any managed allocation is made.</summary>
    public long MaxRepositoryImportFileBytes { get; set; } = 16L * 1024L * 1024L;
}

/// <summary>Versioned post-provider vector policy used by both knowledge ingestion and query embedding.</summary>
public enum KnowledgeEmbeddingVectorMode
{
    /// <summary>Use provider-native vectors without a dimensionality transform.</summary>
    Native,

    /// <summary>Apply the Nomic v1.5 layer-normalize, truncate-to-512, then L2-normalize transform when confidently resolved.</summary>
    Matryoshka512
}
