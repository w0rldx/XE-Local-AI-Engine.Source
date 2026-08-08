namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.Client.Models.Enums;

public sealed record InvocationHistoryEntry(
    Guid InvocationId,
    Guid ConversationId,
    InvocationStatus Status,
    string? ModelUsed,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? Error,
    FailureCategory? FailureCategory,
    int StreamedChunkCount,
    int StreamedThinkingChunkCount,
    string? TraceId = null)
{
    public TimeSpan Duration => CompletedAt - StartedAt;
}
