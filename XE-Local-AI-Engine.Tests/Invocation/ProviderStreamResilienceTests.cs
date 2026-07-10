namespace XE_Local_AI_Engine.Tests.Invocation;

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ProviderStreamResilienceTests
{
    [Test]
    public async Task ExecuteStreamingAsync_WhenTransientThenSuccess_RetriesAndYieldsFullStream()
    {
        var callCount = new StrongBox<int>(value: 0);
        var options = new ProviderResilienceOptions
        {
            MaxRetries = 2,
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        };
        var sut = CreateSut(options);

        var factory = FactoryFailing(failures: 2, callCount, () => Yield(1, 2, 3));
        var result = await CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None));

        AssertEx.Equal(expected: 3, result.Count);
        AssertEx.Equal(expected: 1, result[0]);
        AssertEx.Equal(expected: 3, result[2]);
        AssertEx.Equal(expected: 3, callCount.Value);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenTransientExhaustsRetries_ThrowsUnderlyingFailure()
    {
        var callCount = new StrongBox<int>(value: 0);
        var options = new ProviderResilienceOptions
        {
            MaxRetries = 2,
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        };
        var sut = CreateSut(options);

        var factory = FactoryFailing(failures: int.MaxValue, callCount, () => Yield(1));

        await AssertEx.ThrowsAsync<SocketException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None)));

        // One initial attempt plus the two configured retries.
        AssertEx.Equal(expected: 3, callCount.Value);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenCallerAlreadyCancelled_DoesNotInvokeProviderOrRetry()
    {
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();
        var callCount = new StrongBox<int>(value: 0);
        var options = new ProviderResilienceOptions
        {
            MaxRetries = 5,
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        };
        var sut = CreateSut(options);

        // A transient-throwing factory that must never even be invoked once the caller's token is cancelled.
        var factory = FactoryFailing(failures: int.MaxValue, callCount, () => Yield(1));

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, cancellationTokenSource.Token)));

        AssertEx.Equal(expected: 0, callCount.Value);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenHttpServerError_IsTransientAndRetried()
    {
        var callCount = new StrongBox<int>(value: 0);
        var options = new ProviderResilienceOptions
        {
            MaxRetries = 2,
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        };
        var sut = CreateSut(options);

        var factory = FactoryFailing(failures: 2, callCount, () => Yield(9), () => ThrowHttp(HttpStatusCode.InternalServerError));
        var result = await CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None));

        AssertEx.Equal(expected: 1, result.Count);
        AssertEx.Equal(expected: 3, callCount.Value);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenHttpClientError_IsNotTransientAndNotRetried()
    {
        var callCount = new StrongBox<int>(value: 0);
        var options = new ProviderResilienceOptions
        {
            MaxRetries = 2,
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        };
        var sut = CreateSut(options);

        var factory = FactoryFailing(failures: int.MaxValue, callCount, () => Yield(1), () => ThrowHttp(HttpStatusCode.BadRequest));

        await AssertEx.ThrowsAsync<HttpRequestException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None)));

        AssertEx.Equal(expected: 1, callCount.Value);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenConsecutiveFailuresReachThreshold_OpensBreakerAndFailsFast()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var callCount = new StrongBox<int>(value: 0);
        var options = new ProviderResilienceOptions
        {
            MaxRetries = 0,
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = true,
            CircuitBreakerFailureThreshold = 2,
            CircuitBreakerBreakDurationSeconds = 30
        };
        var sut = CreateSut(options, time);

        var factory = FactoryFailing(failures: int.MaxValue, callCount, () => Yield(1));

        await AssertEx.ThrowsAsync<SocketException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None)));
        await AssertEx.ThrowsAsync<SocketException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None)));

        var callsBeforeOpenSend = callCount.Value;
        await AssertEx.ThrowsAsync<ProviderCircuitOpenException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None)));

        // While open the breaker rejects without invoking the provider factory at all.
        AssertEx.Equal(callsBeforeOpenSend, callCount.Value);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenBreakDurationElapses_HalfOpenTrialSuccessClosesBreaker()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var callCount = new StrongBox<int>(value: 0);
        var succeed = new StrongBox<bool>(value: false);
        var options = new ProviderResilienceOptions
        {
            MaxRetries = 0,
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = true,
            CircuitBreakerFailureThreshold = 2,
            CircuitBreakerBreakDurationSeconds = 30
        };
        var sut = CreateSut(options, time);

        IAsyncEnumerable<int> Factory(CancellationToken _)
        {
            callCount.Value++;
            return succeed.Value ? Yield(7, 8) : ThrowConnectionRefused();
        }

        // Trip the breaker open with two consecutive failures.
        await AssertEx.ThrowsAsync<SocketException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", Factory, CancellationToken.None)));
        await AssertEx.ThrowsAsync<SocketException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", Factory, CancellationToken.None)));
        await AssertEx.ThrowsAsync<ProviderCircuitOpenException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", Factory, CancellationToken.None)));

        // Advance past the break window: the next send is a half-open trial and is allowed through.
        time.Advance(TimeSpan.FromSeconds(31));
        succeed.Value = true;
        var result = await CollectAsync(sut.ExecuteStreamingAsync("endpoint", Factory, CancellationToken.None));

        AssertEx.Equal(expected: 2, result.Count);

        // The successful trial closed the breaker: a following send is admitted, not fast-failed.
        var followUp = await CollectAsync(sut.ExecuteStreamingAsync("endpoint", Factory, CancellationToken.None));
        AssertEx.Equal(expected: 2, followUp.Count);
    }

    private static ProviderStreamResilience CreateSut(ProviderResilienceOptions options, TimeProvider? timeProvider = null)
    {
        return new ProviderStreamResilience(Options.Create(options),
            timeProvider ?? TimeProvider.System,
            NullLogger<ProviderStreamResilience>.Instance);
    }

    private static Func<CancellationToken, IAsyncEnumerable<int>> FactoryFailing(int failures,
        StrongBox<int> callCount,
        Func<IAsyncEnumerable<int>> onSuccess,
        Func<IAsyncEnumerable<int>>? onFailure = null)
    {
        var throwingStream = onFailure ?? ThrowConnectionRefused;
        return _ =>
        {
            callCount.Value++;
            return callCount.Value <= failures ? throwingStream() : onSuccess();
        };
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

    private static async IAsyncEnumerable<int> Yield(params int[] values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<int> ThrowConnectionRefused()
    {
        await Task.Yield();
        throw new SocketException((int)SocketError.ConnectionRefused);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<int> ThrowHttp(HttpStatusCode statusCode)
    {
        await Task.Yield();
        throw new HttpRequestException("provider send failed", inner: null, statusCode);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public MutableTimeProvider(DateTimeOffset start)
        {
            _utcNow = start;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan by)
        {
            _utcNow = _utcNow.Add(by);
        }
    }
}
