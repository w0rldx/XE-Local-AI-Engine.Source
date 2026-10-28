namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>Bridges buffered dataset generation progress to the Operator-scoped generation hub.</summary>
internal sealed class DatasetGenerationHubEventRelay : HubEventRelay<DatasetGenerationEvent, DatasetGenerationHub>
{
    private readonly IDatasetGenerationEventBuffer _events;

    public DatasetGenerationHubEventRelay(IDatasetGenerationEventBuffer events,
        IHubContext<DatasetGenerationHub> hubContext,
        ILogger<DatasetGenerationHubEventRelay> logger) : base(hubContext,
        logger,
        DatasetGenerationEventBufferOptions.DefaultMaxEventCount,
        DatasetGenerationHubEvents.Event,
        static generationEvent => DatasetGenerationHub.DatasetGroup(generationEvent.DatasetId),
        static (log, generationEvent) => log.LogWarning("The dataset generation relay was saturated for dataset {DatasetId} at sequence {Sequence}; the client must replay.",
            generationEvent.DatasetId,
            generationEvent.Sequence))
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
    }

    protected override void Subscribe() =>
        _events.EventPublished += OnEventPublished;

    protected override void Unsubscribe() =>
        _events.EventPublished -= OnEventPublished;

    private void OnEventPublished(object? sender, DatasetGenerationEventArgs args) =>
        Enqueue(args.Event);
}
