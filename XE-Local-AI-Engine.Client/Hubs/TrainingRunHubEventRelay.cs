namespace XE_Local_AI_Engine.Client.Hubs;

using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>
///     Bounded, ordered transport relay from the run event buffer to per-run SignalR groups. A saturated relay drops
///     only the live transport copy; the resulting sequence gap makes the client replay or refetch.
/// </summary>
internal sealed class TrainingRunHubEventRelay(
    ITrainingRunEventBuffer events,
    IHubContext<TrainingRunHub> hubContext,
    ILogger<TrainingRunHubEventRelay> logger) : BackgroundService
{
    private readonly Channel<TrainingRunEvent> _channel = Channel.CreateBounded<TrainingRunEvent>(new BoundedChannelOptions(TrainingRunEventBufferOptions.DefaultMaxEventCount)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    private readonly ITrainingRunEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly IHubContext<TrainingRunHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly ILogger<TrainingRunHubEventRelay> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
            await foreach (var runEvent in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await _hubContext.Clients.Group(TrainingRunHub.RunGroup(runEvent.RunId))
                                 .SendAsync(TrainingRunHubEvents.Event, runEvent, stoppingToken)
                                 .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown. The application buffer owns eviction on every terminal path.
        }
    }

    private void OnEventPublished(object? sender, TrainingRunEventArgs args)
    {
        if (!_channel.Writer.TryWrite(args.Event))
        {
            _logger.LogWarning("The training run relay was saturated for run {RunId} at sequence {Sequence}; the client must replay.",
                args.Event.RunId,
                args.Event.Sequence);
        }
    }
}
