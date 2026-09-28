namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>
///     Stable SignalR client-method name for image-job status pushes. The React client subscribes to this single method;
///     each push carries the full coarse status, so the client reconciles by job id and dedupes on <c>Seq</c>.
/// </summary>
public static class ImageJobHubEvents
{
    public const string StatusChanged = "imageJob.statusChanged";
}

/// <summary>
///     Image-job status push payload, projected 1:1 from the Application <c>ImageJobStatusEvent</c> for both the live
///     group push and the late-subscriber replay.
/// </summary>
/// <remarks>
///     NEVER carries the prompt. The generation-timeline fields are nullable because the runtime only observes them for
///     part of a job, and a null is sent explicitly rather than omitted.
/// </remarks>
internal sealed class ImageJobStatusHubMessage
{
    public required Guid JobId { get; init; }

    public required string Phase { get; init; }

    public required int? QueuePosition { get; init; }

    public required long? ElapsedMs { get; init; }

    public required Guid? ImageId { get; init; }

    public required string? SanitizedError { get; init; }

    public required long OccurredAtUtc { get; init; }

    public required long Seq { get; init; }

    public required string? GenerationPhase { get; init; }

    public required int? Step { get; init; }

    public required int? TotalSteps { get; init; }

    public required double? SecondsPerIteration { get; init; }

    public required long? EstimatedRemainingMs { get; init; }
}
