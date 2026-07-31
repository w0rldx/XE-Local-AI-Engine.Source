namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows.Implementation;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;

/// <summary>
///     Explicit owner of ONE in-flight preview run: its state, cancellation, the per-run node-local
///     <see cref="IChatClient" />s (one per distinct model used by the graph's agents),
///     the .AI.Agent <see cref="IPreviewWorkflowRunSession" />, the owning hub connection id, and the per-run gate that
///     serializes continue/cancel. <see cref="DisposeAsync" /> disposes the session then EVERY client, swallow-logging
///     (mirrors <c>OrchestrationRunSession</c>) — disposal must never throw on a race.
///     Idle clock: <see cref="ResetIdleClock" /> renews the inter-event bound after each productive event;
///     <see cref="SuspendIdleClock" /> sets it to <see cref="System.Threading.Timeout.InfiniteTimeSpan" /> while Paused
///     so the sweeper does not kill a paused run.
///     Subscriber set: the hub connections currently watching this run. It is EMPTY between the start response and the
///     client's first Subscribe, and becomes empty again on unsubscribe/disconnect; <see cref="IsAbandonedPast" />
///     turns "empty for longer than the grace period" into the one bound a Paused run cannot escape.
/// </summary>
internal sealed class PreviewWorkflowRunHandle : IAsyncDisposable
{
    private readonly SemaphoreSlim _commandGate = new(initialCount: 1, maxCount: 1);
    private readonly ILogger _logger;
    private readonly Lock _stateGate = new();

    // Hub connection ids currently subscribed to this run's events. Guarded by _stateGate together with the
    // abandoned clock so "became empty" and "stamped the clock" are one atomic step.
    private readonly HashSet<string> _subscribers = new(StringComparer.Ordinal);

    private long _abandonedSinceTicks;

    private long _accumulatedOutputBytes;
    private bool _disposed;
    private bool _idleSuspended;
    private long _lastActivityTicks;

    public PreviewWorkflowRunHandle(Guid runId,
        IReadOnlyCollection<IChatClient> chatClients,
        IPreviewWorkflowRunSession session,
        string? connectionId,
        TimeProvider timeProvider,
        ILogger logger)
    {
        RunId = runId;
        ChatClients = chatClients ?? throw new ArgumentNullException(nameof(chatClients));
        Session = session ?? throw new ArgumentNullException(nameof(session));
        ConnectionId = connectionId;
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        CancellationTokenSource = new CancellationTokenSource();
        StartedAtUtc = timeProvider.GetUtcNow();
        _lastActivityTicks = StartedAtUtc.UtcTicks;
        // A run is born with no subscriber (the client only learns the runId from the start response and subscribes
        // afterwards), so the abandoned clock starts at run start. A client that never manages to subscribe is
        // therefore swept on the same grace period as one that reloads away.
        _abandonedSinceTicks = StartedAtUtc.UtcTicks;
        State = PreviewRunState.Running;
    }

    public Guid RunId { get; }

    /// <summary>The per-run node-local chat clients owned by this handle — one per distinct model used by the graph.</summary>
    public IReadOnlyCollection<IChatClient> ChatClients { get; }

    public IPreviewWorkflowRunSession Session { get; }

    public string? ConnectionId { get; }

    public TimeProvider TimeProvider { get; }

    public CancellationTokenSource CancellationTokenSource { get; }

    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>The pause request token surfaced by the runner; set when the run enters Paused, consumed by Continue.</summary>
    public string? PendingRequestId { get; set; }

    /// <summary>The Pause node the run is parked on; set alongside <see cref="PendingRequestId" /> when it pauses.</summary>
    public string? PausedNodeId { get; set; }

