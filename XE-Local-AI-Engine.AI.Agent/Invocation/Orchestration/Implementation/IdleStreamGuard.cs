namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration.Implementation;

/// <summary>
///     Wraps a streamed <see cref="IAsyncEnumerator{T}" /> with a WALL-CLOCK idle bound a non-cooperative workflow /
///     provider cannot defeat — the AI.Agent-layer twin of the application layer's <c>StreamIdleWatchdog</c> (the two
///     live in separate assemblies by the layer arrow, so the small race/abandon/bounded-dispose primitives are
///     deliberately duplicated rather than shared across the boundary). Unlike the watchdog, the idle deadline is not a
///     fixed per-wait timeout owned here: it is an EXTERNAL, re-armable <c>idleToken</c> (the caller's idle CTS, which it
///     resets per event and suspends across an approval pause), and the stopping mechanism is surfaced as an ordinary
///     <see cref="OperationCanceledException" /> (the layer cannot reference the application's typed watchdog exception,
///     and orchestration idle expiry has always surfaced as a cancellation).
///     <para>
///         Each pull races <c>MoveNextAsync</c> against the idle token, so the wait returns at the deadline even when the
///         enumerator ignores its cancellation token and never returns. On expiry the provider is asked to stop and given
///         a bounded grace to unwind; a cooperative one unwinds cleanly, a non-cooperative one is ABANDONED — its stuck
///         pull is left running but observed off-thread (never an unobserved-task fault), disposal is bounded the same
///         way, and the abandonment is reported via <paramref name="onAbandoned" />. Because the iterator terminates on
///         expiry, a late item from an abandoned enumerator can never reach the consumer. This class takes ownership of
///         the enumerator's disposal.
///     </para>
/// </summary>
internal static class IdleStreamGuard
{
    /// <summary>
    ///     After the idle deadline (or outer cancellation) the provider is asked to stop; this is how long it is then
    ///     given to honour cancellation — for its stuck <c>MoveNextAsync</c> to unwind, and separately for a
    ///     <c>DisposeAsync</c> to complete — before it is abandoned. Small so a wedged workflow cannot hold an invocation
    ///     or shutdown for long, but non-zero so a cooperative workflow unwinds cleanly and is not misreported.
    /// </summary>
    public static readonly TimeSpan DefaultAbandonmentGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Guards <paramref name="enumeratorFactory" /> against a non-cooperative provider. The factory receives a token
    ///     linked to <paramref name="outerToken" /> only (NOT the idle deadline), so the enumerator still cancels
    ///     cooperatively on outer cancellation while the idle deadline is enforced by the race rather than by the
    ///     enumerator observing a token. <paramref name="idleToken" /> is the caller's re-armable idle CTS token (fires on
    ///     the idle deadline, and — because the caller links it to <paramref name="outerToken" /> — on outer cancellation
    ///     too). On idle expiry <paramref name="onIdleTimeout" /> is invoked and an <see cref="OperationCanceledException" />
    ///     is thrown; on outer cancellation a plain <see cref="OperationCanceledException" /> is thrown with no idle signal.
    /// </summary>
    public static IAsyncEnumerable<T> GuardAsync<T>(Func<CancellationToken, IAsyncEnumerator<T>> enumeratorFactory, IdleGuardContext context)
    {
        ArgumentNullException.ThrowIfNull(enumeratorFactory);
        if (context.OnIdleTimeout is null || context.OnAbandoned is null)
        {
            throw new ArgumentNullException(nameof(context), "The idle-guard context must supply both the idle-timeout and abandonment callbacks.");
        }

        return IterateAsync(enumeratorFactory, context);
    }

