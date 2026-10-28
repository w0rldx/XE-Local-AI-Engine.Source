namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>Bridges buffered training run progress to the Operator-scoped run hub.</summary>
internal sealed class TrainingRunHubEventRelay : HubEventRelay<TrainingRunEvent, TrainingRunHub>
{
    private readonly ITrainingRunEventBuffer _events;

    public TrainingRunHubEventRelay(ITrainingRunEventBuffer events,
        IHubContext<TrainingRunHub> hubContext,
        ILogger<TrainingRunHubEventRelay> logger) : base(hubContext,
        logger,
        TrainingRunEventBufferOptions.DefaultMaxEventCount,
        TrainingRunHubEvents.Event,
        static runEvent => TrainingRunHub.RunGroup(runEvent.RunId),
        static (log, runEvent) => log.LogWarning("The training run relay was saturated for run {RunId} at sequence {Sequence}; the client must replay.",
            runEvent.RunId,
            runEvent.Sequence))
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
    }

    protected override void Subscribe() =>
        _events.EventPublished += OnEventPublished;

    protected override void Unsubscribe() =>
        _events.EventPublished -= OnEventPublished;

    private void OnEventPublished(object? sender, TrainingRunEventArgs args) =>
        Enqueue(args.Event);
}
