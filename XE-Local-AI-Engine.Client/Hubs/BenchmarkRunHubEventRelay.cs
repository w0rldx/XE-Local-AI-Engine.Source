namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Bridges buffered benchmark run output to the Operator-scoped benchmark hub.</summary>
internal sealed class BenchmarkRunHubEventRelay : HubEventRelay<BenchmarkRunStreamEvent, BenchmarkRunHub>
{
    private readonly IBenchmarkEventBuffer _events;

    public BenchmarkRunHubEventRelay(IBenchmarkEventBuffer events,
        IHubContext<BenchmarkRunHub> hubContext,
        ILogger<BenchmarkRunHubEventRelay> logger) : base(hubContext,
        logger,
        BenchmarkEventBufferOptions.DefaultMaxEventCount,
        BenchmarkRunHubEvents.Event,
        static streamEvent => BenchmarkRunHub.RunGroup(streamEvent.RunId),
        static (log, streamEvent) => log.LogWarning("Benchmark live-event relay was saturated for run {RunId} at sequence {Sequence}; the client must replay.",
            streamEvent.RunId,
            streamEvent.Sequence))
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
    }

    protected override void Subscribe() =>
        _events.EventPublished += OnEventPublished;

    protected override void Unsubscribe() =>
        _events.EventPublished -= OnEventPublished;

    private void OnEventPublished(object? sender, BenchmarkRunStreamEventArgs args) =>
        Enqueue(args.StreamEvent);
}
