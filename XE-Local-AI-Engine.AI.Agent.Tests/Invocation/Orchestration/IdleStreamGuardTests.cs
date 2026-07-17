namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation.Orchestration;

using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Wall-clock guarantees of <see cref="IdleStreamGuard" /> — the AI.Agent-layer idle bound the orchestration run
///     session wraps its <c>WatchStreamAsync</c> drain and disposal in. Driven directly against cancellation-ignoring
///     fakes (the concrete MAF <c>StreamingRun</c> cannot be faked), which is the seam that carries the new logic.
/// </summary>
public sealed class IdleStreamGuardTests
{
    [Test]
    public async Task GuardAsync_WhenProviderIgnoresIdleToken_TimesOutWithoutLeakingLateEventsAndRecordsAbandonment()
    {
        var idleTimeouts = 0;
        var abandonments = 0;
        var provider = new StallingStream(cooperative: false);
        var collected = new List<int>();

        using var idleCts = new CancellationTokenSource();
        var context = new IdleGuardContext(TimeSpan.FromMilliseconds(100),
            () => idleTimeouts++,
            () => abandonments++,
            idleCts.Token,
            CancellationToken.None);

        async Task Consume()
        {
            await foreach (var evt in IdleStreamGuard.GuardAsync<int>(provider.CreateEnumerator, context).ConfigureAwait(false))
            {
                collected.Add(evt);
            }
        }

        // Arm the idle deadline; the first event arrives at once, the second pull blocks in MoveNextAsync ignoring the
        // token. The wall-clock race must still fire and complete the enumeration (throwing) despite that.
        idleCts.CancelAfter(TimeSpan.FromMilliseconds(100));
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => Consume().WaitAsync(TimeSpan.FromSeconds(10)));

        AssertEx.Equal(expected: 1, collected.Count);
        AssertEx.Equal(expected: 1, collected[0]);
        AssertEx.Equal(expected: 1, idleTimeouts);
        AssertEx.Equal(expected: 1, abandonments);

