namespace XE_Local_AI_Engine.Client.Services.Events;

using XE_Local_AI_Engine.Client.Models.Enums;

public sealed class InvocationHistoryEntry
{
    public required Guid InvocationId { get; init; }

    public required Guid ConversationId { get; init; }

    public required InvocationStatus Status { get; init; }

    public required string? ModelUsed { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required string? Error { get; init; }

    public required FailureCategory? FailureCategory { get; init; }

    public required int StreamedChunkCount { get; init; }

    public required int StreamedThinkingChunkCount { get; init; }

    public string? TraceId { get; init; }

    public TimeSpan Duration => CompletedAt - StartedAt;
}
