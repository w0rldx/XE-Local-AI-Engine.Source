namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>
///     Represents invocation state.
/// </summary>
public sealed class InvocationState
{
    // Streamed content/reasoning accumulate one chunk at a time on the hot streaming path. Each channel is the immutable,
    // append-only StreamingText below: appending is O(chunk) and, crucially, a snapshot CLONE copies the reference (O(1))
    // instead of materializing the full string. The whole string is built (and cached) only when a consumer reads
    // StreamedContent/StreamedThinkingContent — the pump's debounced flush, a resume replay, or the terminal flush — so
    // materialization happens at bounded cadence rather than the per-chunk O(n) ToString (O(n^2) over a turn) it replaced.

    public Guid InvocationId { get; init; }

    public Guid ConversationId { get; init; }

    /// <summary>
    ///     The W3C trace id of the ambient activity when the invocation was created, or null when no activity was in
    ///     scope (legacy/platform paths). Surfaced in the invocation monitor so a failed run's "See local logs" row
    ///     carries a copyable correlation id into the exported traces. Not a hot-path value — captured once at creation.
    ///     Like every other field, BOTH hand-rolled clones (WorkerEventDispatcher.Clone AND InvocationResumeRegistry.Clone)
    ///     must copy it.
    /// </summary>
    public string? TraceId { get; init; }

    public InvocationStatus Status { get; set; }

    /// <summary>
    ///     The current runtime phase of the turn — preparing the runtime, loading the model (the cold-start window
    ///     BEFORE the stream-idle watchdog is armed), or generating. Null for turns that never reported a phase
    ///     (platform/legacy paths). Surfaced so the UI can show "loading model…" rather than an apparent hang while a
    ///     large local model warms.
    /// </summary>
    public InvocationRuntimePhase? RuntimePhase { get; set; }

    /// <summary>
    ///     The immutable streamed-content accumulator — the single source of truth for the response text. Cloning an
    ///     <see cref="InvocationState" /> copies THIS reference (O(1)) rather than the materialized
    ///     <see cref="StreamedContent" /> string, which is what removes the per-chunk materialization from the hot path.
    ///     BOTH hand-rolled clones (WorkerEventDispatcher.Clone AND InvocationResumeRegistry.Clone) must copy this member,
    ///     not <see cref="StreamedContent" />.
    /// </summary>
    internal StreamingText ContentAccumulator { get; set; } = StreamingText.Empty;

    public string StreamedContent
    {
        get => ContentAccumulator.Value;
        set => ContentAccumulator = StreamingText.FromString(value ?? string.Empty);
    }

    public int StreamedChunkCount { get; set; }

    /// <summary>
    ///     The immutable streamed-reasoning accumulator. Cloned by reference exactly like <see cref="ContentAccumulator" />;
    ///     both hand-rolled clones must copy this member, not <see cref="StreamedThinkingContent" />.
    /// </summary>
    internal StreamingText ThinkingAccumulator { get; set; } = StreamingText.Empty;

    public string StreamedThinkingContent
    {
        get => ThinkingAccumulator.Value;
        set => ThinkingAccumulator = StreamingText.FromString(value ?? string.Empty);
    }

    public int StreamedThinkingChunkCount { get; set; }

    /// <summary>Appends a streamed content chunk without materializing the accumulated string.</summary>
    public void AppendStreamedContent(string chunk)
    {
        ContentAccumulator = ContentAccumulator.Append(chunk);
    }

    /// <summary>Appends a streamed reasoning chunk without materializing the accumulated string.</summary>
    public void AppendStreamedThinkingContent(string chunk)
    {
        ThinkingAccumulator = ThinkingAccumulator.Append(chunk);
    }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset LastUpdatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? Error { get; set; }

    public FailureCategory? FailureCategory { get; set; }

    public string? ModelUsed { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public int? TotalTokens { get; set; }

    public int? ReasoningTokens { get; set; }

    /// <summary>
    ///     Wall-clock generation duration in milliseconds, measured by the invocation runner across the whole turn
    ///     (prompt-eval through final token). Null until the invocation completes and for legacy/platform turns that
    ///     did not report it. Drives the optional tokens-per-second attribution.
    /// </summary>
    public long? GenerationDurationMs { get; set; }

    public InvocationApprovalState? PendingApproval { get; set; }

    public InvocationApprovalResolutionState? LastApprovalResolution { get; set; }

    public IReadOnlyList<InvocationToolCallState> PendingToolCalls { get; set; } = [];

    public InvocationToolCallResultState? LastToolCallResult { get; set; }
}
