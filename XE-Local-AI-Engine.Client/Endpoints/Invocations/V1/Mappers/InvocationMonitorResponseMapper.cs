namespace XE_Local_AI_Engine.Client.Endpoints.Invocations.V1.Mappers;

using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;

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
            HasPendingApproval = state.PendingApproval is not null,
            HasPendingQuestion = state.PendingQuestion is not null,
            TraceId = state.TraceId
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
            DurationMs = Math.Max(val1: 0, (long)entry.Duration.TotalMilliseconds),
            Error = ToOperatorError(entry.Error, entry.Status, entry.FailureCategory),
            FailureCategory = entry.FailureCategory,
            StreamedChunkCount = entry.StreamedChunkCount,
            StreamedThinkingChunkCount = entry.StreamedThinkingChunkCount,
            TraceId = entry.TraceId
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
