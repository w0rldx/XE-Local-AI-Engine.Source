namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Providers.Training.Contracts;

internal sealed class TrainingRuntimeEventPublisher(IHubContext<TrainingRuntimeHub> hubContext) : ITrainingRuntimeEventPublisher
{
    public async Task PublishStatusAsync(TrainingRuntimeStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statusEvent);
        await hubContext.Clients.All.SendAsync(TrainingRuntimeHubEvents.StatusChanged,
            TrainingRuntimeStatusHubMessage.FromContract(statusEvent), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
///     Stable SignalR wire shape. The provider contract stays transport-agnostic; this projection keeps its CLR types
///     from leaking onto the wire and can absorb new fields without changing the provider event contract.
/// </summary>
internal sealed class TrainingRuntimeStatusHubMessage
{
    public required string Phase { get; init; }
    public required IReadOnlyList<string> AppendedLogLines { get; init; }
    public required long AppendedLogStartSequence { get; init; }
    public required bool Terminal { get; init; }
    public string? SanitizedError { get; init; }

    public static TrainingRuntimeStatusHubMessage FromContract(TrainingRuntimeStatusHubEvent statusEvent)
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
