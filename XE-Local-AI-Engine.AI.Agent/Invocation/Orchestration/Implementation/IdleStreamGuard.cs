namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration.Implementation;

using System.Runtime.CompilerServices;

/// <summary>
///     Wraps a streamed <see cref="IAsyncEnumerator{T}" /> with a WALL-CLOCK idle bound a non-cooperative workflow or
///     provider cannot defeat, and takes ownership of the enumerator's disposal.
/// </summary>
/// <remarks>
///     The AI.Agent-layer twin of the application layer's <c>StreamIdleWatchdog</c>; the two sit in separate assemblies
///     by the layer arrow, so the race/abandon/bounded-dispose primitives are deliberately duplicated rather than
///     shared. The deadline is an EXTERNAL, re-armable idle token rather than a fixed per-wait timeout owned here, and
///     a stop surfaces as an ordinary <see cref="OperationCanceledException" />, because this layer cannot reference
///     the typed watchdog exception. See docs/wiki/04-agent-mode.md ("The idle guard: bounding a non-cooperative provider").
/// </remarks>
internal static class IdleStreamGuard
{
    /// <summary>
    ///     How long a provider asked to stop is given to honour cancellation — separately for its stuck
    ///     <c>MoveNextAsync</c> to unwind and for a <c>DisposeAsync</c> to complete — before it is abandoned.
    /// </summary>
    /// <remarks>
    ///     Small, so a wedged workflow cannot hold an invocation or a shutdown for long, but non-zero so a cooperative
    ///     workflow unwinds cleanly and is not misreported.
    /// </remarks>
    public static readonly TimeSpan DefaultAbandonmentGrace = TimeSpan.FromSeconds(5);

    /// <summary>Guards <paramref name="enumeratorFactory" /> against a non-cooperative provider.</summary>
    /// <remarks>
    ///     The factory receives a token linked to the context's outer token ONLY, not the idle deadline, so the
    ///     enumerator still cancels cooperatively while the deadline is enforced by the race. Idle expiry invokes the
    ///     context's idle-timeout callback and throws; outer cancellation throws with no idle signal. Stop semantics
    ///     are strict — a stop is observed before every advancement AND before every yield. See
    ///     docs/wiki/04-agent-mode.md ("The idle guard: bounding a non-cooperative provider").
    /// </remarks>
    public static IAsyncEnumerable<T> GuardAsync<T>(Func<CancellationToken, IAsyncEnumerator<T>> enumeratorFactory, IdleGuardContext context)
    {
        ArgumentNullException.ThrowIfNull(enumeratorFactory);
        if (context.OnIdleTimeout is null || context.OnAbandoned is null)
        {
            throw new ArgumentNullException(nameof(context), "The idle-guard context must supply both the idle-timeout and abandonment callbacks.");
        }

        // CancellationToken.None: the iterator's [EnumeratorCancellation] parameter is filled in at enumeration time
        // from GetAsyncEnumerator / WithCancellation, so a token supplied here would be the one that gets replaced.
        return IterateAsync(enumeratorFactory, context, CancellationToken.None);
    }

    private static async IAsyncEnumerable<T> IterateAsync<T>(Func<CancellationToken, IAsyncEnumerator<T>> enumeratorFactory,
        IdleGuardContext callerContext,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // An enumeration token is folded into BOTH of the guard's own tokens rather than added as a third stop
        // condition, so it cancels the provider AND resolves the per-pull race; an absent or duplicate token is inert.
        using var outerCts = CancellationTokenSource.CreateLinkedTokenSource(callerContext.OuterToken, cancellationToken);
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(callerContext.IdleToken, cancellationToken);
        var context = callerContext with
        {
            OuterToken = outerCts.Token,
            IdleToken = idleCts.Token
        };

        // providerCts is linked to the OUTER token only, and cancelling it is the cooperative stop signal. Keeping it
        // off the idle deadline makes the race deterministic: the idle signal wins, and the pull is cancelled below.
        using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(context.OuterToken);
        var enumerator = enumeratorFactory(providerCts.Token);
        var disposalHandedOff = false;
        try
        {
            while (true)
            {
                // Observe a stop BEFORE advancing: a MoveNextAsync that always completes synchronously never reaches
                // the async race below, so without this it could emit past a cancel or an expired deadline forever.
                ThrowIfStopped(context);

                var moveNext = enumerator.MoveNextAsync();

                // Fast path: a buffered event completes synchronously and successfully — take it without a Task/timer.
                if (moveNext.IsCompletedSuccessfully)
                {
                    if (!await moveNext.ConfigureAwait(false))
                    {
                        yield break;
                    }

                    // Re-observe AFTER the synchronous advancement: a stop seen here halts the stream BEFORE this item is
                    // yielded (an observed stop never emits the pending item).
                    ThrowIfStopped(context);
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
                        // Symmetry with the fast path: a stop that fired in the window between the race resolving and the
                        // yield halts before this item is emitted.
                        ThrowIfStopped(context);
                        yield return enumerator.Current;
                        break;
                }
            }
        }
        finally
        {
            // Dispose within a bound so a hung DisposeAsync cannot wedge the pipeline. When disposal was handed to an
            // abandonment cleanup it owns the enumerator, possibly mid-MoveNextAsync, so do not touch it here.
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

    /// <summary>
    ///     Throws if a stop has already been observed, so the synchronous fast path cannot emit past it.
    /// </summary>
    /// <remarks>
    ///     Outer cancellation takes precedence over an idle deadline — the caller links the idle token to the outer
    ///     one, so both are set on cancellation — and surfaces as a plain
    ///     <see cref="OperationCanceledException" />; an idle deadline fires
    ///     <see cref="IdleGuardContext.OnIdleTimeout" /> exactly as the async race path does.
    /// </remarks>
    private static void ThrowIfStopped(IdleGuardContext context)
    {
        context.OuterToken.ThrowIfCancellationRequested();

        if (context.IdleToken.IsCancellationRequested)
        {
            context.OnIdleTimeout();
            throw new OperationCanceledException(context.IdleToken);
        }
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
            // Non-cooperative: leave MoveNextAsync running and hand its observation AND the enumerator's bounded
            // disposal off-thread — disposing during a pending MoveNextAsync violates the IAsyncEnumerator contract.
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
    ///     Abandons a stuck pull: observes <paramref name="moveTask" /> off-thread and, once it has settled, disposes
    ///     the <paramref name="enumerator" /> within <paramref name="grace" />.
    /// </summary>
    /// <remarks>
    ///     Returns immediately; the cleanup runs detached, and disposal never races the pending pull. If the pull never
    ///     settles the enumerator is never disposed — the documented cost of bounding a provider that ignores
    ///     cancellation.
    /// </remarks>
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
///     The idle-guard's parameters, bundled so no method carries multiple loose <see cref="CancellationToken" />s.
/// </summary>
/// <remarks>
///     The two tokens are kept last so a single loose token would still satisfy the analyzer.
///     <paramref name="OnIdleTimeout" /> fires once when the deadline stops the run and
///     <paramref name="OnAbandoned" /> once per abandoned advancement or disposal.
///     <paramref name="IdleToken" /> is the caller's re-armable deadline, linked by the caller to
///     <paramref name="OuterToken" />, which tells an idle timeout apart from a plain cancellation.
/// </remarks>
internal readonly record struct IdleGuardContext(
    TimeSpan Grace,
    Action OnIdleTimeout,
    Action OnAbandoned,
    CancellationToken IdleToken,
    CancellationToken OuterToken);
