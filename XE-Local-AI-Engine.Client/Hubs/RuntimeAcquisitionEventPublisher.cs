namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Hub-backed <see cref="IRuntimeAcquisitionEventPublisher" />, replacing the no-op default the provider registers.
/// </summary>
/// <remarks>
///     Broadcasts each runtime acquisition status event to all connected operator clients under
///     <see cref="RuntimeAcquisitionHubEvents.StatusChanged" />, so the React client subscribes once and advances the
///     phase / byte progress without a follow-up REST poll. Payloads are sanitized upstream by the status registry —
///     never an absolute path, a download URL, or a token.
/// </remarks>
internal sealed class RuntimeAcquisitionEventPublisher : IRuntimeAcquisitionEventPublisher
{
    private readonly IHubContext<RuntimeAcquisitionHub> _hubContext;

    public RuntimeAcquisitionEventPublisher(IHubContext<RuntimeAcquisitionHub> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        _hubContext = hubContext;
    }

    public Task PublishStatusAsync(RuntimeAcquisitionStatusEvent statusEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        return _hubContext.Clients.All.SendAsync(RuntimeAcquisitionHubEvents.StatusChanged,
            ToHubMessage(statusEvent), cancellationToken);
    }

    private static RuntimeAcquisitionStatusHubMessage ToHubMessage(RuntimeAcquisitionStatusEvent statusEvent)
    {
        return new RuntimeAcquisitionStatusHubMessage
        {
            Sequence = statusEvent.Sequence,
            Phase = statusEvent.Phase,
            Variant = statusEvent.Variant,
            Tag = statusEvent.Tag,
            CompletedBytes = statusEvent.CompletedBytes,
            TotalBytes = statusEvent.TotalBytes,
            StepIndex = statusEvent.StepIndex,
            StepCount = statusEvent.StepCount,
            SanitizedError = statusEvent.SanitizedError
        };
    }
}
