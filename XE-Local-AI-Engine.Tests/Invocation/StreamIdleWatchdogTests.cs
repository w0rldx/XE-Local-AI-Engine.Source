namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Runtime.CompilerServices;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class StreamIdleWatchdogTests
{
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
}
