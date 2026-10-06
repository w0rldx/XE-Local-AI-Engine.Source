namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Threading.Channels;

/// <summary>
///     Asks the dispatcher to sweep now rather than at its next interval. A singleton of its own so a lane can raise it
///     without depending on the dispatcher, which depends on the lanes.
/// </summary>
/// <remarks>
///     A sweep and not a per-run signal, because what a landing frees is node-wide: a lane slot or a concurrent-run place
///     another run's <c>Queued</c> row or <c>Pending</c> run is waiting on, and only a sweep finds that run.
/// </remarks>
internal sealed class GraphWorkflowSweepWake
{
    /// <summary>One slot, dropped on full: any number of raises before the next sweep coalesce into that sweep.</summary>
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(capacity: 1)
    {
        FullMode = BoundedChannelFullMode.DropWrite
    });

    public void Raise() =>
        _ = _requests.Writer.TryWrite(true);

    /// <summary>Returns on a raise, or once <paramref name="interval" /> has passed on <paramref name="timeProvider" />.</summary>
    /// <remarks>A raise that lands while a sweep runs stays queued, so the wait after that sweep returns at once.</remarks>
    public async Task WaitAsync(TimeSpan interval, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(interval, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            _ = await _requests.Reader.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The interval elapsed: the periodic safety sweep.
        }
    }
}
