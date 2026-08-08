namespace XE_Local_AI_Engine.Client.Services.Memory;

/// <summary>
///     Options for the adaptive-memory extraction service. <see cref="ExtractionModelName" /> names the node-local model
///     used to mine lessons from a completed run; it is defaulted in composition to the node's configured chat model so
///     extraction never silently picks a cloud model. When it resolves to empty (no node-local model configured),
///     extraction is a clean no-op — the disabled gate that keeps CI deterministic without Ollama, mirroring the
///     embedding-ranker disabled gate. <see cref="MaxCandidates" /> caps how many memories a single run may propose.
/// </summary>
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
    ///     Bound on how many pending extraction jobs the queue holds. Each job carries conversation content, so an
    ///     unbounded backlog would retain that content in memory indefinitely; a full queue drops the newest job (logged,
    ///     text-free) rather than blocking the chat pump or growing without limit. Clamped to at least 1.
    /// </summary>
    public int QueueCapacity { get; set; } = 128;

    /// <summary>
    ///     Bounded window the worker waits for in-flight extractions to finish at shutdown before abandoning them. Clamped
    ///     to at least 1 second. Mirrors the knowledge-ingestion worker's drain.
    /// </summary>
    public int ShutdownDrainTimeoutSeconds { get; set; } = 10;

    /// <summary>
    ///     Master switch for the SEMANTIC (embedding-cosine) dedup layer that runs ON TOP OF the always-on lexical dedup.
    ///     When on, a candidate that survives lexical dedup is embedded with the node-local embedding model and dropped if
    ///     it is cosine-near an existing live memory (a paraphrase the lexical key misses). Off (or with no confident
    ///     node-local embedding model available) leaves the lexical-only behaviour byte-for-byte. Defaults on; the
    ///     IsConfident gate already makes it a clean no-op on a node with no embedding model, so this flag exists so an
    ///     operator can disable semantic dedup WITHOUT unconfiguring the embedding model that retrieval also shares.
    /// </summary>
    public bool SemanticDedupEnabled { get; set; } = true;

    /// <summary>
    ///     Provider key for the node-local embedding model used by semantic dedup; must match a registered node-local
    ///     provider (default "llamacpp", mirroring the playbook-retrieval ranker and knowledge-base defaults). Blank
    ///     disables semantic dedup (lexical-only). The ACTUAL embedding model name is resolved on this provider via the
    ///     shared <c>IEmbeddingModelResolver</c>, so the dedup and retrieval/knowledge lanes agree on one installed model.
    /// </summary>
    public string SemanticDedupEmbeddingProviderName { get; set; } = "llamacpp";

    /// <summary>
    ///     Cosine-similarity threshold at/above which an extracted candidate is treated as a semantic duplicate of an
    ///     existing live memory (same scope) and dropped. Tuned conservatively (default 0.92) so ONLY true near-duplicates
    ///     collapse — a too-low threshold would swallow distinct lessons. Clamped to the open-closed interval (0, 1]; a
    ///     non-positive or &gt;1 value resets to the default.
    /// </summary>
    public double SemanticDedupSimilarityThreshold { get; set; } = 0.92d;

    /// <summary>
    ///     Upper bound on the RAM-only, never-persisted cache of existing-memory embeddings (keyed by id+version+resolved
    ///     model, so an edited or model-swapped memory re-embeds automatically). Candidates are always re-embedded per run
    ///     and never cached. Floored at 1. Mirrors the playbook ranker's embedding-cache bound.
    /// </summary>
    public int SemanticDedupEmbeddingCacheMaxEntries { get; set; } = 512;
}
