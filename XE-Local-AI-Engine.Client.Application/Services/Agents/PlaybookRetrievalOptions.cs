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

    /// <summary>Provider key for the embedding model; must match a registered node-local provider (default "llamacpp").</summary>
    public string EmbeddingProviderName { get; set; } = "llamacpp";

    /// <summary>Upper bound on the in-memory candidate-embedding cache (RAM-only, never persisted). Floored at 1.</summary>
    public int EmbeddingCacheMaxEntries { get; set; } = 512;

    /// <summary>
    ///     Soft token budget for the memory injected into the resolved system prompt per send (adaptive memory). After
    ///     the top-K selection, the lowest-ranked actions are trimmed until the estimated injected token count is at or
    ///     below this budget. <c>0</c> means unbounded (the legacy pre-budget behavior). The trim engages only on the
    ///     retrieval path (above threshold + non-blank query); the static-prepend fast path is left byte-identical, so a
    ///     no-memory or at/below-threshold resolve is unaffected. The estimate is a deterministic char-based heuristic
    ///     (see <c>PlaybookRetrievalSelector</c>), not an exact tokenizer — this is a soft guard against prompt bloat,
    ///     not a correctness property.
    /// </summary>
    public int MaxInjectedMemoryTokens { get; set; } = 2000;

    /// <summary>
    ///     Soft sub-budget (in the same estimated tokens) reserved within <see cref="MaxInjectedMemoryTokens" /> for
    ///     Failure-scope ("what NOT to do") memory, so negative guidance cannot crowd out positive procedural guidance.
    ///     Failure-scope items are trimmed to this sub-budget first (lowest-ranked dropped first), then the surviving full
    ///     set is trimmed to <see cref="MaxInjectedMemoryTokens" />. Default ~30% of the total. <c>0</c> means no separate
    ///     Failure cap (Failure competes for the shared budget on equal footing). Like the total budget this is a soft
    ///     guard, not a correctness property, and only engages on the retrieval path.
    /// </summary>
    public int MaxInjectedFailureMemoryTokens { get; set; } = 600;
}
