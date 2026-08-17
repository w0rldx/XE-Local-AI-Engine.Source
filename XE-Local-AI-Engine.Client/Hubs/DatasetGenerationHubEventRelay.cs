namespace XE_Local_AI_Engine.Client.Hubs;

using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>
///     Bounded, ordered transport relay from the generation event buffer to per-dataset SignalR groups. A saturated relay
///     drops only the live transport copy; the resulting sequence gap makes the client replay or refetch.
/// </summary>
internal sealed class DatasetGenerationHubEventRelay(
    IDatasetGenerationEventBuffer events,
    IHubContext<DatasetGenerationHub> hubContext,
    ILogger<DatasetGenerationHubEventRelay> logger) : BackgroundService
{
    private readonly Channel<DatasetGenerationEvent> _channel = Channel.CreateBounded<DatasetGenerationEvent>(new BoundedChannelOptions(DatasetGenerationEventBufferOptions.DefaultMaxEventCount)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    private readonly IDatasetGenerationEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly IHubContext<DatasetGenerationHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly ILogger<DatasetGenerationHubEventRelay> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _events.EventPublished += OnEventPublished;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _events.EventPublished -= OnEventPublished;
        _ = _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var generationEvent in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await _hubContext.Clients.Group(DatasetGenerationHub.DatasetGroup(generationEvent.DatasetId))
                                 .SendAsync(DatasetGenerationHubEvents.Event, generationEvent, stoppingToken)
                                 .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown. The application buffer owns plaintext eviction on every terminal path.
        }
    }

    private void OnEventPublished(object? sender, DatasetGenerationEventArgs args)
    {
        if (!_channel.Writer.TryWrite(args.Event))
        {
            _logger.LogWarning("The dataset generation relay was saturated for dataset {DatasetId} at sequence {Sequence}; the client must replay.",
                args.Event.DatasetId,
                args.Event.Sequence);
        }
    }
}
