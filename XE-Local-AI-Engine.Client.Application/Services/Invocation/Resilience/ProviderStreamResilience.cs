namespace XE_Local_AI_Engine.Client.Services.Invocation.Resilience;

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Invocation;

/// <inheritdoc cref="IProviderStreamResilience" />
/// <remarks>
///     A minimal internal implementation rather than a Polly/HTTP-resilience pipeline: the "retry only before the first
///     chunk has been streamed to our transport" gate is application-level state that an HTTP-handler resilience
///     pipeline cannot express (it would also retry mid-stream and duplicate tokens), so the resilience must live at
///     this seam. Failures and the breaker window are tracked per key; the runner supplies the resolved model as the
///     key, so the breaker is effectively per resolved model. The open window is time-based rather than a strict
///     single half-open trial — once it elapses every caller is admitted, so concurrent probes are possible; the runner's
///     single-invocation guard is what bounds real concurrency.
/// </remarks>
internal sealed class ProviderStreamResilience : IProviderStreamResilience
{
    private readonly ConcurrentDictionary<string, BreakerState> _breakers = new(StringComparer.Ordinal);
    private readonly ILogger<ProviderStreamResilience> _logger;
    private readonly ProviderResilienceOptions _options;
    private readonly TimeProvider _timeProvider;

    public ProviderStreamResilience(IOptions<ProviderResilienceOptions> options,
        TimeProvider timeProvider,
        ILogger<ProviderStreamResilience> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IAsyncEnumerable<T> ExecuteStreamingAsync<T>(string endpointKey,
        Func<CancellationToken, IAsyncEnumerable<T>> streamFactory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointKey);
        ArgumentNullException.ThrowIfNull(streamFactory);

        return ExecuteStreamingCoreAsync(endpointKey, streamFactory, cancellationToken);
    }

