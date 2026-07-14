namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Tests.Testing;

// The metric-asserting tests capture the node meter globally via a MeterListener; running the whole class serially keeps
// a sibling test's watchdog-timeout/abandonment emission from bleeding into another test's capture window.
[NotInParallel]
public sealed class StreamIdleWatchdogTests
{
    [Test]
    public async Task WithIdleTimeout_WhenProviderIgnoresCancellation_TimesOutWithoutLeakingLateItemsAndRecordsAbandonment()
    {
        using var metrics = new NodeMeterCapture();
        var provider = new IgnoresCancellationStream();
        var collected = new List<int>();

        // The provider yields one chunk, then blocks in MoveNextAsync on an uncancelled gate — ignoring the watchdog's
        // token entirely. With a purely cooperative wait this would hang forever; the wall-clock bound must still fire.
        async Task Consume()
        {
            await foreach (var item in StreamIdleWatchdog.WithIdleTimeout<int>(_ => provider,
                               TimeSpan.FromMilliseconds(100),
                               "stream stalled",
                               CancellationToken.None,
                               abandonmentGrace: TimeSpan.FromMilliseconds(100)))
            {
                collected.Add(item);
            }
        }

        // Completes (throws) well within the cap despite MoveNextAsync never returning.
        var exception = await AssertEx.ThrowsAsync<StreamIdleTimeoutException>(() => Consume().WaitAsync(TimeSpan.FromSeconds(10)));
        AssertEx.Contains(exception.Message, "stream stalled");

        // Only the first chunk was delivered; the abandonment and the inter-chunk watchdog timeout were both recorded.
        AssertEx.Equal(expected: 1, collected.Count);
        AssertEx.Equal(expected: 1, collected[0]);
        AssertEx.Equal(expected: 1, metrics.Count("chat_stream_provider_abandoned_total"));
        AssertEx.Equal(expected: 1, metrics.Count("chat_stream_watchdog_timeout_total"));

        // Release the stuck MoveNextAsync AFTER the timeout: it now produces a late chunk, but the terminated stream must
        // never surface it, and the abandonment cleanup must eventually dispose the enumerator.
        provider.ReleaseStuckMoveNext();
        await provider.Disposed.WaitAsync(TimeSpan.FromSeconds(5));
        AssertEx.False(collected.Contains(2), "a late chunk from the abandoned enumerator must never reach the consumer");
    }

    [Test]
    public async Task WithIdleTimeout_WhenDisposeAsyncHangs_BoundsDisposalAndRecordsAbandonment()
    {
        using var metrics = new NodeMeterCapture();
        var provider = new HangingDisposeStream();
        var collected = new List<int>();

        // The stream ends immediately (no idle timeout), but DisposeAsync never completes. The watchdog's disposal must
        // be bounded so a hung DisposeAsync cannot wedge the pipeline; the abandonment is recorded.
        async Task Consume()
        {
            await foreach (var item in StreamIdleWatchdog.WithIdleTimeout<int>(_ => provider,
                               TimeSpan.FromSeconds(30),
                               "should not fire",
                               CancellationToken.None,
                               abandonmentGrace: TimeSpan.FromMilliseconds(100)))
            {
                collected.Add(item);
            }
        }

        await Consume().WaitAsync(TimeSpan.FromSeconds(10));

        AssertEx.Empty(collected);
        AssertEx.Equal(expected: 1, metrics.Count("chat_stream_provider_abandoned_total"));
        AssertEx.Equal(expected: 0, metrics.Count("chat_stream_watchdog_timeout_total"));
    }

    [Test]
    public async Task WithIdleTimeout_WhenBufferedStreamCompletesSynchronously_StopsEmittingAfterOuterCancellation()
    {
        var collected = new List<int>();
        using var cancellationTokenSource = new CancellationTokenSource();

        // Every MoveNextAsync completes synchronously, so this stream never reaches the wall-clock race — the fast path's
        // cancellation check is the only thing that can stop it emitting.
        async Task Consume()
        {
            await foreach (var item in StreamIdleWatchdog.WithIdleTimeout<int>(_ => new SynchronousBufferedStream(500),
                               TimeSpan.FromSeconds(30),
                               "should not fire",
                               cancellationTokenSource.Token))
            {
                collected.Add(item);
                if (collected.Count == 3)
                {
                    await cancellationTokenSource.CancelAsync();
                }
            }
        }

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => Consume().WaitAsync(TimeSpan.FromSeconds(10)));