        // Releasing the stuck pull produces a late event; the terminated stream must never surface it, and the
        // abandonment cleanup must dispose the enumerator.
        provider.ReleaseStuckMoveNext();
        await provider.Disposed.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEx.False(collected.Contains(2), "a late event from the abandoned workflow must never reach the consumer");
    }

    [Test]
    public async Task GuardAsync_WhenProviderHonoursCancellation_TimesOutButDoesNotRecordAbandonment()
    {
        var idleTimeouts = 0;
        var abandonments = 0;
        var provider = new StallingStream(cooperative: true);
        var collected = new List<int>();

        using var idleCts = new CancellationTokenSource();
        var context = new IdleGuardContext(TimeSpan.FromMilliseconds(100),
            () => idleTimeouts++,
            () => abandonments++,
            idleCts.Token,
            CancellationToken.None);

        async Task Consume()
        {
            await foreach (var evt in IdleStreamGuard.GuardAsync<int>(provider.CreateEnumerator, context).ConfigureAwait(false))
            {
                collected.Add(evt);
            }
        }

        idleCts.CancelAfter(TimeSpan.FromMilliseconds(100));
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => Consume().WaitAsync(TimeSpan.FromSeconds(10)));

        // A cooperative workflow unwinds within the grace, so it is a clean idle timeout — NOT an abandonment.
        AssertEx.Equal(expected: 1, idleTimeouts);
        AssertEx.Equal(expected: 0, abandonments);
        AssertEx.Equal(expected: 1, collected.Count);
    }

    [Test]
    public async Task GuardAsync_WhenOuterCancelled_ThrowsWithoutRecordingAnIdleTimeout()
    {
        var idleTimeouts = 0;
        var abandonments = 0;
        var provider = new StallingStream(cooperative: true);

        using var outerCts = new CancellationTokenSource();
        // Mirror the session: the idle CTS is linked to the outer token, so outer cancellation fires both.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);
        var context = new IdleGuardContext(TimeSpan.FromMilliseconds(100),
            () => idleTimeouts++,
            () => abandonments++,
            idleCts.Token,
            outerCts.Token);

        async Task Consume()
        {
            await foreach (var _ in IdleStreamGuard.GuardAsync<int>(provider.CreateEnumerator, context).ConfigureAwait(false))
            {
                // Cancel the outer token once the run is under way (the first event has been pulled).
                await outerCts.CancelAsync();
            }
        }

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => Consume().WaitAsync(TimeSpan.FromSeconds(10)));

        // Outer cancellation is a plain cancellation, not a stall — the idle-timeout metric must NOT be emitted.
        AssertEx.Equal(expected: 0, idleTimeouts);
    }

    [Test]
    public async Task GuardAsync_WhenEnumeratorDisposeHangs_BoundsDisposalAndRecordsAbandonment()
    {
        var idleTimeouts = 0;
        var abandonments = 0;
        var collected = new List<int>();

        var context = new IdleGuardContext(TimeSpan.FromMilliseconds(100),
            () => idleTimeouts++,
            () => abandonments++,
            CancellationToken.None,
            CancellationToken.None);

        async Task Consume()
        {
            await foreach (var evt in IdleStreamGuard.GuardAsync<int>(_ => new HangingDisposeEnumerator(), context).ConfigureAwait(false))
            {
                collected.Add(evt);
            }
        }

        // The stream ends immediately (no idle timeout), but DisposeAsync never completes — the guard's finally must
        // bound it and record the abandonment rather than hang.
        await Consume().WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Empty(collected);
        AssertEx.Equal(expected: 0, idleTimeouts);
        AssertEx.Equal(expected: 1, abandonments);
    }

    [Test]
    public async Task DisposeBoundedAsync_WhenDisposeNeverCompletes_ReturnsFalseWithinGrace()
    {
#pragma warning disable CA2000 // Ownership passes to DisposeBoundedAsync (the method under test); its DisposeAsync never completes, so it cannot be awaited or disposed here.
        var disposable = new HangingDisposable();
#pragma warning restore CA2000

        var completed = await IdleStreamGuard.DisposeBoundedAsync(disposable, TimeSpan.FromMilliseconds(100))
                                             .WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.False(completed, "a DisposeAsync that never completes must be bounded and reported as not-completed");
    }

    [Test]
    public async Task DisposeBoundedAsync_WhenDisposeCompletes_ReturnsTrue()
    {
#pragma warning disable CA2000 // Ownership passes to DisposeBoundedAsync (the method under test), which disposes it.
        var disposable = new CompletingDisposable();
#pragma warning restore CA2000

        var completed = await IdleStreamGuard.DisposeBoundedAsync(disposable, TimeSpan.FromSeconds(5));

        AssertEx.True(completed, "a prompt DisposeAsync must be reported as completed");
        AssertEx.True(disposable.Disposed, "the disposable must actually have been disposed");
    }

    [Test]
    public async Task GuardAsync_WhenBufferedStreamCompletesSynchronously_StopsEmittingAfterOuterCancellation()
    {
        var idleTimeouts = 0;
        var abandonments = 0;
        var collected = new List<int>();

        using var outerCts = new CancellationTokenSource();
        // Mirror the session: the idle CTS is linked to the outer token.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(outerCts.Token);
        var context = new IdleGuardContext(TimeSpan.FromMilliseconds(100),
            () => idleTimeouts++,
            () => abandonments++,
            idleCts.Token,
            outerCts.Token);

        async Task Consume()
        {
            // Every MoveNextAsync completes synchronously, so this stream never reaches the async race — the fast path's
            // cancellation check is the only thing that can stop it.
            await foreach (var evt in IdleStreamGuard.GuardAsync<int>(_ => new SynchronousBufferedEnumerator(500), context).ConfigureAwait(false))
            {
                collected.Add(evt);
                if (collected.Count == 3)
                {
                    await outerCts.CancelAsync();
                }
            }
        }

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => Consume().WaitAsync(TimeSpan.FromSeconds(10)));

        // The item after cancellation must never be emitted, and outer cancellation is not an idle timeout.
        AssertEx.Equal(expected: 3, collected.Count);
        AssertEx.Equal(expected: 0, idleTimeouts);
        AssertEx.Equal(expected: 0, abandonments);
    }

    [Test]
    public async Task GuardAsync_WhenBufferedStreamCompletesSynchronously_StopsEmittingAfterIdleDeadline()
    {
        var idleTimeouts = 0;
        var abandonments = 0;
        var collected = new List<int>();

        using var idleCts = new CancellationTokenSource();
        var context = new IdleGuardContext(TimeSpan.FromMilliseconds(100),
            () => idleTimeouts++,
            () => abandonments++,
            idleCts.Token,
            CancellationToken.None);

        async Task Consume()
        {
            await foreach (var evt in IdleStreamGuard.GuardAsync<int>(_ => new SynchronousBufferedEnumerator(500), context).ConfigureAwait(false))
            {
                collected.Add(evt);
                if (collected.Count == 3)
                {
                    // Fire the idle deadline directly (a synchronous stream never yields the thread for a timer to run).
                    await idleCts.CancelAsync();
                }
            }
        }

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => Consume().WaitAsync(TimeSpan.FromSeconds(10)));

        // The deadline is observed in the fast path before the next item — no further emission, and it counts as a timeout.
        AssertEx.Equal(expected: 3, collected.Count);
        AssertEx.Equal(expected: 1, idleTimeouts);
        AssertEx.Equal(expected: 0, abandonments);
    }

    // An enumerator whose MoveNextAsync ALWAYS completes synchronously (a pre-buffered stream, items 1..limit), ignoring
    // any token — it never reaches the guard's async race, so it proves the fast-path cancellation/deadline check.
    private sealed class SynchronousBufferedEnumerator(int limit) : IAsyncEnumerator<int>
    {
        private int _index;

        public int Current { get; private set; }

        public ValueTask<bool> MoveNextAsync()
        {
            if (_index >= limit)
            {
                return ValueTask.FromResult(false);
            }

            _index++;
            Current = _index;
            return ValueTask.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    // Yields one event then blocks the next MoveNextAsync. A cooperative enumerator observes its bound token and unwinds
    // when the guard cancels it, whereas a non-cooperative one ignores the token so only the test release unblocks it.
    // The controller is non-disposable; the disposable enumerator it creates is owned by the guard.
    private sealed class StallingStream(bool cooperative)
    {
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Disposed => _disposed.Task;

        public void ReleaseStuckMoveNext()
        {
            _release.TrySetResult(true);
        }

        public IAsyncEnumerator<int> CreateEnumerator(CancellationToken cancellationToken)
        {
            return new Enumerator(_release.Task, _disposed, cooperative, cancellationToken);
        }

        private sealed class Enumerator(Task<bool> release, TaskCompletionSource disposed, bool cooperative, CancellationToken token) : IAsyncEnumerator<int>
        {
            private int _index;

            public int Current { get; private set; }

            public async ValueTask<bool> MoveNextAsync()
            {
                _index++;
                if (_index == 1)
                {
                    Current = 1;
                    return true;
                }

                if (_index == 2)
                {
                    if (cooperative)
                    {
                        // Respect the bound token: the guard cancelling it unblocks this pull promptly.
                        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    }
                    else
                    {
                        // Ignore the token entirely: only the test's release unblocks it.
                        _ = await release.ConfigureAwait(false);
                    }

                    Current = 2;
                    return true;
                }

                return false;
            }

            public ValueTask DisposeAsync()
            {
                disposed.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }
    }

    // Ends immediately at the first pull, but its DisposeAsync never completes — exercises the guard's bounded disposal.
    private sealed class HangingDisposeEnumerator : IAsyncEnumerator<int>
    {
        private readonly TaskCompletionSource _neverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Current => 0;

        public ValueTask<bool> MoveNextAsync()
        {
            return ValueTask.FromResult(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _neverCompletes.Task.ConfigureAwait(false);
        }
    }

    private sealed class HangingDisposable : IAsyncDisposable
    {
        private readonly TaskCompletionSource _neverCompletes = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DisposeAsync()
        {
            await _neverCompletes.Task.ConfigureAwait(false);
        }
    }

    private sealed class CompletingDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
