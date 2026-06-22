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
}
