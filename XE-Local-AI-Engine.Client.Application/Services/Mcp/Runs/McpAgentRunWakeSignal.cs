namespace XE_Local_AI_Engine.Client.Services.Mcp.Runs;

using Microsoft.Extensions.Options;

/// <summary>
///     Wakes idle dispatcher workers once a run is claimable. A permit outlives a worker that is between its empty queue
///     read and its wait, so a wake raised in that gap is taken by the wait instead of being lost.
/// </summary>
internal sealed class McpAgentRunWakeSignal : IDisposable
{
    private readonly int _maxPending;
    private readonly SemaphoreSlim _permits = new(initialCount: 0);

    public McpAgentRunWakeSignal(IOptions<McpAgentRunOptions> options)
    {
        _maxPending = (options ?? throw new ArgumentNullException(nameof(options))).Value.MaxConcurrentWorkers;
    }

    /// <summary>Called after a Queued row has committed. Capped at the worker count: each stale permit costs one empty queue read.</summary>
    public void Raise()
    {
        if (_permits.CurrentCount < _maxPending)
        {
            _ = _permits.Release();
        }
    }

    /// <summary>Answers <see langword="true" /> on a permit, or <see langword="false" /> once <paramref name="fallback" /> has passed on <paramref name="timeProvider" />.</summary>
    public async Task<bool> WaitAsync(TimeSpan fallback, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(fallback, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _permits.WaitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The fallback interval elapsed: the caller re-reads the queue as a safety sweep.
            return false;
        }
    }

    public void Dispose() =>
        _permits.Dispose();
}
