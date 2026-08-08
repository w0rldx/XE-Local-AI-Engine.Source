namespace XE_Local_AI_Engine.Client.Services.Memory;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Post-run adaptive-memory extraction orchestration: given a completed (or failed) agent run, mine candidate
///     lessons with a <b>node-local</b> model only (never the cloud-capable shared chat client — the privacy invariant),
///     gate out temporary conversations BEFORE any model call, drop near-duplicates of existing memories, and persist
///     the survivors as <c>Suggested</c>/<c>Extracted</c> playbook actions for human review. Extracted candidates are
///     inert by construction (the resolver injects only <c>Enabled</c> actions; the eval gate + human approval still
///     govern promotion). The agent proposes; the system decides.
/// </summary>
/// <remarks>
///     This service is dispatched off the chat hot path (see <see cref="IMemoryExtractionDispatcher" />) so its model
///     call never delays the terminal SSE event or fails the user's turn. It is the extraction counterpart to the
///     privacy-correct analysis path (<c>IPlaybookAnalysisService</c>).
/// </remarks>
public interface IMemoryExtractionService
{
    /// <summary>
    ///     Extracts and persists candidate memories for the run described by <paramref name="run" />. Returns the
    ///     outcome (whether the temp-chat gate or the disabled gate short-circuited, plus what was proposed vs kept vs
    ///     deduplicated). A temporary conversation, a missing extraction model, or a run with no distillable lesson all
    ///     return cleanly with nothing persisted and no throw.
    /// </summary>
    Task<MemoryExtractionOutcome> ExtractAsync(MemoryExtractionRunInput run, CancellationToken cancellationToken = default);
}

/// <summary>
///     The completed-run inputs the extraction service mines. Carries the user turns + the assistant's answer text + the
///     failure signal (a Failed terminal status plus the sanitized <see cref="Error" /> string — there is no live
///     <c>Exception</c> object at the primary seam, only the status + sanitized string; that is enough to flag
///     <see cref="MemoryScope.Failure" /> eligibility), the link ids, and the temp-chat flag. Message content is held
///     only in memory for the model call and the dedup compare — it is NEVER written to the execution log.
/// </summary>
public sealed record MemoryExtractionRunInput(
    Guid AgentDefinitionId,
    Guid ConversationId,
    Guid AssistantMessageId,
    IReadOnlyList<MemoryExtractionTurn> UserTurns,
    string AssistantResponse,
    bool Failed,
    string? Error,
    bool MemoryExcluded);

/// <summary>A single user turn handed to the extraction model (role is implied — these are the user side only).</summary>
public sealed record MemoryExtractionTurn(string Content);

/// <summary>The result of an extraction run. Counts let callers/tests see what was proposed vs kept vs filtered.</summary>
public sealed record MemoryExtractionOutcome(
    bool MemoryExcluded,
    bool ModelConfigured,
    IReadOnlyList<PlaybookActionRecord> CreatedCandidates,
    int ProposedCount,
    int DuplicateCount)
{
    /// <summary>The short-circuit result for a temporary (memory-excluded) conversation: nothing proposed, nothing kept.</summary>
    public static MemoryExtractionOutcome SuppressedByTempChat()
    {
        return new MemoryExtractionOutcome(MemoryExcluded: true, ModelConfigured: false, [], ProposedCount: 0, DuplicateCount: 0);
    }

    /// <summary>The short-circuit result when no node-local extraction model is configured (the CI-safe disabled gate).</summary>
    public static MemoryExtractionOutcome NoModelConfigured()
    {
        return new MemoryExtractionOutcome(MemoryExcluded: false, ModelConfigured: false, [], ProposedCount: 0, DuplicateCount: 0);
    }
}
