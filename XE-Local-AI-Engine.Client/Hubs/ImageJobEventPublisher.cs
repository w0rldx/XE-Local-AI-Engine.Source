namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Hub-backed <see cref="IImageJobEventPublisher" />, replacing the no-op default in the Client host.
/// </summary>
/// <remarks>
///     Delivers each coarse status event ONLY to the job's per-job group (<see cref="ImageJobHub.JobGroup" />) under
///     <see cref="ImageJobHubEvents.StatusChanged" /> as the SignalR method name, so a connection receives only the
///     jobs it subscribed to. <see cref="ImageJobHub.Subscribe" /> replays through the same projection, so a replayed
///     frame is identical to the live one it deduplicates against. Model-download statuses are broadcast instead.
/// </remarks>
internal sealed class ImageJobEventPublisher : IImageJobEventPublisher
{
    private readonly IHubContext<ImageJobHub> _hubContext;

    public ImageJobEventPublisher(IHubContext<ImageJobHub> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        _hubContext = hubContext;
    }

    public Task PublishStatusAsync(ImageJobStatusEvent statusEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        return _hubContext.Clients
                          .Group(ImageJobHub.JobGroup(statusEvent.JobId))
                          .SendAsync(ImageJobHubEvents.StatusChanged, ToHubMessage(statusEvent), cancellationToken);
    }

    // Broadcast, not grouped: the app-wide operations feed watches every download without knowing its name in advance.
    public Task PublishModelDownloadAsync(ImageModelDownloadStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        return _hubContext.Clients.All.SendAsync(ImageJobHubEvents.ModelDownloadStatusChanged,
            new ImageModelDownloadHubMessage
            {
                ModelName = status.ModelName,
                Phase = status.Phase.ToString(),
                CompletedBytes = status.CompletedBytes,
                TotalBytes = status.TotalBytes,
                SanitizedError = status.SanitizedError,
                PartIndex = status.PartIndex,
                PartCount = status.PartCount,
                Sequence = status.Sequence
            },
            cancellationToken);
    }

    internal static ImageJobStatusHubMessage ToHubMessage(ImageJobStatusEvent statusEvent)
    {
        return new ImageJobStatusHubMessage
        {
            JobId = statusEvent.JobId,
            Phase = statusEvent.Phase,
            QueuePosition = statusEvent.QueuePosition,
            ElapsedMs = statusEvent.ElapsedMs,
            ImageId = statusEvent.ImageId,
            SanitizedError = statusEvent.SanitizedError,
            OccurredAtUtc = statusEvent.OccurredAtUtc,
            Seq = statusEvent.Seq,
            GenerationPhase = statusEvent.GenerationPhase,
            Step = statusEvent.Step,
            TotalSteps = statusEvent.TotalSteps,
            SecondsPerIteration = statusEvent.SecondsPerIteration,
            EstimatedRemainingMs = statusEvent.EstimatedRemainingMs
        };
    }
}
