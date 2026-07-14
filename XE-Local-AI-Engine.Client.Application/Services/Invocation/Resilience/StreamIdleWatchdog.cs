namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.Client.Common.Telemetry;

/// <summary>
///     Raised when a streamed provider response stalls: no chunk arrived within the configured inter-chunk idle
///     window. Derives from <see cref="TimeoutException" /> so the invocation runner's failure classifier maps it to
///     the timeout failure category without a bespoke arm. The message names the timeout that fired and carries no
///     paths, URLs, or provider internals.
/// </summary>
public sealed class StreamIdleTimeoutException : TimeoutException
{
    public StreamIdleTimeoutException(string message)
        : base(message)
    {
    }
}

/// <summary>
///     Inter-chunk idle watchdog for a streamed <see cref="IAsyncEnumerable{T}" />. Bounds the gap BETWEEN yielded
///     items (the time the provider takes to produce the next chunk); it deliberately does not bound the total stream
///     duration (the invocation-level timeout owns that) nor the consumer's own processing/transport time between
///     chunks. Mirrors the per-event idle clock the orchestration session already uses so both streaming paths enforce
///     an inter-chunk stall the same way.
///     <para>
///         The idle bound is a WALL-CLOCK bound a non-cooperative provider cannot defeat: each pull races the provider's
///         <c>MoveNextAsync</c> against a <see cref="Task.Delay(TimeSpan, CancellationToken)" /> deadline, so the wait
///         returns at the deadline even when the enumerator ignores the cancellation token and never returns. On timeout
///         the provider is asked to stop and given a bounded grace to unwind; a provider that honours it unwinds cleanly,
///         while one that ignores it is ABANDONED — its stuck operation is left running but observed off-thread (so it
///         never surfaces as an unobserved-task fault), disposal is bounded the same way, and the abandonment is recorded
///         on <see cref="NodeMetrics.ChatStreamProviderAbandonedTotal" />. Because the iterator terminates on timeout, any
///         late item the abandoned enumerator eventually produces can never reach the consumer.
///     </para>
/// </summary>
internal static class StreamIdleWatchdog
{
    /// <summary>
    ///     After an idle timeout (or outer cancellation) the provider is asked to stop; this is how long it is then given
    ///     to honour cancellation — for its stuck <c>MoveNextAsync</c> to unwind and, separately, for a <c>DisposeAsync</c>
    ///     to complete — before the enumerator is abandoned. Kept small so a wedged provider cannot hold the pipeline for
    ///     long, but non-zero so a cooperative provider unwinds cleanly and is not misreported as abandoned.
    /// </summary>
    private static readonly TimeSpan DefaultAbandonmentGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Enumerates the stream built by <paramref name="streamFactory" /> so that if more than
    ///     <paramref name="idleTimeout" /> elapses waiting for the next item, the send is cancelled and a
    ///     <see cref="StreamIdleTimeoutException" /> carrying <paramref name="timeoutMessage" /> is thrown. The factory
    ///     receives the watchdog's own linked token so that expiry actually cancels the underlying provider call (a
    ///     token handed to <c>GetAsyncEnumerator</c> alone would not, since the provider stream binds cancellation via
    ///     its method argument). Crucially the wait is wall-clock-bounded, so a provider that IGNORES that token still
    ///     hits the deadline. A non-positive <paramref name="idleTimeout" /> disables the watchdog (pass-through).
    ///     Outer cancellation via <paramref name="cancellationToken" /> propagates as an ordinary
    ///     <see cref="OperationCanceledException" /> and is never reported as an idle timeout.
    /// </summary>
    /// <param name="abandonmentGrace">
    ///     Overrides <see cref="DefaultAbandonmentGrace" /> (used by tests to keep the abandon path fast). When
    ///     <see langword="null" /> or non-positive the default is used.
    /// </param>
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
            await foreach (var item in streamFactory(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }

            yield break;
        }