        // The chunk after cancellation must never be emitted.
        AssertEx.Equal(expected: 3, collected.Count);
    }

    [Test]
    public async Task WithIdleTimeout_WhenChunksArriveWithinBudget_YieldsAll()
    {
        var items = await CollectAsync(StreamIdleWatchdog.WithIdleTimeout(Fast, TimeSpan.FromSeconds(2), "should not fire", CancellationToken.None));

        AssertEx.Equal(expected: 3, items.Count);
        AssertEx.Equal(expected: 1, items[0]);
        AssertEx.Equal(expected: 3, items[2]);
    }

    [Test]
    public async Task WithIdleTimeout_WhenProviderStallsBetweenChunks_ThrowsStreamIdleTimeout()
    {
        var exception = await AssertEx.ThrowsAsync<StreamIdleTimeoutException>(() =>
            CollectAsync(StreamIdleWatchdog.WithIdleTimeout(OneThenStall, TimeSpan.FromMilliseconds(100), "stream idle fired here", CancellationToken.None)));

        AssertEx.Contains(exception.Message, "stream idle fired here");
    }

    [Test]
    public async Task WithIdleTimeout_WhenOuterTokenCancelled_ThrowsOperationCanceledNotIdleTimeout()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        var stream = StreamIdleWatchdog.WithIdleTimeout(OneThenStall, TimeSpan.FromSeconds(30), "should not fire", cancellationTokenSource.Token);

        await using var enumerator = stream.GetAsyncEnumerator();
        AssertEx.True(await enumerator.MoveNextAsync());
        AssertEx.Equal(expected: 1, enumerator.Current);

        await cancellationTokenSource.CancelAsync();

        // Outer cancellation must surface as a plain OperationCanceledException. StreamIdleTimeoutException does not
        // derive from OperationCanceledException, so a passing ThrowsAsync here proves the idle path was not taken and
        // the runner will classify this as user/invocation cancellation rather than an idle timeout.
        await AssertEx.ThrowsAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
    }

    [Test]
    public async Task WithIdleTimeout_WhenIdleTimeoutNonPositive_IsDisabledPassthrough()
    {
        var items = await CollectAsync(StreamIdleWatchdog.WithIdleTimeout(Fast, TimeSpan.Zero, "disabled", CancellationToken.None));

        AssertEx.Equal(expected: 3, items.Count);
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
        {
            items.Add(item);
        }

        return items;
    }

    private static async IAsyncEnumerable<int> Fast([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var value = 1; value <= 3; value++)
        {
            yield return value;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<int> OneThenStall([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return 1;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield return 2;
    }

    // Yields one chunk, then blocks the next MoveNextAsync on an uncancelled gate — deliberately IGNORING the token, so
    // only a wall-clock bound can unstick it. Releasing the gate later produces a late chunk that must be quarantined.
    // The enumerable is non-disposable; the disposable enumerator is created and handed to the watchdog (which owns it).
    private sealed class IgnoresCancellationStream : IAsyncEnumerable<int>
    {
        private readonly TaskCompletionSource<bool> _stuckMoveNext = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Disposed => _disposed.Task;

        public void ReleaseStuckMoveNext()
        {
            _stuckMoveNext.TrySetResult(true);
        }

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new Enumerator(_stuckMoveNext.Task, _disposed);
        }

        private sealed class Enumerator(Task<bool> stuckMoveNext, TaskCompletionSource disposed) : IAsyncEnumerator<int>
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
                    // No cancellation-token registration at all: the only way out is the test releasing the gate.
                    var moved = await stuckMoveNext.ConfigureAwait(false);
                    Current = 2;
                    return moved;
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

    // Every MoveNextAsync completes synchronously (a pre-buffered stream, chunks 1..limit), ignoring the token — it never
    // reaches the watchdog's wall-clock race, so it proves the fast-path cancellation check.
    private sealed class SynchronousBufferedStream(int limit) : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new Enumerator(limit);
        }

        private sealed class Enumerator(int limit) : IAsyncEnumerator<int>
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
    }

    // Ends immediately (no idle timeout), but its DisposeAsync never completes — the watchdog must bound disposal.
    private sealed class HangingDisposeStream : IAsyncEnumerable<int>
    {
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            return new Enumerator();
        }

        private sealed class Enumerator : IAsyncEnumerator<int>
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
    }

    // Captures increments of the node's counters for the duration of a test so a watchdog timeout / abandonment can be
    // asserted without reaching into private state. Scoped per-test (and the metric tests run NotInParallel on a shared
    // key) so counts from concurrent tests cannot bleed in.
    private sealed class NodeMeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

        public NodeMeterCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, "XE.Node", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
                _counts.AddOrUpdate(instrument.Name, measurement, (_, existing) => existing + measurement));
            _listener.Start();
        }

        public long Count(string instrumentName)
        {
            return _counts.TryGetValue(instrumentName, out var value) ? value : 0;
        }

        public void Dispose()
        {
            _listener.Dispose();
        }
    }
}
