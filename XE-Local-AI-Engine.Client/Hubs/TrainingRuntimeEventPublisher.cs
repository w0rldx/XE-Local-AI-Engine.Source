namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Providers.Training.Contracts;

internal sealed class TrainingRuntimeEventPublisher : ITrainingRuntimeEventPublisher
{
    private readonly IHubContext<TrainingRuntimeHub> _hubContext;

    public TrainingRuntimeEventPublisher(IHubContext<TrainingRuntimeHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task PublishStatusAsync(TrainingRuntimeStatusEvent statusEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        await _hubContext.Clients.All.SendAsync(TrainingRuntimeHubEvents.StatusChanged,
            ToHubMessage(statusEvent), cancellationToken);
    }

    private static TrainingRuntimeStatusHubMessage ToHubMessage(TrainingRuntimeStatusEvent statusEvent)
    {
        return new TrainingRuntimeStatusHubMessage
        {
            Phase = statusEvent.Phase,
            AppendedLogLines = statusEvent.AppendedLogLines,
            AppendedLogStartSequence = statusEvent.AppendedLogStartSequence,
            Terminal = statusEvent.Terminal,
            SanitizedError = statusEvent.SanitizedError
        };
    }
}
