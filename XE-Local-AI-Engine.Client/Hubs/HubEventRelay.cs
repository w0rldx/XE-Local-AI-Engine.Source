namespace XE_Local_AI_Engine.Client.Hubs;

using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

/// <summary>
///     Bounded, ordered transport relay from an application event buffer to per-scope SignalR groups. When the relay is
///     saturated it drops only the live transport copy; the sequence gap makes the client replay or refetch. The buffer
///     stays the replay authority — a relay only bridges what the buffer already published.
/// </summary>
internal abstract class HubEventRelay<TEvent, THub>(
    IHubContext<THub> hubContext,
    ILogger logger,
    int capacity,
    string method,
    Func<TEvent, string> group,
    Action<ILogger, TEvent> logSaturated) : BackgroundService
    where THub : Hub
{
    private readonly Channel<TEvent> _channel = Channel.CreateBounded<TEvent>(new BoundedChannelOptions(capacity)
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    private readonly Func<TEvent, string> _group = group ?? throw new ArgumentNullException(nameof(group));
    private readonly IHubContext<THub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly ILogger _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly Action<ILogger, TEvent> _logSaturated = logSaturated ?? throw new ArgumentNullException(nameof(logSaturated));
    private readonly string _method = method ?? throw new ArgumentNullException(nameof(method));

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Subscribe();
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        _ = _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Attaches this relay's handler to its buffer; the exact mirror of <see cref="Unsubscribe" />.</summary>
    protected abstract void Subscribe();

    protected abstract void Unsubscribe();

    /// <summary>Hands one published event to the transport, dropping and warning only when the bound is reached.</summary>
    protected void Enqueue(TEvent published)
    {
        if (!_channel.Writer.TryWrite(published))
        {
            _logSaturated(_logger, published);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var published in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await _hubContext.Clients.Group(_group(published)).SendAsync(_method, published, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown. The application buffer owns plaintext eviction for every execution terminal path.
        }
    }
}

/// <summary>Bridges buffered benchmark run output to the Operator-scoped benchmark hub.</summary>
internal sealed class BenchmarkRunHubEventRelay(
    IBenchmarkEventBuffer events,
    IHubContext<BenchmarkRunHub> hubContext,
    ILogger<BenchmarkRunHubEventRelay> logger) : HubEventRelay<BenchmarkRunStreamEvent, BenchmarkRunHub>(hubContext,
    logger,
    BenchmarkEventBufferOptions.DefaultMaxEventCount,
    BenchmarkRunHubEvents.Event,
    static streamEvent => BenchmarkRunHub.RunGroup(streamEvent.RunId),
    static (log, streamEvent) => log.LogWarning("Benchmark live-event relay was saturated for run {RunId} at sequence {Sequence}; the client must replay.",
        streamEvent.RunId,
        streamEvent.Sequence))
{
    private readonly IBenchmarkEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));

    protected override void Subscribe() =>
        _events.EventPublished += OnEventPublished;

    protected override void Unsubscribe() =>
        _events.EventPublished -= OnEventPublished;

    private void OnEventPublished(object? sender, BenchmarkRunStreamEventArgs args) =>
        Enqueue(args.StreamEvent);
}

/// <summary>Bridges buffered dataset generation progress to the Operator-scoped generation hub.</summary>
internal sealed class DatasetGenerationHubEventRelay(
    IDatasetGenerationEventBuffer events,
    IHubContext<DatasetGenerationHub> hubContext,
    ILogger<DatasetGenerationHubEventRelay> logger) : HubEventRelay<DatasetGenerationEvent, DatasetGenerationHub>(hubContext,
    logger,
    DatasetGenerationEventBufferOptions.DefaultMaxEventCount,
    DatasetGenerationHubEvents.Event,
    static generationEvent => DatasetGenerationHub.DatasetGroup(generationEvent.DatasetId),
    static (log, generationEvent) => log.LogWarning("The dataset generation relay was saturated for dataset {DatasetId} at sequence {Sequence}; the client must replay.",
        generationEvent.DatasetId,
        generationEvent.Sequence))
{
    private readonly IDatasetGenerationEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));

    protected override void Subscribe() =>
        _events.EventPublished += OnEventPublished;

    protected override void Unsubscribe() =>
        _events.EventPublished -= OnEventPublished;

    private void OnEventPublished(object? sender, DatasetGenerationEventArgs args) =>
        Enqueue(args.Event);
}

/// <summary>Bridges buffered training run progress to the Operator-scoped run hub.</summary>
internal sealed class TrainingRunHubEventRelay(
    ITrainingRunEventBuffer events,
    IHubContext<TrainingRunHub> hubContext,
    ILogger<TrainingRunHubEventRelay> logger) : HubEventRelay<TrainingRunEvent, TrainingRunHub>(hubContext,
    logger,
    TrainingRunEventBufferOptions.DefaultMaxEventCount,
    TrainingRunHubEvents.Event,
    static runEvent => TrainingRunHub.RunGroup(runEvent.RunId),
    static (log, runEvent) => log.LogWarning("The training run relay was saturated for run {RunId} at sequence {Sequence}; the client must replay.",
        runEvent.RunId,
        runEvent.Sequence))
{
    private readonly ITrainingRunEventBuffer _events = events ?? throw new ArgumentNullException(nameof(events));

    protected override void Subscribe() =>
        _events.EventPublished += OnEventPublished;

    protected override void Unsubscribe() =>
        _events.EventPublished -= OnEventPublished;

    private void OnEventPublished(object? sender, TrainingRunEventArgs args) =>
        Enqueue(args.Event);
}
