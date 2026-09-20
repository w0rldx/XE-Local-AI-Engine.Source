namespace XE_Local_AI_Engine.Client.Services.Memory;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     The adaptive-memory extraction agent, the AI surface: it reads a completed run and proposes structured
///     candidate memories, each carrying its scope, advisory trigger condition and confidence.
/// </summary>
/// <remarks>
///     The agent only PROPOSES; it persists and decides nothing, while the service gates temp chats, dedupes and
///     writes <c>Suggested</c> actions for human review. Implementations run a node-local model, never the shared
///     cloud-capable client, so conversation content never leaves the node. The seam keeps the model off the hot send
///     path and lets tests substitute a deterministic fake, so CI needs no Ollama.
/// </remarks>
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
public sealed class ProposedMemory
{
    public required string Behavior { get; init; }

    public required MemoryScope Scope { get; init; }

    public required string? TriggerCondition { get; init; }

    public required double Confidence { get; init; }
}
