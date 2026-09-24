namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     One node run's work in flight: the task, the token that stops it, and the two facts the dispatcher needs about
///     it without touching the task itself.
/// </summary>
/// <remarks>
///     <see cref="Attempt" /> is what makes an answer belong to a try: a retry lands the row on a new attempt, and a
///     pass belonging to the one before is not an answer about the one the row is on now. <see cref="InvocationId" />
///     is minted before the work starts, because the stop path has to have something to hand to whatever knows how to
///     unwind it. <see cref="LeaseAcquired" /> is a <see cref="StrongBox{T}" /> rather than a <see cref="bool" /> so
///     the task body can flip it and the poll can see it: the row reads <c>Queued</c> until the work holds its slot.
/// </remarks>
internal sealed class GraphWorkflowInFlight<TResult>
{
    public required CancellationTokenSource Cancellation { get; init; }

    public required Task<TResult> Work { get; init; }

    public required int Attempt { get; init; }

    public required Guid InvocationId { get; init; }

    public required StrongBox<bool> LeaseAcquired { get; init; }
}

/// <summary>
///     The in-flight registry every graph-workflow lane is built out of: a bounded number of node runs may hold a slot
///     at once, each driven by a detached task that produces a RESULT and never writes a row.
/// </summary>
/// <remarks>
///     Generic and executor-agnostic on purpose — what a turn is, how it is settled and what document it produces are
///     the executor's business, while the slots, the registry and the stop-and-forget contract are the same for the
///     agent and tool lanes both, and a second implementation of those is how two lanes come to disagree about whether
///     a row is still being driven. Its contract is asserted directly, because the two things that keep a drain from
///     spinning — a stop answering no on a repeat, an entry outliving the work until a poll SEES it land — are its own.
/// </remarks>
internal sealed class GraphWorkflowInFlightLane<TResult> : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, GraphWorkflowInFlight<TResult>> _inflight = new();
    private readonly SemaphoreSlim _lane;
    private readonly Action<GraphWorkflowInFlight<TResult>>? _onDiscard;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    /// <summary>
    ///     <paramref name="onDiscard" /> is what an executor whose work needs more than a cancelled token to unwind
    ///     hooks into every drop — including the superseded ones, which never come through the executor at all. It runs
    ///     before the token is cancelled and must not block.
    /// </summary>
    public GraphWorkflowInFlightLane(int slots, Action<GraphWorkflowInFlight<TResult>>? onDiscard = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        _lane = new SemaphoreSlim(slots, slots);
        _onDiscard = onDiscard;
    }

    /// <summary>Whether this node run's work is being driven right now, or has landed and not yet been consumed.</summary>
    public bool IsInFlight(Guid nodeRunId) =>
        _inflight.ContainsKey(nodeRunId);

    public bool TryGet(Guid nodeRunId, [NotNullWhen(true)] out GraphWorkflowInFlight<TResult>? flight) =>
        _inflight.TryGetValue(nodeRunId, out flight);

    /// <summary>
    ///     Takes a slot and starts <paramref name="work" />, or answers <see langword="null" /> when the lane is full.
    /// </summary>
    /// <remarks>
    ///     The slot is taken BEFORE the work starts and released when it ends, whatever it ends as, so a throw inside
    ///     the caller's task cannot leak one. A full lane is queueing rather than failure: nothing is written, and the
    ///     next tick asks again.
    /// </remarks>
    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to the in-flight entry, which outlives this call by design: Consume disposes it "
                        + "when the settle has committed, Discard disposes it once the work has unwound, and DisposeAsync "
                        + "disposes whatever is left. Disposing here would cancel the work that was just started.")]
    public async Task<GraphWorkflowInFlight<TResult>?> TryStartAsync(Guid nodeRunId,
        int attempt,
        Guid invocationId,
        Func<StrongBox<bool>, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        // simplified: a zero-timeout wait, so a full lane costs a tick rather than a parked thread. It does NOT bound how
        // long a row sits Queued waiting on a node-wide slot below — bound the lease wait, or expire on a queued-at stamp, if that measures.
        if (!await _lane.WaitAsync(millisecondsTimeout: 0, cancellationToken))
        {
            return null;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        // The box is the lane's to make and the work's to flip: it is the only thing the poll can read about a turn
        // that has started but does not yet hold the node-wide slot it is waiting for.
        var leaseAcquired = new StrongBox<bool>(value: false);
        var flight = new GraphWorkflowInFlight<TResult>
        {
            Cancellation = cancellation,
            Work = RunAsync(work, leaseAcquired, cancellation.Token),
            Attempt = attempt,
            InvocationId = invocationId,
            LeaseAcquired = leaseAcquired
        };
        if (_inflight.TryAdd(nodeRunId, flight))
        {
            return flight;
        }

        // Something is already driving this row: the caller checks first, so reaching here means it raced itself and the
        // entry that won is the one the poll settles. The loser is discarded — cancelled, then disposed once its work notices, since a token source nothing owns leaks.
        await cancellation.CancelAsync();
        _ = DisposeWhenDoneAsync(flight);
        return null;
    }

    /// <summary>Asks the work to stop, answering whether it actually asked.</summary>
    /// <remarks>
    ///     <see langword="false" /> on a repeat, and that is the point rather than tidiness: the entry lives until a
    ///     poll SEES the work land, so a cancelling drain reaches this every tick until then, and since the caller
    ///     counts a <see langword="true" /> as a written transition and the dispatcher re-signals after any productive
    ///     tick, answering <see langword="true" /> each time would spin the drain for the work's whole duration.
    /// </remarks>
    public async Task<bool> StopAsync(Guid nodeRunId)
    {
        if (!_inflight.TryGetValue(nodeRunId, out var flight) || flight.Cancellation.IsCancellationRequested)
        {
            return false;
        }

        // simplified: the cost is up to one DispatchIntervalMilliseconds sweep before a stopped turn is noticed, where the spin noticed at once.
        // Signalling from the work's continuation would inject the dispatcher into the lane it takes; a settable signal or completion channel breaks that, if it measures.
        await flight.Cancellation.CancelAsync();
        return true;
    }

    /// <summary>Drops the entry and cancels its work without waiting for the unwind.</summary>
    /// <remarks>
    ///     Awaiting the unwind would hold the dispatcher's advance gate, and with it every other run, for as long as
    ///     the work takes to notice. Removing the entry is the load-bearing half, not the cancel: a row settled with
    ///     its entry left behind would refuse the next attempt its place in the registry, and that attempt would then
    ///     run with nothing polling it.
    /// </remarks>
    public async Task DiscardAsync(Guid nodeRunId)
    {
        if (!_inflight.TryRemove(nodeRunId, out var flight))
        {
            return;
        }

        _onDiscard?.Invoke(flight);
        await flight.Cancellation.CancelAsync();
        _ = DisposeWhenDoneAsync(flight);
    }

    /// <summary>Consumes a landed entry, once its settle has COMMITTED.</summary>
    /// <remarks>
    ///     Doing it before the write would spend the result on a write that may throw, and the next poll would then
    ///     find no entry and report "the host stopped" about work that finished perfectly.
    /// </remarks>
    public void Consume(Guid nodeRunId)
    {
        if (_inflight.TryRemove(nodeRunId, out var flight))
        {
            flight.Cancellation.Dispose();
        }
    }

    /// <summary>
    ///     Drops every entry whose row has moved on — re-attempted, cancelled, or otherwise no longer this lane's to
    ///     settle.
    /// </summary>
    /// <remarks>
    ///     Called once a tick before anything is polled, because a retry reaches a row WITHOUT coming through the lane
    ///     that is driving it.
    /// </remarks>
    public async Task ForgetSupersededAsync(IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns)
    {
        ArgumentNullException.ThrowIfNull(nodeRuns);

        foreach (var nodeRun in nodeRuns)
        {
            if (_inflight.TryGetValue(nodeRun.Id, out var flight)
                && (flight.Attempt != nodeRun.Attempt
                    || nodeRun.Status is not (GraphWorkflowNodeRunStatus.Queued or GraphWorkflowNodeRunStatus.Running)))
            {
                await DiscardAsync(nodeRun.Id);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) == 1)
        {
            return;
        }

        await _shutdown.CancelAsync();
        foreach (var flight in _inflight.Values)
        {
            await SwallowAsync(flight.Work);
            flight.Cancellation.Dispose();
        }

        _inflight.Clear();
        _shutdown.Dispose();
        _lane.Dispose();
    }

    /// <summary>The caller's work, with the slot released whatever it ends as.</summary>
    private async Task<TResult> RunAsync(Func<StrongBox<bool>, CancellationToken, Task<TResult>> work, StrongBox<bool> leaseAcquired, CancellationToken cancellationToken)
    {
        try
        {
            return await work(leaseAcquired, cancellationToken);
        }
        finally
        {
            _ = _lane.Release();
        }
    }

    private static async Task DisposeWhenDoneAsync(GraphWorkflowInFlight<TResult> flight)
    {
        await SwallowAsync(flight.Work);
        flight.Cancellation.Dispose();
    }

    /// <summary>A discarded result is about work the run has decided to replace, so how it ended is not news.</summary>
    private static async Task SwallowAsync(Task work)
    {
        try
        {
            await work;
        }
        catch (Exception)
        {
            // Every outcome of a discarded entry is deliberately unread, including this one.
        }
    }
}
