namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.Client.Common.Telemetry;

/// <summary>
///     Inter-chunk idle watchdog for a streamed <see cref="IAsyncEnumerable{T}" />: it bounds the gap BETWEEN yielded
///     items, never the total stream duration (the invocation timeout owns that) nor the consumer's own time.
/// </summary>
/// <remarks>
///     Mirrors the orchestration session's per-event idle clock. A WALL-CLOCK bound a non-cooperative provider cannot
///     defeat: each pull is awaited through <c>WaitAsync(idleTimeout, token)</c>, whose timer fires even when the
///     enumerator ignores cancellation. On timeout the provider is asked to stop within a bounded grace; one that
///     ignores it is ABANDONED, its stuck operation observed off-thread and counted on
///     <see cref="NodeMetrics.ChatStreamProviderAbandonedTotal" />. The iterator terminates, so no late item arrives.
/// </remarks>
internal static class StreamIdleWatchdog
{
    /// <summary>
    ///     How long a provider asked to stop is given to honour cancellation — for its stuck <c>MoveNextAsync</c> to
    ///     unwind and, separately, for a <c>DisposeAsync</c> to complete — before the enumerator is abandoned.
    /// </summary>
    /// <remarks>
    ///     Small, so a wedged provider cannot hold the pipeline for long, but non-zero, so a cooperative one unwinds
    ///     cleanly rather than being misreported as abandoned.
    /// </remarks>
    private static readonly TimeSpan DefaultAbandonmentGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Enumerates the stream built by <paramref name="streamFactory" />, throwing a
    ///     <see cref="StreamIdleTimeoutException" /> carrying <paramref name="timeoutMessage" /> when more than
    ///     <paramref name="idleTimeout" /> elapses waiting for the next item.
    /// </summary>
    /// <remarks>
    ///     The factory receives the watchdog's own linked token, because the provider stream binds cancellation via
    ///     its method argument and a token handed to <c>GetAsyncEnumerator</c> alone would not cancel the send. The
    ///     wait is wall-clock-bounded, so a provider that IGNORES that token still hits the deadline. A non-positive
    ///     <paramref name="idleTimeout" /> disables the watchdog. Outer cancellation propagates as an ordinary
    ///     <see cref="OperationCanceledException" /> and is never reported as an idle timeout.
    /// </remarks>
    /// <param name="abandonmentGrace">Overrides <see cref="DefaultAbandonmentGrace" />; null or non-positive uses it.</param>
    public static IAsyncEnumerable<T> WithIdleTimeout<T>(Func<CancellationToken, IAsyncEnumerable<T>> streamFactory,
        TimeSpan idleTimeout,
        string timeoutMessage,
        CancellationToken cancellationToken,
        TimeSpan? abandonmentGrace = null)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentNullException.ThrowIfNull(timeoutMessage);