    private async IAsyncEnumerable<T> ExecuteStreamingCoreAsync<T>(string endpointKey,
        Func<CancellationToken, IAsyncEnumerable<T>> streamFactory,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var breaker = _options.CircuitBreakerEnabled
            ? _breakers.GetOrAdd(endpointKey, static _ => new BreakerState())
            : null;

        if (breaker is not null && IsOpen(breaker))
        {
            throw new ProviderCircuitOpenException(endpointKey);
        }

        // Establish the stream (with pre-first-token retry) OUTSIDE any yield: yield return is illegal inside a
        // try/catch, so the retry loop lives in a helper and returns a live enumerator positioned at the first item.
        var establishment = await EstablishAsync(endpointKey, streamFactory, breaker, cancellationToken).ConfigureAwait(false);
        var enumerator = establishment.Enumerator;

        await using (enumerator.ConfigureAwait(false))
        {
            if (establishment.HasFirst)
            {
                yield return establishment.First!;
            }

            // Past the first chunk the send is live; a mid-stream failure is NOT retried (it would duplicate output).
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                yield return enumerator.Current;
            }
        }
    }

    private async Task<Establishment<T>> EstablishAsync<T>(string endpointKey,
        Func<CancellationToken, IAsyncEnumerable<T>> streamFactory,
        BreakerState? breaker,
        CancellationToken cancellationToken)
    {
        var maxAttempts = _options.RetryEnabled ? Math.Max(val1: 0, _options.MaxRetries) + 1 : 1;
        var attempt = 0;

        while (true)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();

            IAsyncEnumerator<T>? enumerator = null;
            try
            {
                enumerator = streamFactory(cancellationToken).GetAsyncEnumerator(cancellationToken);
                var moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                RecordSuccess(breaker);
                return new Establishment<T>(enumerator, moved, moved ? enumerator.Current : default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // User/timeout cancellation flowing through the invocation token: never retried, never counted.
                await DisposeQuietlyAsync(enumerator).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await DisposeQuietlyAsync(enumerator).ConfigureAwait(false);

                var transient = IsTransient(exception, cancellationToken);
                if (transient)
                {
                    RecordFailure(breaker);
                }

                var canRetry = transient
                               && _options.RetryEnabled
                               && attempt < maxAttempts
                               && (breaker is null || !IsOpen(breaker));

                if (!canRetry)
                {
                    throw;
                }

                ProviderCallBudget.Current?.RecordProviderRetry();
                _logger.LogWarning(exception,
                    "Transient provider send failure for endpoint {Endpoint} (attempt {Attempt} of {MaxAttempts}); retrying after backoff.",
                    endpointKey,
                    attempt,
                    maxAttempts);

                await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var baseMs = Math.Max(val1: 0, _options.BaseDelayMilliseconds);
        if (baseMs == 0)
        {
            return;
        }

        var maxMs = Math.Max(baseMs, _options.MaxDelayMilliseconds);

        // Exponential backoff: the base delay doubled per attempt and capped, then up to fifty percent jitter added to
        // de-correlate concurrent retries against the same endpoint.
        var exponential = baseMs * Math.Pow(x: 2, attempt - 1);
        var capped = Math.Min(maxMs, exponential);
        var jitter = capped * 0.5 * Random.Shared.NextDouble();
        var delay = TimeSpan.FromMilliseconds(Math.Min(maxMs, capped + jitter));

        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    // Transient = the send failed at the connection/transport layer or with a server-side/overload status, so a retry
    // to a (possibly re-spawned) endpoint may succeed. A 4xx other than 429 is a request/capability problem and is
    // deliberately NOT retried. A cancellation requested through the caller's token is never transient.
    private static bool IsTransient(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case SocketException
                {
                    SocketErrorCode: SocketError.ConnectionRefused
                    or SocketError.ConnectionReset
                    or SocketError.ConnectionAborted
                    or SocketError.HostUnreachable
                    or SocketError.NetworkUnreachable
                    or SocketError.TimedOut
                }:
                    return true;

                case HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError }:
                    return true;

                case HttpRequestException httpRequestException:
                    if (httpRequestException.StatusCode is null)
                    {
                        return true;
                    }

                    var statusCode = (int)httpRequestException.StatusCode.Value;
                    if (statusCode >= 500 || statusCode == 429)
                    {
                        return true;
                    }

                    break;

                // An HTTP client timeout surfaces as TaskCanceledException whose token is NOT the caller's (already
                // ruled out above), or as a bare TimeoutException.
                case TaskCanceledException:
                case TimeoutException:
                    return true;

                case AggregateException aggregate:
                    if (aggregate.InnerExceptions.Any(inner => IsTransient(inner, cancellationToken)))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private async ValueTask DisposeQuietlyAsync<T>(IAsyncEnumerator<T>? enumerator)
    {
        if (enumerator is null)
        {
            return;
        }

        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposeException)
        {
            // Best effort disposal. A faulted enumerator can rethrow the transport error while closing, so ignore it
            // and let the original send failure stay the one that propagates.
            _logger.LogTrace(disposeException, "Ignoring provider stream enumerator disposal fault during recovery.");
        }
    }

    private static void RecordSuccess(BreakerState? breaker)
    {
        if (breaker is null)
        {
            return;
        }

        lock (breaker.Gate)
        {
            breaker.ConsecutiveFailures = 0;
            breaker.OpenUntil = null;
        }
    }

    private void RecordFailure(BreakerState? breaker)
    {
        if (breaker is null)
        {
            return;
        }

        var breakDuration = TimeSpan.FromSeconds(Math.Max(val1: 1, _options.CircuitBreakerBreakDurationSeconds));
        var threshold = Math.Max(val1: 1, _options.CircuitBreakerFailureThreshold);
        var now = _timeProvider.GetUtcNow();

        lock (breaker.Gate)
        {
            // A failure by any caller admitted after the break window elapsed re-opens the breaker immediately for a
            // fresh window (the window is time-based, so more than one probe may be in flight).
            if (breaker.OpenUntil is { } openUntil && now >= openUntil)
            {
                breaker.OpenUntil = now + breakDuration;
                return;
            }

            breaker.ConsecutiveFailures++;
            if (breaker.ConsecutiveFailures >= threshold)
            {
                breaker.OpenUntil = now + breakDuration;
            }
        }
    }

    private bool IsOpen(BreakerState breaker)
    {
        lock (breaker.Gate)
        {
            return breaker.OpenUntil is { } openUntil && _timeProvider.GetUtcNow() < openUntil;
        }
    }

    private readonly record struct Establishment<T>(IAsyncEnumerator<T> Enumerator, bool HasFirst, T? First);

    private sealed class BreakerState
    {
        public Lock Gate { get; } = new();

        public long ConsecutiveFailures { get; set; }

        public DateTimeOffset? OpenUntil { get; set; }
    }
}
