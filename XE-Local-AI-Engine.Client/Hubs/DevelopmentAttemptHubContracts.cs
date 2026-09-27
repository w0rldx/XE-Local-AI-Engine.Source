namespace XE_Local_AI_Engine.Client.Hubs;

using XE_Local_AI_Engine.Client.Services.Development;

public sealed class DevelopmentAttemptSubscriptionSnapshot
{
    public required Guid ProjectId { get; init; }

    public required Guid TaskId { get; init; }

    public required Guid AttemptId { get; init; }

    public required long Watermark { get; init; }

    public required long DroppedOrCoalescedUpdateCount { get; init; }

    public required DevelopmentAttemptLiveUpdate? Latest { get; init; }
}