        var grace = abandonmentGrace is { } value && value > TimeSpan.Zero ? value : DefaultAbandonmentGrace;
        return IterateAsync(streamFactory, idleTimeout, timeoutMessage, grace, cancellationToken);
    }

    private static async IAsyncEnumerable<T> IterateAsync<T>(Func<CancellationToken, IAsyncEnumerable<T>> streamFactory,
        TimeSpan idleTimeout,
        string timeoutMessage,
        TimeSpan abandonmentGrace,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        if (idleTimeout <= TimeSpan.Zero)
        {
            await foreach (var item in streamFactory(cancellationToken).WithCancellation(cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        // Cancelling providerCts is the cooperative stop signal, but a provider that IGNORES it can leave
        // MoveNextAsync/DisposeAsync pending forever — hence wall-clock waits and manual disposal, never `await using`.
        using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = streamFactory(providerCts.Token).GetAsyncEnumerator(providerCts.Token);
        var disposalHandedOff = false;
        try
        {
            var awaitingFirstChunk = true;
            while (true)
            {
                // Observe outer cancellation BEFORE advancing: a pre-buffered enumerator completes synchronously, never
                // reaches the wait below, and would emit past a cancel. A zero-wait chunk has no idle gap to exceed.
                cancellationToken.ThrowIfCancellationRequested();

                var moveNext = enumerator.MoveNextAsync();
                var completedSynchronously = moveNext.IsCompletedSuccessfully;

                // One projection on every path: the ValueTask is consumed exactly once, and AsTask() over an
                // already-completed ValueTask<bool> returns a cached Task, so the fast path still allocates nothing.
                var pending = moveNext.AsTask();

                // Fast path: a buffered chunk, so no timer and no suspension. A synchronous fault or cancel is not
                // taken here; it is rethrown from the projection on the slow path below.
                if (completedSynchronously)
                {
                    if (!await pending)
                    {
                        yield break;
                    }

                    // Re-observe AFTER the synchronous advancement: a cancel seen here halts the stream before this chunk
                    // is yielded (an observed cancel never emits the pending chunk).
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return enumerator.Current;
                    awaitingFirstChunk = false;
                    continue;
                }

                // Slow path: a non-iterator helper bounds the wait, so it can catch around the await. It reports the
                // outcome and whether, on abandonment, it took ownership of the enumerator's disposal.
                var outcome = await PullNextAsync(enumerator,
                    pending,
                    idleTimeout,
                    abandonmentGrace,
                    awaitingFirstChunk,
                    providerCts,
                    cancellationToken);

                if (outcome.DisposalHandedOff)
                {
                    disposalHandedOff = true;
                }

                switch (outcome.Status)
                {
                    case PullStatus.Completed:
                        yield break;
                    case PullStatus.IdleTimedOut:
                        throw new StreamIdleTimeoutException(timeoutMessage);
                    case PullStatus.OuterCancelled:
                        // Outer cancellation, not an idle timeout: surface a plain OperationCanceledException so the
                        // runner classifies it as user/invocation cancellation rather than a stall.
                        throw new OperationCanceledException(cancellationToken);
                    default:
                        // Symmetry with the fast path: a cancel that fired between the race resolving and the yield
                        // halts before this chunk is emitted.
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return enumerator.Current;
                        awaitingFirstChunk = false;
                        break;
                }
            }
        }
        finally
        {
            // Completion, a consumer break, or a cooperative unwind: dispose within a bound so a hung DisposeAsync
            // cannot wedge the pipeline. An abandonment cleanup owns the enumerator instead — do not touch it here.
            if (!disposalHandedOff)
            {
                var disposeTask = enumerator.DisposeAsync().AsTask();
                if (!await WaitBoundedAsync(disposeTask, abandonmentGrace))
                {
                    Observe(disposeTask);
                    NodeMetrics.ChatStreamProviderAbandonedTotal.Add(1);
                }
            }
        }
    }

    /// <summary>
    ///     Bounds one provider pull (<paramref name="moveTask" />) by a wall-clock idle deadline.
    /// </summary>
    /// <remarks>
    ///     A pull that wins in time answers <see cref="PullStatus.Advanced" /> or
    ///     <see cref="PullStatus.Completed" />, and the caller may read <c>enumerator.Current</c>. On the deadline it
    ///     asks the provider to stop, allows <paramref name="abandonmentGrace" /> to unwind, hands a non-cooperative
    ///     one to <see cref="AbandonAsync" />, emits the watchdog metric and answers
    ///     <see cref="PullStatus.IdleTimedOut" /> — or <see cref="PullStatus.OuterCancelled" /> if the token fired.
    /// </remarks>
    private static async Task<PullOutcome> PullNextAsync<T>(IAsyncEnumerator<T> enumerator,
        Task<bool> moveTask,
        TimeSpan idleTimeout,
        TimeSpan abandonmentGrace,
        bool awaitingFirstChunk,
        CancellationTokenSource providerCts,
        CancellationToken cancellationToken)
    {
        bool idleFired;
        try
        {
            // WaitAsync is the wall-clock bound: its timer fires whether or not the provider honours cancellation, at
            // one timer registration per pull. A chunk or a provider fault inside the window returns or rethrows.
            var moved = await moveTask.WaitAsync(idleTimeout, cancellationToken);
            return new PullOutcome(moved ? PullStatus.Advanced : PullStatus.Completed, DisposalHandedOff: false);
        }
        catch (TimeoutException idleDeadline) when (!IsFaultOf(moveTask, idleDeadline))
        {
            // Our deadline fired. The filter keeps a provider fault that happens to BE a TimeoutException propagating
            // as a provider fault; only our own deadline lands here, and an outer cancel takes precedence over a stall.
            idleFired = !cancellationToken.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Outer cancellation won, through WaitAsync or a cooperative provider; both run the stop/grace/abandon tail
            // so nothing is disposed mid-pull. A provider cancel for its OWN reasons is not caught and propagates.
            idleFired = false;
        }

        // The round is over either way, so ask the provider to stop and give it a bounded grace: a cooperative one
        // unwinds MoveNextAsync inside it (a clean timeout), a non-cooperative one does not and is abandoned.
        await providerCts.CancelAsync();
        var settled = await WaitBoundedAsync(moveTask, abandonmentGrace);
        var disposalHandedOff = false;
        if (settled)
        {
            Observe(moveTask);
        }
        else
        {
            // Non-cooperative: leave MoveNextAsync running and hand both its observation and the enumerator's bounded
            // disposal off-thread. Disposing while a MoveNextAsync is pending violates the IAsyncEnumerator contract.
            disposalHandedOff = true;
            AbandonAsync(moveTask, enumerator, abandonmentGrace);
            NodeMetrics.ChatStreamProviderAbandonedTotal.Add(1);
        }

        if (idleFired)
        {
            NodeMetrics.ChatStreamWatchdogTimeoutTotal.Add(1,
                new KeyValuePair<string, object?>("reason", awaitingFirstChunk ? "no_first_chunk_timeout" : "inter_chunk_stall_timeout"));
            return new PullOutcome(PullStatus.IdleTimedOut, disposalHandedOff);
        }

        return new PullOutcome(PullStatus.OuterCancelled, disposalHandedOff);
    }

    /// <summary>
    ///     Waits for <paramref name="task" /> but no longer than <paramref name="bound" />. Returns <see langword="true" />
    ///     when the task settled within the bound. Never throws the task's exception (the caller decides how to observe it)
    ///     and never leaves the timing delay unobserved.
    /// </summary>
    private static async Task<bool> WaitBoundedAsync(Task task, TimeSpan bound)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        using var delayCts = new CancellationTokenSource();
        var delay = Task.Delay(bound, delayCts.Token);
        var winner = await Task.WhenAny(task, delay);
        if (ReferenceEquals(winner, task))
        {
            await delayCts.CancelAsync();
            Observe(delay);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Abandons a stuck provider pull, returning immediately while a detached cleanup observes
    ///     <paramref name="moveTask" /> and then disposes the <paramref name="enumerator" /> within
    ///     <paramref name="grace" />.
    /// </summary>
    /// <remarks>
    ///     Observing it off-thread keeps its eventual fault from being unobserved, and disposal waits for it to settle
    ///     rather than running concurrently with a pending pull. A pull that never settles leaves the enumerator
    ///     undisposed: the accepted cost of bounding a provider that ignores cancellation, whose native resources may
    ///     leak until, if ever, it returns.
    /// </remarks>
    private static void AbandonAsync<T>(Task<bool> moveTask, IAsyncEnumerator<T> enumerator, TimeSpan grace)
    {
        _ = CleanupAsync(moveTask, enumerator, grace);

        static async Task CleanupAsync(Task<bool> pending, IAsyncEnumerator<T> enumerator, TimeSpan grace)
        {
            try
            {
                _ = await pending;
            }
            catch
            {
                // The abandoned round's outcome is irrelevant; awaiting it only prevents an unobserved-task fault.
            }

            try
            {
                var disposeTask = enumerator.DisposeAsync().AsTask();
                if (!await WaitBoundedAsync(disposeTask, grace))
                {
                    Observe(disposeTask);
                }
            }
            catch
            {
                // A DisposeAsync fault after abandonment is not actionable; swallow so it is not unobserved.
            }
        }
    }

    /// <summary>
    ///     True when <paramref name="exception" /> is the very exception <paramref name="task" /> faulted with: the
    ///     pull's own failure that <c>WaitAsync</c> rethrew, not the deadline <c>WaitAsync</c> raised itself.
    /// </summary>
    /// <remarks>
    ///     Identity, not type, is the discriminator — a provider fault that happens to be a
    ///     <see cref="TimeoutException" /> must stay a provider fault.
    /// </remarks>
    private static bool IsFaultOf(Task task, Exception exception)
    {
        return task.Exception is { } aggregate && aggregate.InnerExceptions.Contains(exception);
    }

    /// <summary>
    ///     Attaches a continuation that retrieves a faulted task's exception so an abandoned task cannot raise an
    ///     unobserved-task fault. A successful or cancelled task carries nothing to observe.
    /// </summary>
    private static void Observe(Task task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = task.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Outcome of one <see cref="PullNextAsync{T}" /> race.</summary>
    private enum PullStatus
    {
        /// <summary>The pull produced a chunk; <c>enumerator.Current</c> is valid to read.</summary>
        Advanced,

        /// <summary>The pull reported end-of-stream; the enumeration is complete.</summary>
        Completed,

        /// <summary>The inter-chunk idle deadline fired; the round is a stall timeout.</summary>
        IdleTimedOut,

        /// <summary>The outer cancellation token fired; the round is a plain cancellation, not a stall.</summary>
        OuterCancelled
    }

    /// <summary>A <see cref="PullStatus" /> plus whether the enumerator's disposal was handed to an abandonment cleanup.</summary>
    private readonly record struct PullOutcome(PullStatus Status, bool DisposalHandedOff);
}
