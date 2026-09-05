namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     One node run's work in flight: the task, the token that stops it, and the two facts the dispatcher needs about
///     it without touching the task itself.
///     <para>
///         <see cref="Attempt" /> is what makes an answer belong to a try: a retry lands the row on a new attempt, and
///         a pass belonging to the one before is not an answer about the one the row is on now.
///         <see cref="InvocationId" /> is minted before the work starts, because the stop path has to have something to
///         hand to whatever knows how to unwind it. <see cref="LeaseAcquired" /> is a
///         <see cref="StrongBox{T}" /> rather than a <see cref="bool" /> so the task body can flip it and the poll can
///         see it: the row honestly reads <c>Queued</c> until the work holds whatever node-wide slot it needs.
///     </para>
/// </summary>
internal sealed record GraphWorkflowInFlight<TResult>(CancellationTokenSource Cancellation,
    Task<TResult> Work,
    int Attempt,
    Guid InvocationId,
    StrongBox<bool> LeaseAcquired);

/// <summary>
///     The in-flight registry every graph-workflow lane is built out of: a bounded number of node runs may hold a slot
///     at once, each driven by a detached task that produces a RESULT and never writes a row.
///     <para>
///         Generic and executor-agnostic on purpose. What a turn is, how it is settled and what document it produces
///         are the executor's business; the slots, the registry and the stop-and-forget contract are the same for all
///         of them, and a second implementation of those is how two lanes come to disagree about whether a row is
///         still being driven.
///     </para>
///     <para>
///         Nothing instantiates this in the run engine yet — the agent lane that does lands in the next phase, and the
///         tool lane after it. Its contract is asserted directly instead.
///     </para>
/// </summary>
internal sealed class GraphWorkflowInFlightLane<TResult> : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, GraphWorkflowInFlight<TResult>> _inflight = new();
    private readonly SemaphoreSlim _lane;
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public GraphWorkflowInFlightLane(int slots)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        _lane = new SemaphoreSlim(slots, slots);
    }

    /// <summary>Whether this node run's work is being driven right now, or has landed and not yet been consumed.</summary>
    public bool IsInFlight(Guid nodeRunId) =>
        _inflight.ContainsKey(nodeRunId);

    public bool TryGet(Guid nodeRunId, [NotNullWhen(true)] out GraphWorkflowInFlight<TResult>? flight) =>
        _inflight.TryGetValue(nodeRunId, out flight);

    /// <summary>
    ///     Takes a slot and starts <paramref name="work" />, or answers <see langword="null" /> when the lane is full.
    ///     <para>
    ///         The slot is taken BEFORE the work starts and released when it ends, whatever it ends as, so a throw
    ///         inside the caller's task cannot leak one. A full lane is queueing rather than failure: nothing is
    ///         written, and the next tick asks again.
    ///     </para>
    /// </summary>
    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to the in-flight entry, which outlives this call by design: Consume disposes it "
                        + "when the settle has committed, Discard disposes it once the work has unwound, and DisposeAsync "
                        + "disposes whatever is left. Disposing here would cancel the work that was just started.")]
    public async Task<GraphWorkflowInFlight<TResult>?> TryStartAsync(Guid nodeRunId,
        int attempt,
        Guid invocationId,
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        // ponytail: a zero-timeout wait, so a full lane costs a tick rather than a parked thread. What this does NOT
        // bound is how long a row may sit Queued once it is in flight and waiting on a node-wide slot further down —
        // bound the lease wait, or stamp a queued-at instant and expire on it, if that ever measures.
        if (!await _lane.WaitAsync(millisecondsTimeout: 0, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        var flight = new GraphWorkflowInFlight<TResult>(cancellation, RunAsync(work, cancellation.Token), attempt, invocationId, new StrongBox<bool>(value: false));
        if (_inflight.TryAdd(nodeRunId, flight))
        {
            return flight;
        }

        // Something is already being driven for this row. The caller checks that first; reaching here means it raced
        // itself, and the entry that won is the one the poll will settle.
        await cancellation.CancelAsync().ConfigureAwait(false);
        return null;
    }

    /// <summary>
    ///     Asks the work to stop, answering whether it actually asked.
    ///     <para>
    ///         <see langword="false" /> on a repeat, and that is the whole point rather than tidiness: the entry lives
    ///         until a poll SEES the work land, so a cancelling drain reaches this every tick until then. The caller
    ///         counts a <see langword="true" /> as a written transition and the dispatcher re-signals itself after any
    ///         productive tick, so answering <see langword="true" /> each time would spin the drain for the whole
    ///         duration of the work.
    ///     </para>
    ///     <para>
    ///         ponytail: the ceiling that buys is up to one <c>DispatchIntervalMilliseconds</c> sweep before a stopped
    ///         turn is noticed, where the spin noticed immediately. Signalling from the work's own continuation would
    ///         mean injecting the dispatcher into the lane that the dispatcher already takes. Break the cycle — a
    ///         settable signal, or a completion channel — if that latency ever measures.
    ///     </para>
    /// </summary>
    public async Task<bool> StopAsync(Guid nodeRunId)
    {
        if (!_inflight.TryGetValue(nodeRunId, out var flight) || flight.Cancellation.IsCancellationRequested)
        {
            return false;
        }

        await flight.Cancellation.CancelAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     Drops the entry and cancels its work without waiting for the unwind — awaiting it here would hold the
    ///     dispatcher's advance gate, and with it every other run, for as long as the work takes to notice.
    ///     <para>
    ///         Removing the entry is the load-bearing half, not the cancel: a row settled with its entry left behind
    ///         would refuse the next attempt its place in the registry, and that attempt would then run with nothing
    ///         polling it.
    ///     </para>
    /// </summary>
    public async Task DiscardAsync(Guid nodeRunId)
    {
        if (!_inflight.TryRemove(nodeRunId, out var flight))
        {
            return;
        }

        await flight.Cancellation.CancelAsync().ConfigureAwait(false);
        _ = DisposeWhenDoneAsync(flight);
    }

    /// <summary>
    ///     Consumes a landed entry, once its settle has COMMITTED. Doing it before the write would spend the result on
    ///     a write that may throw, and the next poll would then find no entry and report "the host stopped" about work
    ///     that finished perfectly.
    /// </summary>
    public void Consume(Guid nodeRunId)
    {
        if (_inflight.TryRemove(nodeRunId, out var flight))
        {
            flight.Cancellation.Dispose();
        }
    }

    /// <summary>
    ///     Drops every entry whose row has moved on — re-attempted, cancelled, or otherwise no longer this lane's to
    ///     settle. Called once a tick before anything is polled, because a retry reaches a row WITHOUT coming through
    ///     the lane that is driving it.
    /// </summary>
    public async Task ForgetSupersededAsync(IReadOnlyList<GraphWorkflowNodeRunSnapshot> nodeRuns)
    {
        ArgumentNullException.ThrowIfNull(nodeRuns);

        foreach (var nodeRun in nodeRuns)
        {
            if (_inflight.TryGetValue(nodeRun.Id, out var flight)
                && (flight.Attempt != nodeRun.Attempt
                    || nodeRun.Status is not (GraphWorkflowNodeRunStatus.Queued or GraphWorkflowNodeRunStatus.Running)))
            {
                await DiscardAsync(nodeRun.Id).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) == 1)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var flight in _inflight.Values)
        {
            await SwallowAsync(flight.Work).ConfigureAwait(false);
            flight.Cancellation.Dispose();
        }

        _inflight.Clear();
        _shutdown.Dispose();
        _lane.Dispose();
    }

    /// <summary>The caller's work, with the slot released whatever it ends as.</summary>
    private async Task<TResult> RunAsync(Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken)
    {
        try
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _lane.Release();
        }
    }

    private static async Task DisposeWhenDoneAsync(GraphWorkflowInFlight<TResult> flight)
    {
        await SwallowAsync(flight.Work).ConfigureAwait(false);
        flight.Cancellation.Dispose();
    }

    /// <summary>A discarded result is about work the run has decided to replace, so how it ended is not news.</summary>
    private static async Task SwallowAsync(Task work)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Every outcome of a discarded entry is deliberately unread, including this one.
        }
    }
}