        // The provider stream binds cancellation via the token handed to the factory; cancelling providerCts is the
        // cooperative signal to stop. But a provider that IGNORES that token can leave MoveNextAsync/DisposeAsync pending
        // forever, so the waits below are wall-clock-bounded and a stuck operation is abandoned (never awaited inline)
        // rather than trusted to return. Disposal is therefore managed manually (no `await using`), so a hung DisposeAsync
        // cannot wedge the pipeline.
        using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = streamFactory(providerCts.Token).GetAsyncEnumerator(providerCts.Token);
        var disposalHandedOff = false;
        try
        {
            var awaitingFirstChunk = true;
            while (true)
            {
                // Observe outer cancellation BEFORE advancing: a stream whose MoveNextAsync always completes
                // synchronously (a pre-buffered enumerator) never reaches the wall-clock race below, so without this it
                // could emit past a cancel forever. The idle bound is a per-wait timer, and a zero-wait synchronous chunk
                // has no idle gap to exceed, so only cancellation is checked on this path.
                cancellationToken.ThrowIfCancellationRequested();

                var moveNext = enumerator.MoveNextAsync();

                // Fast path: a buffered chunk completes synchronously and successfully — take it without allocating a
                // Task or timer. A synchronous fault/cancel is NOT consumed here; it falls through to AsTask() below and
                // is rethrown when awaited (consuming the ValueTask exactly once on that path).
                if (moveNext.IsCompletedSuccessfully)
                {
                    if (!moveNext.Result)
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

                // The pull did not complete synchronously. Project it to a Task ONCE (it may need to outlive this wait if
                // abandoned) and hand it to the wall-clock-bounded race helper (a non-iterator method so its own `using`
                // is analysed normally). The helper never yields — it returns the outcome and, on abandonment, whether it
                // took ownership of the enumerator's disposal.
                var outcome = await PullNextAsync(enumerator,
                    moveNext.AsTask(),
                    idleTimeout,
                    abandonmentGrace,
                    awaitingFirstChunk,
                    providerCts,
                    cancellationToken).ConfigureAwait(false);

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
            // Normal completion, a consumer break, or a timeout whose provider unwound cooperatively: dispose within a
            // bound so a hung DisposeAsync cannot wedge the pipeline. When disposal was handed to an abandonment cleanup
            // it owns the enumerator (which may still be mid-MoveNextAsync), so do not touch it here.
            if (!disposalHandedOff)
            {
                var disposeTask = enumerator.DisposeAsync().AsTask();
                if (!await WaitBoundedAsync(disposeTask, abandonmentGrace).ConfigureAwait(false))
                {
                    Observe(disposeTask);
                    NodeMetrics.ChatStreamProviderAbandonedTotal.Add(1);
                }
            }
        }
    }

    /// <summary>
    ///     Races one provider pull (<paramref name="moveTask" />) against a wall-clock idle deadline. Returns
    ///     <see cref="PullStatus.Advanced" /> / <see cref="PullStatus.Completed" /> when the pull wins in time (the caller
    ///     may then read <c>enumerator.Current</c>); on the deadline it asks the provider to stop, gives it
    ///     <paramref name="abandonmentGrace" /> to unwind, records and (via <see cref="AbandonAsync" />) hands off a
    ///     non-cooperative provider, emits the watchdog metric, and returns <see cref="PullStatus.IdleTimedOut" /> — or
    ///     <see cref="PullStatus.OuterCancelled" /> when <paramref name="cancellationToken" /> is what fired.
    /// </summary>
    private static async Task<PullOutcome> PullNextAsync<T>(IAsyncEnumerator<T> enumerator,
        Task<bool> moveTask,
        TimeSpan idleTimeout,
        TimeSpan abandonmentGrace,
        bool awaitingFirstChunk,
        CancellationTokenSource providerCts,
        CancellationToken cancellationToken)
    {
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var idleDelay = Task.Delay(idleTimeout, idleCts.Token);
        var winner = await Task.WhenAny(moveTask, idleDelay).ConfigureAwait(false);
        if (ReferenceEquals(winner, moveTask))
        {
            // A chunk (or a provider fault) arrived within the window. Cancel + observe the idle delay so its
            // cancellation cannot surface as an unobserved-task fault, then consume the result — this rethrows a provider
            // fault, and an outer-cancellation OperationCanceledException flows straight out to the caller.
            await idleCts.CancelAsync().ConfigureAwait(false);
            Observe(idleDelay);
            var moved = await moveTask.ConfigureAwait(false);
            return new PullOutcome(moved ? PullStatus.Advanced : PullStatus.Completed, DisposalHandedOff: false);
        }

        // The deadline (or outer cancellation) won the race — we are done with this enumerator either way.
        var idleFired = !cancellationToken.IsCancellationRequested;

        // Ask the provider to stop, then give it a bounded grace to honour cancellation. A cooperative provider unwinds
        // MoveNextAsync within the grace (a clean timeout); a non-cooperative one does not and is abandoned.
        await providerCts.CancelAsync().ConfigureAwait(false);
        var settled = await WaitBoundedAsync(moveTask, abandonmentGrace).ConfigureAwait(false);
        var disposalHandedOff = false;
        if (settled)
        {
            Observe(moveTask);
        }
        else
        {
            // Non-cooperative: leave MoveNextAsync running and hand BOTH its observation and the enumerator's (bounded)
            // disposal to an off-thread cleanup — we must not dispose while a MoveNextAsync is still pending (an
            // IAsyncEnumerator contract violation).
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
        var winner = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (ReferenceEquals(winner, task))
        {
            await delayCts.CancelAsync().ConfigureAwait(false);
            Observe(delay);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Abandons a stuck provider pull: observes <paramref name="moveTask" /> off-thread (so its eventual fault, if any,
    ///     is not unobserved) and, only once it has settled (never concurrently with the pending pull), disposes the
    ///     <paramref name="enumerator" /> within <paramref name="grace" />. Returns immediately; the cleanup runs detached.
    ///     If the pull never settles the enumerator is never disposed — the documented cost of bounding a provider that
    ///     ignores cancellation (its native resources may leak until, if ever, it returns).
    /// </summary>
    private static void AbandonAsync<T>(Task<bool> moveTask, IAsyncEnumerator<T> enumerator, TimeSpan grace)
    {
        _ = CleanupAsync(moveTask, enumerator, grace);

        static async Task CleanupAsync(Task<bool> pending, IAsyncEnumerator<T> enumerator, TimeSpan grace)
        {
            try
            {
                _ = await pending.ConfigureAwait(false);
            }
            catch
            {
                // The abandoned round's outcome is irrelevant; awaiting it only prevents an unobserved-task fault.
            }

            try
            {
                var disposeTask = enumerator.DisposeAsync().AsTask();
                if (!await WaitBoundedAsync(disposeTask, grace).ConfigureAwait(false))
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
