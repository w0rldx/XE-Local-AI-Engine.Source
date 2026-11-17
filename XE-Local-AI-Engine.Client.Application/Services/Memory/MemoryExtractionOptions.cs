namespace XE_Local_AI_Engine.Client.Services.Memory;

/// <summary>
///     Options for the adaptive-memory extraction service, whose <see cref="MaxCandidates" /> caps how many memories
///     a single run may propose.
/// </summary>
/// <remarks>
///     <see cref="ExtractionModelName" /> only seeds the <c>MemoryExtractionModelName</c> node setting, which consumers read; blank
///     inherits the default model, so extraction never silently picks a cloud one. A blank effective name makes extraction a no-op.
/// </remarks>
public sealed class MemoryExtractionOptions
{
    public const string Section = "MemoryExtraction";

    /// <summary>Appsettings seed of the <c>MemoryExtractionModelName</c> node setting.</summary>
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
    /// <remarks>
    ///     Default 1: the chat process has ONE background slot (<c>LlamaServerChatSlots.Background</c>), so a second
    ///     concurrent extraction would only queue behind the first on the server.
    /// </remarks>
    public int MaxConcurrentExtractions { get; set; } = 1;

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
    ///     Master switch, default off, for the SEMANTIC dedup layer that runs ON TOP OF the always-on lexical dedup.
    /// </summary>
    /// <remarks>
    ///     Off because the measured embedders (<c>MemoryDedupCalibrationLiveTests</c>) cannot separate a duplicate lesson
    ///     from a one-word contradiction: near-misses often score at or above the threshold. An operator may enable it;
    ///     a candidate surviving lexical dedup is then embedded and dropped if it is cosine-near a live memory. Off, or
    ///     with no confident node-local embedding model, the lexical-only behavior is byte-for-byte.
    /// </remarks>
    public bool SemanticDedupEnabled { get; set; }

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
    ///     Applies only when <see cref="SemanticDedupEnabled" /> is on. Default 0.92; a too-low threshold swallows
    ///     distinct lessons. It is clamped to (0, 1], and any other value resets to the default.
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
