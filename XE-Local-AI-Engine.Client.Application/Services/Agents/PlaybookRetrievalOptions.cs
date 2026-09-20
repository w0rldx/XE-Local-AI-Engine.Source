namespace XE_Local_AI_Engine.Client.Services.Agents;

/// <summary>
///     Options for the playbook relevance-retrieval path.
/// </summary>
/// <remarks>
///     Above <see cref="RetrievalThreshold" /> Enabled actions, and with a non-blank query on the send, the resolver
///     injects only the top <see cref="TopK" /> most relevant actions instead of the full static prepend. At or below
///     the threshold, or with a blank query, the static prepend is preserved byte-for-byte.
/// </remarks>
public sealed class PlaybookRetrievalOptions
{
    public const string Section = "PlaybookRetrieval";

    /// <summary>Enabled-action count above which relevance retrieval engages (at or below it, static prepend is used).</summary>
    public int RetrievalThreshold { get; set; } = 8;

    /// <summary>Maximum number of actions injected per send once retrieval engages.</summary>
    public int TopK { get; set; } = 8;

    /// <summary>
    ///     Node-local embedding model used to rank candidates by semantic similarity once retrieval engages.
    /// </summary>
    /// <remarks>
    ///     Null or empty, the default, keeps the model-free lexical ranker effective, and any embedding failure lands
    ///     there too, so a send never breaks and CI stays deterministic without a model server.
    /// </remarks>
    public string? EmbeddingModelName { get; set; }

    /// <summary>Provider key for the embedding model; must match a registered node-local provider (default "llamacpp").</summary>
    public string EmbeddingProviderName { get; set; } = "llamacpp";

    /// <summary>Upper bound on the in-memory candidate-embedding cache (RAM-only, never persisted). Floored at 1.</summary>
    public int EmbeddingCacheMaxEntries { get; set; } = 512;

    /// <summary>
    ///     Soft token budget for the memory injected into the resolved system prompt per send.
    /// </summary>
    /// <remarks>
    ///     After the top-K selection the lowest-ranked actions are trimmed until the estimate lands at or below this;
    ///     <c>0</c> is unbounded. The trim engages only on the retrieval path, leaving the static-prepend fast path
    ///     byte-identical. The estimate is a deterministic char-based heuristic (see <c>PlaybookRetrievalSelector</c>),
    ///     not a tokenizer: a soft guard against prompt bloat, not a correctness property.
    /// </remarks>
    public int MaxInjectedMemoryTokens { get; set; } = 2000;

    /// <summary>
    ///     Soft sub-budget within <see cref="MaxInjectedMemoryTokens" /> for Failure-scope memory, so negative
    ///     guidance cannot crowd out positive procedural guidance.
    /// </summary>
    /// <remarks>
    ///     Failure-scope items are trimmed to this sub-budget first, lowest-ranked dropped first, then the surviving
    ///     set is trimmed to the total. <c>0</c> removes the separate cap and lets Failure compete on equal footing.
    ///     Like the total budget it is a soft guard and engages only on the retrieval path.
    /// </remarks>
    public int MaxInjectedFailureMemoryTokens { get; set; } = 600;
}
