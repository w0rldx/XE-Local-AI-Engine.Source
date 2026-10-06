namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     Hub-backed <see cref="IRuntimeResidencyChangeNotifier" />, replacing the no-op default the providers register.
/// </summary>
/// <remarks>
///     Trailing edge: a change arms one timer, <see cref="CoalescingWindow" /> out, and later changes ride that tick. Each
///     tick makes every client re-read both lists, so sends are also kept <see cref="MinTickSpacing" /> apart: request
///     churn costs one tick per spacing, never more than the poll this replaces. Sends are serial, a failure is logged
///     and swallowed, and disposal disarms the timer and awaits the cancelled send; later changes are dropped.
/// </remarks>
internal sealed class RuntimeResidencyChangePublisher : IRuntimeResidencyChangeNotifier, IAsyncDisposable
{
    /// <summary>The delay from the first change after a quiet period to its tick.</summary>
    internal static readonly TimeSpan CoalescingWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>The minimum spacing between two ticks.</summary>
    internal static readonly TimeSpan MinTickSpacing = TimeSpan.FromSeconds(4);

    private readonly Lock _gate = new();
    private readonly IHubContext<RuntimeResidencyHub> _hubContext;
    private readonly ILogger<RuntimeResidencyChangePublisher> _logger;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TimeProvider _timeProvider;
    private readonly ITimer _timer;
    private bool _armed;
    private bool _disposed;
    private long? _lastTickTimestamp;
    private Task _lastSend = Task.CompletedTask;
    private long _sequence;

    public RuntimeResidencyChangePublisher(IHubContext<RuntimeResidencyHub> hubContext,
        TimeProvider timeProvider,
        ILogger<RuntimeResidencyChangePublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _hubContext = hubContext;
        _logger = logger;
        _timeProvider = timeProvider;
        _timer = timeProvider.CreateTimer(static state => ((RuntimeResidencyChangePublisher)state!).Fire(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public void NotifyChanged()
    {
        lock (_gate)
        {
            if (_disposed || _armed)
            {
                return;
            }

            // Measured on the monotonic clock, so a wall clock that is set back or forward cannot stretch or skip the spacing.
            var untilSpacing = _lastTickTimestamp is { } lastTick ? MinTickSpacing - _timeProvider.GetElapsedTime(lastTick) : TimeSpan.Zero;
            var delay = untilSpacing > CoalescingWindow ? untilSpacing : CoalescingWindow;

            _armed = true;
            _ = _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task lastSend;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lastSend = _lastSend;
        }

        await _timer.DisposeAsync();
        await _shutdownCts.CancelAsync();
        await lastSend;
        _shutdownCts.Dispose();
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _armed = false;
            _lastTickTimestamp = _timeProvider.GetTimestamp();
            _lastSend = SendAfterAsync(_lastSend, ++_sequence);
        }
    }

    private async Task SendAfterAsync(Task previous, long sequence)
    {
        // Off the caller's thread first, which holds the lock in Fire.
        await Task.Yield();
        await previous;
        try
        {
            await _hubContext.Clients.All.SendAsync(RuntimeResidencyHubEvents.Changed,
                new RuntimeResidencyChangedHubMessage
                {
                    Sequence = sequence
                },
                _shutdownCts.Token);
        }
        catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
        {
            // Host shutdown: nobody is left to tell.
        }
        catch (Exception exception)
        {
            // Best-effort: the REST reads stay the source of truth and the client's floor poll catches a lost tick.
            _logger.LogWarning("Could not publish runtime residency tick {Sequence} ({ErrorClass}).", sequence, exception.GetType().Name);
        }
    }
}
