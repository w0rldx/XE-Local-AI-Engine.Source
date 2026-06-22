namespace XE_Local_AI_Engine.Client.Services.Memory;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     The adaptive-memory extraction agent (the AI surface). Reads a completed run and proposes structured candidate
///     memories, each carrying its scope, advisory trigger condition, and confidence. The agent only PROPOSES — it
///     persists nothing and decides nothing; the service gates temp chats, dedupes, and writes <c>Suggested</c> actions
///     for human review. Implementations run a node-local model (never the cloud-capable shared chat client) so
///     conversation content never leaves the node (the privacy invariant). The seam keeps the model off the hot send
///     path and lets tests substitute a deterministic fake (no Ollama in CI).
/// </summary>
public interface IMemoryExtractionAgent
{
    /// <summary>
    ///     Proposes candidate memories distilled from <paramref name="run" />. A failed run makes
    ///     <see cref="MemoryScope.Failure" /> eligible. May return an empty list (no distillable lesson). Returns empty
    ///     cleanly when no node-local extraction model is configured.
    /// </summary>
    Task<IReadOnlyList<ProposedMemory>> ProposeAsync(MemoryExtractionRunInput run, CancellationToken cancellationToken = default);
}

/// <summary>
///     A single proposed memory from the extraction agent — structured (scope + trigger + behavior + confidence) so it
///     can be deduped, measured, and shown with provenance once persisted as a <c>Suggested</c>/<c>Extracted</c> action.
/// </summary>
public sealed record ProposedMemory(
    string Behavior,
    MemoryScope Scope,
    string? TriggerCondition,
    double Confidence);
