namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.Client.Models.Enums;

/// <summary>
///     Represents invocation state.
/// </summary>
public sealed class InvocationState
{
    public Guid InvocationId { get; init; }

    public Guid ConversationId { get; init; }

    public InvocationStatus Status { get; set; }

    public string StreamedContent { get; set; } = string.Empty;

    public int StreamedChunkCount { get; set; }

    public string StreamedThinkingContent { get; set; } = string.Empty;

    public int StreamedThinkingChunkCount { get; set; }

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
