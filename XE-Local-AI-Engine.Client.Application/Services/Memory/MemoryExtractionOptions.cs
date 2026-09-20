namespace XE_Local_AI_Engine.Client.Services.Memory;

/// <summary>
///     Options for the adaptive-memory extraction service, whose <see cref="MaxCandidates" /> caps how many memories
///     a single run may propose.
/// </summary>
/// <remarks>
///     <see cref="ExtractionModelName" /> names the node-local model that mines lessons from a completed run and is
///     defaulted in composition to the node's configured chat model, so extraction never silently picks a cloud one.
///     An empty value makes extraction a clean no-op, the disabled gate that keeps CI deterministic without Ollama.
/// </remarks>
public sealed class MemoryExtractionOptions
{
    public const string Section = "MemoryExtraction";

    /// <summary>
    ///     The node-local model used for extraction. Defaulted from the node chat model at composition time. Empty
    ///     disables extraction (no model call, no candidate) — the CI-safe gate.
    /// </summary>
    public string ExtractionModelName { get; set; } = string.Empty;

    /// <summary>Upper bound on candidate memories per run (prompt-bloat / review-load / candidate-spam guard).</summary>
    public int MaxCandidates { get; set; } = 3;

    /// <summary>Default injection priority assigned to a newly-extracted (Suggested) action (sorts after manual actions).</summary>
    public int CandidatePriority { get; set; } = 100;

    /// <summary>
    ///     Maximum extraction jobs the background worker runs at once. Extraction makes a node-local model call, so
    ///     unbounded fan-out (one per terminal turn) could spin up many concurrent model round-trips. Clamped to at
    ///     least 1.
    /// </summary>
    public int MaxConcurrentExtractions { get; set; } = 2;

    /// <summary>
    ///     Bound on how many pending extraction jobs the queue holds, clamped to at least 1.
    /// </summary>
    /// <remarks>
    ///     Each job carries conversation content, so an unbounded backlog would retain it in memory indefinitely. A
    ///     full queue drops the newest job, logged text-free, rather than blocking the chat pump.
    /// </remarks>
    public int QueueCapacity { get; set; } = 128;

    /// <summary>
    ///     Bounded window the worker waits for in-flight extractions to finish at shutdown before abandoning them. Clamped
    ///     to at least 1 second. Mirrors the knowledge-ingestion worker's drain.
    /// </summary>
    public int ShutdownDrainTimeoutSeconds { get; set; } = 10;

    /// <summary>
    ///     Master switch, default on, for the SEMANTIC dedup layer that runs ON TOP OF the always-on lexical dedup.
    /// </summary>
    /// <remarks>
    ///     When on, a candidate surviving lexical dedup is embedded and dropped if it is cosine-near a live memory;
    ///     off, or with no confident node-local embedding model, the lexical-only behaviour is byte-for-byte. The
    ///     IsConfident gate already no-ops on a node with no embedding model, so this flag exists so an operator can
    ///     disable semantic dedup WITHOUT unconfiguring the embedding model retrieval also shares.
    /// </remarks>
    public bool SemanticDedupEnabled { get; set; } = true;

    /// <summary>
    ///     Provider key for the node-local embedding model semantic dedup uses, which must match a registered
    ///     node-local provider; blank disables semantic dedup.
    /// </summary>
    /// <remarks>
    ///     The ACTUAL model name resolves on this provider through the shared <c>IEmbeddingModelResolver</c>, so the
    ///     dedup, retrieval and knowledge lanes agree on one installed model.
    /// </remarks>
    public string SemanticDedupEmbeddingProviderName { get; set; } = "llamacpp";

    /// <summary>
    ///     Cosine-similarity threshold at or above which a candidate counts as a semantic duplicate of a live memory
    ///     of the same scope and is dropped.
    /// </summary>
    /// <remarks>
    ///     It is tuned conservatively, default 0.92, so ONLY true near-duplicates collapse: a too-low threshold
    ///     swallows distinct lessons. It is clamped to (0, 1], and any other value resets to the default.
    /// </remarks>
    public double SemanticDedupSimilarityThreshold { get; set; } = 0.92d;

    /// <summary>
    ///     Upper bound, floored at 1, on the RAM-only never-persisted cache of existing-memory embeddings.
    /// </summary>
    /// <remarks>
    ///     It is keyed by id, version and resolved model, so an edited or model-swapped memory re-embeds
    ///     automatically, and candidates are always re-embedded per run and never cached.
    /// </remarks>
    public int SemanticDedupEmbeddingCacheMaxEntries { get; set; } = 512;
}
