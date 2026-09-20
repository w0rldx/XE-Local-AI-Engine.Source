namespace XE_Local_AI_Engine.Client.Services.Memory;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Post-run adaptive-memory extraction orchestration: it mines candidate lessons from a completed or failed run
///     and persists the survivors as <c>Suggested</c>/<c>Extracted</c> playbook actions for human review.
/// </summary>
/// <remarks>
///     It uses a <b>node-local</b> model only, never the shared cloud-capable client, gates out temporary
///     conversations BEFORE any model call and drops near-duplicates of existing memories. Extracted candidates are
///     inert by construction, since the resolver injects only <c>Enabled</c> actions and the eval gate plus human
///     approval still govern promotion: the agent proposes, the system decides. It is dispatched off the chat hot
///     path, so its model call never delays the terminal SSE event or fails the user's turn.
/// </remarks>
public interface IMemoryExtractionService
{
    /// <summary>
    ///     Extracts and persists candidate memories for <paramref name="run" />, returning whether a gate
    ///     short-circuited plus what was proposed, kept and deduplicated.
    /// </summary>
    /// <remarks>
    ///     A temporary conversation, a missing extraction model and a run with no distillable lesson all return
    ///     cleanly, with nothing persisted and no throw.
    /// </remarks>
    Task<MemoryExtractionOutcome> ExtractAsync(MemoryExtractionRunInput run, CancellationToken cancellationToken = default);
}

/// <summary>
///     The completed-run inputs the extraction service mines: the user turns, the assistant's answer, the failure
///     signal, the link ids and the temp-chat flag.
/// </summary>
/// <remarks>
///     The failure signal is a Failed terminal status plus the sanitized <see cref="Error" /> string, because no live
///     <c>Exception</c> exists at the primary seam — enough to flag <see cref="MemoryScope.Failure" /> eligibility.
///     Message content is held in memory only for the model call and the dedup compare; it is NEVER written to the
///     execution log.
/// </remarks>
public sealed record MemoryExtractionRunInput
{
    public required Guid AgentDefinitionId { get; init; }

    public required Guid ConversationId { get; init; }

    public required Guid AssistantMessageId { get; init; }

    public required IReadOnlyList<MemoryExtractionTurn> UserTurns { get; init; }

    public required string AssistantResponse { get; init; }

    public required bool Failed { get; init; }

    public required string? Error { get; init; }

    public required bool MemoryExcluded { get; init; }
}

/// <summary>A single user turn handed to the extraction model (role is implied — these are the user side only).</summary>
public sealed class MemoryExtractionTurn
{
    public required string Content { get; init; }
}

/// <summary>The result of an extraction run. Counts let callers/tests see what was proposed vs kept vs filtered.</summary>
public sealed class MemoryExtractionOutcome
{
    public required bool MemoryExcluded { get; init; }

    public required bool ModelConfigured { get; init; }

    public required IReadOnlyList<PlaybookActionRecord> CreatedCandidates { get; init; }

    public required int ProposedCount { get; init; }

    public required int DuplicateCount { get; init; }

    /// <summary>The short-circuit result for a temporary (memory-excluded) conversation: nothing proposed, nothing kept.</summary>
    public static MemoryExtractionOutcome SuppressedByTempChat()
    {
        return new MemoryExtractionOutcome { MemoryExcluded = true, ModelConfigured = false, CreatedCandidates = [], ProposedCount = 0, DuplicateCount = 0 };
    }

    /// <summary>The short-circuit result when no node-local extraction model is configured (the CI-safe disabled gate).</summary>
    public static MemoryExtractionOutcome NoModelConfigured()
    {
        return new MemoryExtractionOutcome { MemoryExcluded = false, ModelConfigured = false, CreatedCandidates = [], ProposedCount = 0, DuplicateCount = 0 };
    }
}