    private static async IAsyncEnumerable<T> IterateAsync<T>(Func<CancellationToken, IAsyncEnumerator<T>> enumeratorFactory, IdleGuardContext context)
    {
        // The enumerator binds cancellation to providerCts (linked to the OUTER token only); cancelling providerCts is
        // the cooperative signal to stop. Keeping it separate from the idle deadline makes the deadline race
        // deterministic — the deadline firing does not itself cancel the pull, so the idle signal reliably wins the race
        // and the pull is only cancelled deliberately, inside the timeout branch.
        using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(context.OuterToken);
        var enumerator = enumeratorFactory(providerCts.Token);
        var disposalHandedOff = false;
        try
        {
            while (true)
            {
                var moveNext = enumerator.MoveNextAsync();

                // Fast path: a buffered event completes synchronously and successfully — take it without a Task/timer.
                if (moveNext.IsCompletedSuccessfully)
                {
                    if (!moveNext.Result)
                    {
                        yield break;
                    }

                    yield return enumerator.Current;
                    continue;
                }

                var outcome = await RaceAsync(enumerator, moveNext.AsTask(), providerCts, context).ConfigureAwait(false);

                if (outcome.DisposalHandedOff)
                {
                    disposalHandedOff = true;
                }

                switch (outcome.Status)
                {
                    case AdvanceStatus.Completed:
                        yield break;
                    case AdvanceStatus.IdleTimedOut:
                        throw new OperationCanceledException(context.IdleToken);
                    case AdvanceStatus.OuterCancelled:
                        throw new OperationCanceledException(context.OuterToken);
                    default:
                        yield return enumerator.Current;
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
                if (!await WaitBoundedAsync(disposeTask, context.Grace).ConfigureAwait(false))
                {
                    Observe(disposeTask);
                    context.OnAbandoned();
                }
            }
        }
    }

    /// <summary>
    ///     Bounds an <see cref="IAsyncDisposable" />'s disposal (e.g. the streaming run itself): returns
    ///     <see langword="true" /> when it completed within <paramref name="grace" />, otherwise observes the hung task
    ///     off-thread and returns <see langword="false" /> without ever blocking past the grace.
    /// </summary>
    public static async Task<bool> DisposeBoundedAsync(IAsyncDisposable disposable, TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(disposable);

        var disposeTask = disposable.DisposeAsync().AsTask();
        if (await WaitBoundedAsync(disposeTask, grace).ConfigureAwait(false))
        {
            await disposeTask.ConfigureAwait(false);
            return true;
        }

        Observe(disposeTask);
        return false;
    }

    private static async Task<AdvanceOutcome> RaceAsync<T>(IAsyncEnumerator<T> enumerator,
        Task<bool> moveTask,
        CancellationTokenSource providerCts,
        IdleGuardContext context)
    {
        using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(context.IdleToken))
        {
            // Completes (cancelled) when the idle deadline fires; a linked CTS so it can be cancelled locally once the
            // pull wins, without touching the caller's shared idle CTS.
            var idleSignal = Task.Delay(Timeout.InfiniteTimeSpan, waitCts.Token);
            var winner = await Task.WhenAny(moveTask, idleSignal).ConfigureAwait(false);
            if (ReferenceEquals(winner, moveTask))
            {
                await waitCts.CancelAsync().ConfigureAwait(false);
                Observe(idleSignal);
                var moved = await moveTask.ConfigureAwait(false);
                return new AdvanceOutcome(moved ? AdvanceStatus.Advanced : AdvanceStatus.Completed, DisposalHandedOff: false);
            }
        }

        // The idle deadline (or outer cancellation) won — we are done with this enumerator either way. Ask the provider
        // to stop, then give it a bounded grace to unwind.
        await providerCts.CancelAsync().ConfigureAwait(false);
        var settled = await WaitBoundedAsync(moveTask, context.Grace).ConfigureAwait(false);
        var disposalHandedOff = false;
        if (settled)
        {
            Observe(moveTask);
        }
        else
        {
            // Non-cooperative: leave MoveNextAsync running and hand its observation AND the enumerator's bounded disposal
            // to an off-thread cleanup — disposing while a MoveNextAsync is pending is an IAsyncEnumerator contract
            // violation.
            disposalHandedOff = true;
            AbandonAsync(moveTask, enumerator, context.Grace);
            context.OnAbandoned();
        }

        if (!context.OuterToken.IsCancellationRequested)
        {
            context.OnIdleTimeout();
            return new AdvanceOutcome(AdvanceStatus.IdleTimedOut, disposalHandedOff);
        }

        return new AdvanceOutcome(AdvanceStatus.OuterCancelled, disposalHandedOff);
    }

    /// <summary>
    ///     Waits for <paramref name="task" /> but no longer than <paramref name="bound" />. Returns <see langword="true" />
    ///     when the task settled within the bound. Never throws the task's exception (the caller decides how to observe
    ///     it) and never leaves the timing delay unobserved.
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
    ///     Abandons a stuck pull: observes <paramref name="moveTask" /> off-thread (so its eventual fault is not
    ///     unobserved) and, only once it has settled (never concurrently with the pending pull), disposes the
    ///     <paramref name="enumerator" /> within <paramref name="grace" />. Returns immediately; the cleanup runs
    ///     detached. If the pull never settles the enumerator is never disposed — the documented cost of bounding a
    ///     provider that ignores cancellation.
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
                // The abandoned pull's outcome is irrelevant; awaiting it only prevents an unobserved-task fault.
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

    private enum AdvanceStatus
    {
        /// <summary>The pull produced an event; <c>enumerator.Current</c> is valid to read.</summary>
        Advanced,

        /// <summary>The pull reported end-of-stream; the enumeration is complete.</summary>
        Completed,

        /// <summary>The idle deadline fired; the run is an idle timeout.</summary>
        IdleTimedOut,

        /// <summary>The outer cancellation token fired; a plain cancellation, not an idle timeout.</summary>
        OuterCancelled
    }

    private readonly record struct AdvanceOutcome(AdvanceStatus Status, bool DisposalHandedOff);
}

/// <summary>
///     The idle-guard's parameters bundled so no method carries multiple loose <see cref="CancellationToken" />s (the two
///     tokens are kept last so a single loose token would still satisfy the analyzer). <paramref name="OnIdleTimeout" />
///     fires once when the idle deadline stops the run; <paramref name="OnAbandoned" /> fires once per abandoned
///     advancement or disposal. <paramref name="IdleToken" /> is the caller's re-armable idle deadline (linked by the
///     caller to <paramref name="OuterToken" />, so it fires on both idle expiry and outer cancellation);
///     <paramref name="OuterToken" /> is the caller's cancellation, used to bind the workflow's own cancellation and to
///     tell an idle timeout apart from a plain cancellation.
/// </summary>
internal readonly record struct IdleGuardContext(
    TimeSpan Grace,
    Action OnIdleTimeout,
    Action OnAbandoned,
    CancellationToken IdleToken,
    CancellationToken OuterToken);
