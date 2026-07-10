namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

/// <summary>
///     Applies a bounded, pre-first-token retry (exponential backoff with jitter) and a per-endpoint circuit breaker
///     around a streaming provider send. Retries are attempted ONLY before the first item is yielded — once any chunk
///     has been produced a retry could duplicate streamed output, so the stream is drained without further retry from
///     that point on.
/// </summary>
public interface IProviderStreamResilience
{
    /// <summary>
    ///     Enumerates the stream produced by <paramref name="streamFactory" />, transparently re-invoking the factory
    ///     for a fresh attempt when the send fails transiently before the first chunk. <paramref name="endpointKey" />
    ///     scopes the circuit breaker to one provider endpoint. The supplied <paramref name="cancellationToken" /> is
    ///     honoured throughout; a cancellation requested through it is never treated as a transient failure and is never
    ///     retried.
    /// </summary>
    IAsyncEnumerable<T> ExecuteStreamingAsync<T>(string endpointKey,
        Func<CancellationToken, IAsyncEnumerable<T>> streamFactory,
        CancellationToken cancellationToken);
}
