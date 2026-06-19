namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows.Implementation;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;

/// <summary>
///     Explicit owner of ONE in-flight preview run: its state, cancellation, the per-run node-local
///     <see cref="IChatClient" />s (one per distinct model used by the graph's agents),
///     the .AI.Agent <see cref="IPreviewWorkflowRunSession" />, the owning hub connection id, and the per-run gate that
///     serializes continue/cancel. <see cref="DisposeAsync" /> disposes the session then EVERY client, swallow-logging
///     (mirrors <c>OrchestrationRunSession</c>) — disposal must never throw on a race.
///
///     Idle clock: <see cref="ResetIdleClock" /> renews the inter-event bound after each productive event;
///     <see cref="SuspendIdleClock" /> sets it to <see cref="System.Threading.Timeout.InfiniteTimeSpan" /> while Paused
///     so the sweeper does not kill a paused run.
/// </summary>
internal sealed class PreviewWorkflowRunHandle : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly object _stateGate = new();

    private long _accumulatedOutputBytes;
    private long _lastActivityTicks;
    private bool _idleSuspended;
    private bool _disposed;

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

    public PreviewRunState State { get; private set; }

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
}
