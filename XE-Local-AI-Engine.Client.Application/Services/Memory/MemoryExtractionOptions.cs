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
}
