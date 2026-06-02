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
}

internal static class InvocationMonitorResponseMapper
{
    private const string CancelledOperatorMessage = "Invocation was cancelled.";
    private const string FailedOperatorMessage = "Invocation ended with a failure. See local logs for details.";

    public static InvocationMonitorResponse ToResponse(InvocationState? current,
        IReadOnlyList<InvocationHistoryEntry> history,
        int historyCapacity)
    {
        ArgumentNullException.ThrowIfNull(history);

        return new InvocationMonitorResponse
        {
            Current = current?.ToResponse(),
            History = history.Select(ToResponse).ToArray(),
            HistoryCapacity = historyCapacity
        };
    }

    private static InvocationCurrentResponse ToResponse(this InvocationState state)
    {
        return new InvocationCurrentResponse
        {
            InvocationId = state.InvocationId,
            ConversationId = state.ConversationId,
            Status = state.Status,
            ModelUsed = state.ModelUsed,
            StartedAt = state.StartedAt,
            LastUpdatedAt = state.LastUpdatedAt,
            CompletedAt = state.CompletedAt,
            Error = ToOperatorError(state.Error, state.Status, state.FailureCategory),
            FailureCategory = state.FailureCategory,
            StreamedChunkCount = state.StreamedChunkCount,
            StreamedThinkingChunkCount = state.StreamedThinkingChunkCount,
            PendingToolCallCount = state.PendingToolCalls.Count,
            HasPendingApproval = state.PendingApproval is not null
        };
    }

    private static InvocationHistoryResponse ToResponse(InvocationHistoryEntry entry)
    {
        return new InvocationHistoryResponse
        {
            InvocationId = entry.InvocationId,
            ConversationId = entry.ConversationId,
            Status = entry.Status,
            ModelUsed = entry.ModelUsed,
            StartedAt = entry.StartedAt,
            CompletedAt = entry.CompletedAt,
            DurationMs = Math.Max(0, (long)entry.Duration.TotalMilliseconds),
            Error = ToOperatorError(entry.Error, entry.Status, entry.FailureCategory),
            FailureCategory = entry.FailureCategory,
            StreamedChunkCount = entry.StreamedChunkCount,
            StreamedThinkingChunkCount = entry.StreamedThinkingChunkCount
        };
    }

    private static string? ToOperatorError(string? error, InvocationStatus status, FailureCategory? failureCategory)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        return status == InvocationStatus.Cancelled || failureCategory == FailureCategory.Cancelled
            ? CancelledOperatorMessage
            : FailedOperatorMessage;
    }
}
