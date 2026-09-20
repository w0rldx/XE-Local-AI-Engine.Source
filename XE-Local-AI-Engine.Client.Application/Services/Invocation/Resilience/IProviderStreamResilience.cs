namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

/// <summary>
///     Applies a bounded, pre-first-token retry (exponential backoff with jitter) and a per-endpoint circuit breaker
///     around a streaming provider send.
/// </summary>
/// <remarks>
///     Retries are attempted ONLY before the first item is yielded: once any chunk has been produced a retry could
///     duplicate streamed output, so from that point the stream is drained without further retry.
/// </remarks>
public interface IProviderStreamResilience
{
    /// <summary>
    ///     Enumerates the stream produced by <paramref name="streamFactory" />, transparently re-invoking the factory
    ///     for a fresh attempt when the send fails transiently before the first chunk.
    /// </summary>
    /// <remarks>
    ///     <paramref name="endpointKey" /> scopes the circuit breaker to one provider endpoint. A cancellation
    ///     requested through <paramref name="cancellationToken" /> is never transient and is never retried.
    /// </remarks>
    IAsyncEnumerable<T> ExecuteStreamingAsync<T>(string endpointKey,
        Func<CancellationToken, IAsyncEnumerable<T>> streamFactory,
        CancellationToken cancellationToken);
}
