namespace XE_Local_AI_Engine.Client.Hubs;

using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

/// <summary>
///     Bounded, ordered transport relay from an application event buffer to per-scope SignalR groups.
/// </summary>
/// <remarks>
///     When the relay is saturated it drops only the live transport copy; the sequence gap makes the client replay or
///     refetch. The buffer stays the replay authority — a relay only bridges what the buffer already published.
/// </remarks>
internal abstract class HubEventRelay<TEvent, THub> : BackgroundService
    where THub : Hub
{
    private readonly Channel<TEvent> _channel;
    private readonly Func<TEvent, string> _group;
    private readonly IHubContext<THub> _hubContext;
    private readonly ILogger _logger;
    private readonly Action<ILogger, TEvent> _logSaturated;
    private readonly string _method;

    protected HubEventRelay(IHubContext<THub> hubContext,
        ILogger logger,
        int capacity,
        string method,
        Func<TEvent, string> group,
        Action<ILogger, TEvent> logSaturated)
    {
        _channel = Channel.CreateBounded<TEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        ArgumentNullException.ThrowIfNull(group);
        _group = group;
        ArgumentNullException.ThrowIfNull(hubContext);
        _hubContext = hubContext;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ArgumentNullException.ThrowIfNull(logSaturated);
        _logSaturated = logSaturated;
        ArgumentNullException.ThrowIfNull(method);
        _method = method;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        Subscribe();
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        _ = _channel.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
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
            await foreach (var published in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _hubContext.Clients.Group(_group(published)).SendAsync(_method, published, stoppingToken);
                }
                catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
                {
                    // One failed send must not end the relay; the sequence gap makes the client replay the lost copy.
                    _logger.LogWarning("Could not relay a {Method} live event ({ErrorClass}); the client must replay.", _method, exception.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown. The application buffer owns plaintext eviction for every execution terminal path.
        }
    }
}
