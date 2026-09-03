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
    ///     Like every other field, <see cref="Clone" /> must copy it.
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
    ///     <see cref="Clone" /> must copy this member, not <see cref="StreamedContent" />.
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
    ///     <see cref="Clone" /> must copy this member, not <see cref="StreamedThinkingContent" />.
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
    ///     Estimated tool-schema tokens the turn spent across all its provider rounds, read from the provider-call
    ///     budget just before the terminal report. A count, never a tool name. Null on a turn whose runner never
    ///     reported one (the platform path, and any stream that ended without a terminal state).
    /// </summary>
    public long? ToolSchemaTokens { get; set; }

    /// <summary>The largest single round's estimated tool-schema token count for the turn; null for the same reasons as <see cref="ToolSchemaTokens" />.</summary>
    public int? MaxToolSchemaTokens { get; set; }

    /// <summary>
    ///     Why the model stopped generating, verbatim from <c>ChatFinishReason.Value</c> on the last streamed update
    ///     that carried one (<c>stop</c>, <c>length</c>, <c>tool_calls</c>, <c>content_filter</c>, or a provider's own
    ///     token). Null when the provider reported none — every non-OpenAI-shaped path, the orchestration path, and any
    ///     turn that ended before a terminal update arrived. It describes the GENERATION, not the invocation: a turn
    ///     that hit the token budget is still <see cref="InvocationStatus.Completed" />, and callers that care about a
    ///     truncated answer must read THIS rather than the status.
    /// </summary>
    public string? FinishReason { get; set; }

    /// <summary>
    ///     Wall-clock generation duration in milliseconds, measured by the invocation runner across the whole turn
    ///     (prompt-eval through final token). Null until the invocation completes and for legacy/platform turns that
    ///     did not report it. Drives the optional tokens-per-second attribution.
    /// </summary>
    public long? GenerationDurationMs { get; set; }

    /// <summary>
    ///     The turn's separated throughput facts — time to first token, and the pp/tg split of tokens and milliseconds.
    ///     Null for every turn whose provider reported no <c>timings</c> (all cloud providers, the orchestration path,
    ///     and any turn that ended before a terminal update arrived). Never a ranking input: it describes how fast the
    ///     answer arrived, not how good it is.
    /// </summary>
    public InvocationThroughput? Throughput { get; set; }

    public InvocationApprovalState? PendingApproval { get; set; }

    /// <summary>
    ///     The <c>ask_user</c> question currently waiting on the operator, or null. Carries the questions themselves (not
    ///     just a correlation id) so a reconnecting browser can be handed a still-answerable prompt.
    ///     <para>
    ///         WARNING: the CLONE produced by <see cref="Clone" /> — not the live mutated state — is what reaches the
    ///         chat pump and persistence. Any field added here must also be added to <see cref="Clone" /> or it silently
    ///         travels as null. This class of bug passes unit tests and only shows up live.
    ///     </para>
    /// </summary>
    public InvocationUserQuestionState? PendingQuestion { get; set; }

    public InvocationApprovalResolutionState? LastApprovalResolution { get; set; }

    public IReadOnlyList<InvocationToolCallState> PendingToolCalls { get; set; } = [];

    public InvocationToolCallResultState? LastToolCallResult { get; set; }

    /// <summary>
    ///     Snapshot-clones this invocation state. The single clone routine both <c>WorkerEventDispatcher</c> and
    ///     <c>InvocationResumeRegistry</c> call — previously each hand-rolled its own copy of this method, and a field
    ///     added to one but not the other would silently travel as null on whichever path was missed (see
    ///     <see cref="PendingQuestion" />). <see cref="ContentAccumulator" />, <see cref="ThinkingAccumulator" /> and
    ///     <see cref="PendingToolCalls" /> are copied by REFERENCE (O(1)): the accumulators are immutable
    ///     append-only buffers, and every writer replaces <see cref="PendingToolCalls" /> wholesale rather than
    ///     mutating it in place, so a snapshot never observes a torn read.
    /// </summary>
    public InvocationState Clone()
    {
        return new InvocationState
        {
            InvocationId = InvocationId,
            ConversationId = ConversationId,
            TraceId = TraceId,
            Status = Status,
            RuntimePhase = RuntimePhase,
            ContentAccumulator = ContentAccumulator,
            StreamedChunkCount = StreamedChunkCount,
            ThinkingAccumulator = ThinkingAccumulator,
            StreamedThinkingChunkCount = StreamedThinkingChunkCount,
            StartedAt = StartedAt,
            LastUpdatedAt = LastUpdatedAt,
            CompletedAt = CompletedAt,
            Error = Error,
            FailureCategory = FailureCategory,
            ModelUsed = ModelUsed,
            InputTokens = InputTokens,
            OutputTokens = OutputTokens,
            TotalTokens = TotalTokens,
            ReasoningTokens = ReasoningTokens,
            ToolSchemaTokens = ToolSchemaTokens,
            MaxToolSchemaTokens = MaxToolSchemaTokens,
            GenerationDurationMs = GenerationDurationMs,
            FinishReason = FinishReason,
            Throughput = Throughput,
            PendingApproval = PendingApproval,
            PendingQuestion = PendingQuestion,
            LastApprovalResolution = LastApprovalResolution,
            PendingToolCalls = PendingToolCalls,
            LastToolCallResult = LastToolCallResult
        };
    }
}
