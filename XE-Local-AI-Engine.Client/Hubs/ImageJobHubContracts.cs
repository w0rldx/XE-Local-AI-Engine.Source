namespace XE_Local_AI_Engine.Client.Hubs;

/// <summary>
///     Stable SignalR client-method name for image-job status pushes. The React client subscribes to this single method;
///     each push carries the full coarse status, so the client reconciles by job id and dedupes on <c>Seq</c>.
/// </summary>
public static class ImageJobHubEvents
{
    public const string StatusChanged = "imageJob.statusChanged";

    /// <summary>Broadcast to every connection: one image-model download's sanitized status.</summary>
    public const string ModelDownloadStatusChanged = "imageModelDownload.statusChanged";
}

/// <summary>
///     Image-model download push payload: the fields of the REST <c>ImageModelDownloadStatusResponse</c>, so the client
///     folds a push exactly as it would a list item. <see cref="Phase" /> is the <c>ImageModelDownloadPhase</c> name.
/// </summary>
/// <remarks>Never a path, URL or token: the coordinator's status is already sanitized.</remarks>
internal sealed class ImageModelDownloadHubMessage
{
    public required string ModelName { get; init; }

    public required string Phase { get; init; }

    public required long? CompletedBytes { get; init; }

    public required long? TotalBytes { get; init; }

    public required string? SanitizedError { get; init; }

    public required int? PartIndex { get; init; }

    public required int? PartCount { get; init; }

    /// <summary>The coordinator's write order; the client drops a push older than the one it applied for the model.</summary>
    public required long Sequence { get; init; }
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