    /// <summary>How many hub connections are currently subscribed to this run's events.</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_stateGate)
            {
                return _subscribers.Count;
            }
        }
    }

    public PreviewRunState State { get; private set; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            await Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Ignoring error while disposing preview run session for run {RunId}.", RunId);
        }

        // IChatClient is IDisposable — every per-run node-local client (one per distinct model) is owned by this handle.
        foreach (var chatClient in ChatClients)
        {
            try
            {
                chatClient.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Ignoring error while disposing preview run chat client for run {RunId}.", RunId);
            }
        }

        try
        {
            CancellationTokenSource.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Ignoring error while disposing preview run cancellation source for run {RunId}.", RunId);
        }

        _commandGate.Dispose();
    }

    /// <summary>Acquire the per-run gate so continue and cancel serialize and never race on the held session.</summary>
    public Task<bool> TryEnterCommandGateAsync(CancellationToken cancellationToken)
    {
        return _commandGate.WaitAsync(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public void ExitCommandGate()
    {
        // SemaphoreSlim.Release throws ObjectDisposedException only after Dispose; guarded by the disposed flag.
        if (!_disposed)
        {
            _ = _commandGate.Release();
        }
    }

    public void SetState(PreviewRunState state)
    {
        lock (_stateGate)
        {
            State = state;
        }
    }

    public PreviewRunState GetState()
    {
        lock (_stateGate)
        {
            return State;
        }
    }

    /// <summary>Renews the idle bound and clears any suspension. Called on each productive event and on resume.</summary>
    public void ResetIdleClock()
    {
        lock (_stateGate)
        {
            _idleSuspended = false;
            _lastActivityTicks = TimeProvider.GetUtcNow().UtcTicks;
        }
    }

    /// <summary>Suspends the idle clock (paused run waits on a human Continue). Sweeper skips suspended runs.</summary>
    public void SuspendIdleClock()
    {
        lock (_stateGate)
        {
            _idleSuspended = true;
        }
    }

    /// <summary>True when the idle TTL has elapsed and the clock is not suspended (a swept candidate).</summary>
    public bool IsIdleExpired(TimeSpan idleTimeout)
    {
        lock (_stateGate)
        {
            if (_idleSuspended)
            {
                return false;
            }

            var elapsed = TimeProvider.GetUtcNow().UtcTicks - _lastActivityTicks;
            return elapsed >= idleTimeout.Ticks;
        }
    }

    /// <summary>Records a hub connection as watching this run, clearing the abandoned clock.</summary>
    public void AddSubscriber(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_stateGate)
        {
            _ = _subscribers.Add(connectionId);
        }
    }

    /// <summary>
    ///     Drops a hub connection from the watcher set. When the set becomes empty the abandoned clock STARTS from
    ///     now, so the grace period is measured from the moment the last watcher left, not from run start.
    /// </summary>
    public void RemoveSubscriber(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_stateGate)
        {
            if (_subscribers.Remove(connectionId) && _subscribers.Count == 0)
            {
                _abandonedSinceTicks = TimeProvider.GetUtcNow().UtcTicks;
            }
        }
    }

    /// <summary>
    ///     True when NO hub connection has been watching this run for longer than <paramref name="grace" />. This is
    ///     the one bound a Paused run cannot escape: pause suspends the idle clock and is exempt from the wall-clock
    ///     cap (both correct — a human may take arbitrarily long to press Continue), but a run nobody is watching has
    ///     no human to wait for, so leaving it parked forever leaks its concurrency slot until the node restarts.
    /// </summary>
    public bool IsAbandonedPast(TimeSpan grace)
    {
        lock (_stateGate)
        {
            if (_subscribers.Count > 0)
            {
                return false;
            }

            return TimeProvider.GetUtcNow().UtcTicks - _abandonedSinceTicks >= grace.Ticks;
        }
    }

    /// <summary>True when a Running (not Paused) run has exceeded the hard wall-clock cap.</summary>
    public bool IsOverWallClock(TimeSpan maxRunDuration)
    {
        if (GetState() == PreviewRunState.Paused)
        {
            return false;
        }

        return TimeProvider.GetUtcNow() - StartedAtUtc >= maxRunDuration;
    }

    /// <summary>
    ///     Adds emitted output bytes to the per-run accumulator and returns true when the byte cap is now exceeded.
    /// </summary>
    public bool AddOutputBytesAndCheckCap(int byteCount, int maxOutputBytes)
    {
        var total = Interlocked.Add(ref _accumulatedOutputBytes, byteCount);
        return total >= maxOutputBytes;
    }
}
