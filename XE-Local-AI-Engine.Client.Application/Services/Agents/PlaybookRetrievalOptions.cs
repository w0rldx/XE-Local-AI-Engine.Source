namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Options for the relevance retrieval and cohort monitoring relevance-retrieval path. When an agent has more than
///     <see cref="RetrievalThreshold" /> Enabled actions and the incoming send carries a non-blank query, the resolver
///     injects only the top <see cref="TopK" /> most relevant actions instead of the full static prepend; at or below the
///     threshold (or with a blank query) the pre-retrieval static-prepend behavior is preserved byte-for-byte.
/// </summary>
public sealed class PlaybookRetrievalOptions
{
    public const string Section = "PlaybookRetrieval";

    /// <summary>Enabled-action count above which relevance retrieval engages (at or below it, static prepend is used).</summary>
    public int RetrievalThreshold { get; set; } = 8;

    /// <summary>Maximum number of actions injected per send once retrieval engages.</summary>
    public int TopK { get; set; } = 8;

    /// <summary>
    ///     Node-local embedding model used to rank candidates by semantic similarity once retrieval engages. Null or
    ///     empty (the default) keeps the model-free lexical ranker as the effective ranker; any embedding failure also
    ///     falls back to lexical, so a send never breaks and CI stays deterministic without Ollama.
    /// </summary>
    public string? EmbeddingModelName { get; set; }

    /// <summary>Provider key for the embedding model; must match a registered node-local provider (default "ollama").</summary>
    public string EmbeddingProviderName { get; set; } = "ollama";

    /// <summary>Upper bound on the in-memory candidate-embedding cache (RAM-only, never persisted). Floored at 1.</summary>
    public int EmbeddingCacheMaxEntries { get; set; } = 512;
}
