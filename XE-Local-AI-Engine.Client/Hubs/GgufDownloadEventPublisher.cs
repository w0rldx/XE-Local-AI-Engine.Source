namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     Hub-backed <see cref="IGgufDownloadEventPublisher" />, replacing the no-op default in the Client host.
/// </summary>
/// <remarks>
///     Broadcasts each sanitized acquisition status to all connected clients under
///     <see cref="GgufDownloadHubEvents.StatusChanged" /> as the SignalR method name, so the React client subscribes
///     once and reconciles each push by model name. The projection copies only the safe fields: no path, URL, or token.
/// </remarks>
internal sealed class GgufDownloadEventPublisher : IGgufDownloadEventPublisher
{
    private readonly IHubContext<GgufDownloadHub> _hubContext;

    public GgufDownloadEventPublisher(IHubContext<GgufDownloadHub> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        _hubContext = hubContext;
    }

    public Task PublishStatusAsync(GgufAcquisitionStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        return _hubContext.Clients.All.SendAsync(GgufDownloadHubEvents.StatusChanged, ToHubMessage(status), cancellationToken);
    }

    private static GgufDownloadStatusHubMessage ToHubMessage(GgufAcquisitionStatus status)
    {
        return new GgufDownloadStatusHubMessage
        {
            ModelName = status.ModelName,
            Phase = status.Phase.ToString(),
            CompletedBytes = status.CompletedBytes,
            TotalBytes = status.TotalBytes,
            SanitizedError = status.SanitizedError,
            OperationId = status.OperationId,
            OperationKind = status.OperationKind.ToString(),
            ErrorCode = status.ErrorCode,
            UpdatedAtUtc = status.UpdatedAtUtc
        };
    }
}
