namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

using System.Runtime.CompilerServices;

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
/// </summary>
internal static class StreamIdleWatchdog
{
    /// <summary>
    ///     Enumerates the stream built by <paramref name="streamFactory" /> so that if more than
    ///     <paramref name="idleTimeout" /> elapses waiting for the next item, the send is cancelled and a
    ///     <see cref="StreamIdleTimeoutException" /> carrying <paramref name="timeoutMessage" /> is thrown. The factory
    ///     receives the watchdog's own linked token so that expiry actually cancels the underlying provider call (a
    ///     token handed to <c>GetAsyncEnumerator</c> alone would not, since the provider stream binds cancellation via
    ///     its method argument). A non-positive <paramref name="idleTimeout" /> disables the watchdog (pass-through).
    ///     Outer cancellation via <paramref name="cancellationToken" /> propagates as an ordinary
    ///     <see cref="OperationCanceledException" /> and is never reported as an idle timeout.
    /// </summary>
    public static IAsyncEnumerable<T> WithIdleTimeout<T>(Func<CancellationToken, IAsyncEnumerable<T>> streamFactory,
        TimeSpan idleTimeout,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentNullException.ThrowIfNull(timeoutMessage);

        return IterateAsync(streamFactory, idleTimeout, timeoutMessage, cancellationToken);
    }

    private static async IAsyncEnumerable<T> IterateAsync<T>(Func<CancellationToken, IAsyncEnumerable<T>> streamFactory,
        TimeSpan idleTimeout,
        string timeoutMessage,
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

        // The idle clock is a linked CTS whose token is handed to the provider stream as its cancellation argument, then
        // reset immediately before each MoveNext and suspended while the consumer holds a yielded item, so only the
        // provider's inter-chunk gap counts against the budget.
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = streamFactory(idleCts.Token).GetAsyncEnumerator(idleCts.Token);

        while (true)
        {
            idleCts.CancelAfter(idleTimeout);

            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new StreamIdleTimeoutException(timeoutMessage);
            }

            if (!moved)
            {
                yield break;
            }

            // Suspend the clock while the consumer processes/streams this item; a slow transport send must not count as
            // provider idle. It is re-armed at the top of the next iteration before we wait on the provider again.
            idleCts.CancelAfter(Timeout.InfiniteTimeSpan);
            yield return enumerator.Current;
        }
    }
}
