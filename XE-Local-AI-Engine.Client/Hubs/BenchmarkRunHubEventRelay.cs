namespace XE_Local_AI_Engine.Client.Hubs;

using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     Bounded, ordered transport relay from the application event buffer to per-run SignalR groups. When the relay is
///     saturated it drops only the live transport copy; the sequence gap makes the client replay or refetch.
/// </summary>
internal sealed class BenchmarkRunHubEventRelay(
    IBenchmarkEventBuffer events,
    IHubContext<BenchmarkRunHub> hubContext,
    ILogger<BenchmarkRunHubEventRelay> logger) : BackgroundService
{
    private readonly Channel<BenchmarkRunStreamEvent> _channel = Channel.CreateBounded<BenchmarkRunStreamEvent>(new BoundedChannelOptions(
        BenchmarkEventBufferOptions.DefaultMaxEventCount)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly IBenchmarkEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));
    private readonly IHubContext<BenchmarkRunHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly ILogger<BenchmarkRunHubEventRelay> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _events.EventPublished += OnEventPublished;
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var streamEvent in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await _hubContext.Clients.Group(BenchmarkRunHub.RunGroup(streamEvent.RunId))
                                 .SendAsync(BenchmarkRunHubEvents.Event, streamEvent, stoppingToken)
                                 .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown. The application buffer owns plaintext eviction for every execution terminal path.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _events.EventPublished -= OnEventPublished;
        _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnEventPublished(object? sender, BenchmarkRunStreamEventArgs args)
    {
        if (!_channel.Writer.TryWrite(args.StreamEvent))
        {
            _logger.LogWarning("Benchmark live-event relay was saturated for run {RunId} at sequence {Sequence}; the client must replay.",
                args.StreamEvent.RunId,
                args.StreamEvent.Sequence);
        }
    }
}
