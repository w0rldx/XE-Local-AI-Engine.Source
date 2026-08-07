namespace XE_Local_AI_Engine.Client.Endpoints.Invocations.V1;

using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;

public sealed class InvocationMonitorResponse
{
    public required InvocationCurrentResponse? Current { get; init; }

    public required IReadOnlyList<InvocationHistoryResponse> History { get; init; }

    public required int HistoryCapacity { get; init; }
}

public sealed class InvocationCurrentResponse
{
    public required Guid InvocationId { get; init; }

    public required Guid ConversationId { get; init; }

    public required InvocationStatus Status { get; init; }

    public string? ModelUsed { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset LastUpdatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? Error { get; init; }

    public FailureCategory? FailureCategory { get; init; }

    public required int StreamedChunkCount { get; init; }

    public required int StreamedThinkingChunkCount { get; init; }

    public required int PendingToolCallCount { get; init; }

    public required bool HasPendingApproval { get; init; }

    /// <summary>
    ///     True while the turn is parked on an <c>ask_user</c> question waiting on the operator. Without it a parked turn
    ///     reads as an ordinary running invocation with nothing pending. Deliberately CONTENT-FREE: the question text is
    ///     operator/model content and never travels on this ops endpoint.
    /// </summary>
    public required bool HasPendingQuestion { get; init; }

    /// <summary>
    ///     The W3C trace id of the run, for cross-correlation with exported traces/logs. Null when no activity was in
    ///     scope. Rendered as copyable text in the monitor so a failed run's "See local logs" row links to its trace.
    /// </summary>
    public string? TraceId { get; init; }
}

public sealed class InvocationHistoryResponse
{
    public required Guid InvocationId { get; init; }

    public required Guid ConversationId { get; init; }

    public required InvocationStatus Status { get; init; }

    public string? ModelUsed { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required long DurationMs { get; init; }

    public string? Error { get; init; }

    public FailureCategory? FailureCategory { get; init; }

    public required int StreamedChunkCount { get; init; }

    public required int StreamedThinkingChunkCount { get; init; }

    /// <summary>
    ///     The W3C trace id of the run, for cross-correlation with exported traces/logs. Null when no activity was in
    ///     scope. Rendered as copyable text in the monitor so a failed run's "See local logs" row links to its trace.
    /// </summary>
    public string? TraceId { get; init; }
}

