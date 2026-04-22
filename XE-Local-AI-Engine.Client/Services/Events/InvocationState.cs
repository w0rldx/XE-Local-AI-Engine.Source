namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.Client.Models.Enums;

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

    public InvocationApprovalState? PendingApproval { get; set; }

    public InvocationApprovalResolutionState? LastApprovalResolution { get; set; }

    public IReadOnlyList<InvocationToolCallState> PendingToolCalls { get; set; } = [];

    public InvocationToolCallResultState? LastToolCallResult { get; set; }
}

public sealed record InvocationApprovalState(
    string RequestId,
    string Description,
    DateTimeOffset RequestedAt);

public sealed record InvocationApprovalResolutionState(
    string RequestId,
    bool Approved,
    DateTimeOffset ResolvedAt);

public sealed record InvocationToolCallState(
    string RequestId,
    string ToolName,
    string Parameters,
    DateTimeOffset RequestedAt);

public sealed record InvocationToolCallResultState(
    string RequestId,
    bool Succeeded,
    string Result,
    string? Error,
    DateTimeOffset ResolvedAt);
