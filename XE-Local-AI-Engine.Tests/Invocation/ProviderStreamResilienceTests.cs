namespace XE_Local_AI_Engine.Tests.Invocation;

using System.ClientModel;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

[Category(TestCategories.Unit)]
public sealed class ProviderStreamResilienceTests
{
    private const string UnsupportedOperationBody = """{"error":{"message":"The requested operation is unsupported."}}""";
    private const string NoRouteBody = """{"error":{"code":"apim_policy_error","message":"Unable to match incoming request to an operation."}}""";

    private const string TpmBody = """{"error":"unsupported_tpm_configuration","message":"TokensPerMinute must be one of 10000, 30000."}""";

    [Test]
    public async Task ExecuteStreamingAsync_WhenTransientThenSuccess_RetriesAndYieldsFullStream()
    {
        var callCount = new StrongBox<int>(value: 0);
        var options = new ProviderResilienceOptions
        {
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        };
        var sut = CreateSut(options, maxRetries: 2);

        var factory = FactoryFailing(failures: 2, callCount, () => Yield(1, 2, 3));
        var result = await CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None));

        AssertEx.Equal(expected: 3, result.Count);
        AssertEx.Equal(expected: 1, result[0]);
        AssertEx.Equal(expected: 3, result[2]);
        AssertEx.Equal(expected: 3, callCount.Value);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenTransientThenSuccess_RecordsOnlyActualRetries()
    {
        var callCount = new StrongBox<int>(value: 0);
        var sut = CreateSut(new ProviderResilienceOptions
            {
                BaseDelayMilliseconds = 0,
                CircuitBreakerEnabled = false
            },
            maxRetries: 2);
        ProviderCallEfficiencySnapshot snapshot;

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions()))
        {
            _ = await CollectAsync(sut.ExecuteStreamingAsync("endpoint", FactoryFailing(failures: 2, callCount, () => Yield(1)), CancellationToken.None));
            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        AssertEx.Equal(expected: 2, snapshot.ProviderRetries);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenTransientExhaustsRetries_ThrowsUnderlyingFailure()
    {
        var callCount = new StrongBox<int>(value: 0);
        var options = new ProviderResilienceOptions
        {
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        };
        var sut = CreateSut(options, maxRetries: 2);

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
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        };
        var sut = CreateSut(options, maxRetries: 5);

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
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        };
        var sut = CreateSut(options, maxRetries: 2);

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
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        };
        var sut = CreateSut(options, maxRetries: 2);

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
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = true,
            CircuitBreakerFailureThreshold = 2,
            CircuitBreakerBreakDurationSeconds = 30
        };
        var sut = CreateSut(options, time, maxRetries: 0);

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
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = true,
            CircuitBreakerFailureThreshold = 2,
            CircuitBreakerBreakDurationSeconds = 30
        };
        var sut = CreateSut(options, time, maxRetries: 0);

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

    [Test]
    public async Task ExecuteStreamingAsync_WhenTheNodeSettingSwitchesRetryOff_FailsOnTheFirstTransientError()
    {
        var callCount = new StrongBox<int>(value: 0);
        var sut = new ProviderStreamResilience(Options.Create(new ProviderResilienceOptions
            {
                BaseDelayMilliseconds = 0,
                CircuitBreakerEnabled = false
            }),
            StubNodeRuntimeSettings.Create().WithProviderRetryEnabled(false).WithProviderMaxRetries(5).Build(),
            TimeProvider.System,
            NullLogger<ProviderStreamResilience>.Instance);

        await AssertEx.ThrowsAsync<SocketException>(() =>
            CollectAsync(sut.ExecuteStreamingAsync("endpoint", FactoryFailing(failures: 1, callCount, () => Yield(1)), CancellationToken.None)));

        AssertEx.Equal(expected: 1, callCount.Value, "retry off means one attempt, whatever the retry count says");
    }

    [Test]
    public async Task ExecuteStreamingAsync_ReadsTheRetrySettingsPerSend_SoASaveAppliesWithoutARestart()
    {
        // One singleton serves every send; the second send must see the switch flipped after the first.
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetProviderRetryEnabledAsync(Arg.Any<CancellationToken>()).Returns(false, true);
        runtimeSettings.GetProviderMaxRetriesAsync(Arg.Any<CancellationToken>()).Returns(2);
        var sut = new ProviderStreamResilience(Options.Create(new ProviderResilienceOptions
            {
                BaseDelayMilliseconds = 0,
                CircuitBreakerEnabled = false
            }),
            runtimeSettings,
            TimeProvider.System,
            NullLogger<ProviderStreamResilience>.Instance);

        var firstCalls = new StrongBox<int>(value: 0);
        await AssertEx.ThrowsAsync<SocketException>(() =>
            CollectAsync(sut.ExecuteStreamingAsync("endpoint", FactoryFailing(failures: 1, firstCalls, () => Yield(1)), CancellationToken.None)));
        var secondCalls = new StrongBox<int>(value: 0);
        var result = await CollectAsync(sut.ExecuteStreamingAsync("endpoint", FactoryFailing(failures: 1, secondCalls, () => Yield(1)), CancellationToken.None));

        AssertEx.Equal(expected: 1, firstCalls.Value);
        AssertEx.Equal(expected: 2, secondCalls.Value);
        AssertEx.Equal(expected: 1, result.Count);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenSdkRateLimited_RetriesAndYieldsFullStream()
    {
        var callCount = new StrongBox<int>(value: 0);
        var sut = CreateSut(new ProviderResilienceOptions
        {
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        }, maxRetries: 2);

        var result = await CollectAsync(sut.ExecuteStreamingAsync("endpoint",
            FactoryFailing(failures: 1, callCount, () => Yield(4, 5), () => ThrowStatus(429)),
            CancellationToken.None));

        AssertEx.Equal(expected: 2, result.Count);
        AssertEx.Equal(expected: 2, callCount.Value);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenSdkServerError_IsTransientAndRetried()
    {
        var callCount = new StrongBox<int>(value: 0);
        var sut = CreateSut(new ProviderResilienceOptions
        {
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        }, maxRetries: 2);

        var result = await CollectAsync(sut.ExecuteStreamingAsync("endpoint",
            FactoryFailing(failures: 2, callCount, () => Yield(1), () => ThrowStatus(503)),
            CancellationToken.None));

        AssertEx.Equal(expected: 1, result.Count);
        AssertEx.Equal(expected: 3, callCount.Value);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenSdkClientError_IsNotTransientAndNotRetried()
    {
        var callCount = new StrongBox<int>(value: 0);
        var sut = CreateSut(new ProviderResilienceOptions
        {
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        }, maxRetries: 2);

        await AssertEx.ThrowsAsync<ClientResultException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint",
            FactoryFailing(failures: int.MaxValue, callCount, () => Yield(1), () => ThrowStatus(400)),
            CancellationToken.None)));

        AssertEx.Equal(expected: 1, callCount.Value);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenRetryAfterIsDeltaSeconds_WaitsExactlyThatLong()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var callCount = new StrongBox<int>(value: 0);

        // Zero base backoff: any recorded delay can only come from the Retry-After header.
        var sut = CreateSut(new ProviderResilienceOptions
        {
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        }, time, maxRetries: 2);

        _ = await CollectAsync(sut.ExecuteStreamingAsync("endpoint",
            FactoryFailing(failures: 1, callCount, () => Yield(1), () => ThrowStatus(429, "7")),
            CancellationToken.None));

        AssertEx.Equal(expected: 1, time.Delays.Count);
        AssertEx.Equal(TimeSpan.FromSeconds(7), time.Delays[0]);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenRetryAfterIsHttpDate_WaitsUntilThatDate()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(year: 2026, month: 10, day: 22, hour: 12, minute: 0, second: 0, TimeSpan.Zero));
        var callCount = new StrongBox<int>(value: 0);
        var retryAt = time.GetUtcNow().AddSeconds(12).ToString("r", CultureInfo.InvariantCulture);
        var sut = CreateSut(new ProviderResilienceOptions
        {
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        }, time, maxRetries: 2);

        _ = await CollectAsync(sut.ExecuteStreamingAsync("endpoint",
            FactoryFailing(failures: 1, callCount, () => Yield(1), () => ThrowStatus(503, retryAt)),
            CancellationToken.None));

        AssertEx.Equal(expected: 1, time.Delays.Count);
        AssertEx.Equal(TimeSpan.FromSeconds(12), time.Delays[0]);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenRetryAfterExceedsTheCap_ThrowsWithoutRetrying()
    {
        var time = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        var callCount = new StrongBox<int>(value: 0);
        var sut = CreateSut(new ProviderResilienceOptions
        {
            MaxRetryAfterSeconds = 30,
            CircuitBreakerEnabled = false
        }, time, maxRetries: 2);
        ProviderCallEfficiencySnapshot snapshot;

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions()))
        {
            await AssertEx.ThrowsAsync<ClientResultException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint",
                FactoryFailing(failures: int.MaxValue, callCount, () => Yield(1), () => ThrowStatus(429, "120")),
                CancellationToken.None)));
            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        AssertEx.Equal(expected: 1, callCount.Value);
        AssertEx.Equal(expected: 0, snapshot.ProviderRetries);
        AssertEx.Equal(expected: 0, time.Delays.Count);
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenRepeatedlyRateLimited_KeepsTheBreakerClosed()
    {
        var callCount = new StrongBox<int>(value: 0);
        var sut = CreateSut(BreakerAtTwo(), new MutableTimeProvider(DateTimeOffset.UnixEpoch), maxRetries: 0);
        var factory = FactoryFailing(failures: int.MaxValue, callCount, () => Yield(1), () => ThrowStatus(429));

        for (var send = 0; send < 3; send++)
        {
            await AssertEx.ThrowsAsync<ClientResultException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None)));
        }

        AssertEx.Equal(expected: 3, callCount.Value, "every rate-limited send reached the provider; none was fast-failed");
    }

    [Test]
    public async Task ExecuteStreamingAsync_WhenRepeatedlyServerError_OpensTheBreaker()
    {
        var callCount = new StrongBox<int>(value: 0);
        var sut = CreateSut(BreakerAtTwo(), new MutableTimeProvider(DateTimeOffset.UnixEpoch), maxRetries: 0);
        var factory = FactoryFailing(failures: int.MaxValue, callCount, () => Yield(1), () => ThrowStatus(503));

        await AssertEx.ThrowsAsync<ClientResultException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None)));
        await AssertEx.ThrowsAsync<ClientResultException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None)));
        await AssertEx.ThrowsAsync<ProviderCircuitOpenException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None)));

        AssertEx.Equal(expected: 2, callCount.Value);
    }

    // A gateway's no-route 500 (wrong base URL) or unsupported operation (500 or 429) fails on the first attempt; a TPM 500 is still retried.
    [Test]
    [Arguments(500, NoRouteBody, 1)]
    [Arguments(500, UnsupportedOperationBody, 1)]
    [Arguments(429, UnsupportedOperationBody, 1)]
    [Arguments(500, TpmBody, 3)]
    public async Task ExecuteStreamingAsync_WhenGatewayError_RetriesUnlessItIsNoRouteOrUnsupported(int status, string body, int expectedAttempts)
    {
        var callCount = new StrongBox<int>(value: 0);
        var sut = CreateSut(new ProviderResilienceOptions
        {
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = false
        }, maxRetries: 2);

        await AssertEx.ThrowsAsync<ClientResultException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint",
            FactoryFailing(failures: int.MaxValue, callCount, () => Yield(1), () => ThrowStatus(status, body: body)),
            CancellationToken.None)));

        AssertEx.Equal(expectedAttempts, callCount.Value);
    }

    // Repeated no-route sends never open the breaker, so fixing the base URL is not followed by a "temporarily unavailable" send.
    [Test]
    public async Task ExecuteStreamingAsync_WhenRepeatedlyNoRoute_KeepsTheBreakerClosed()
    {
        var callCount = new StrongBox<int>(value: 0);
        var sut = CreateSut(BreakerAtTwo(), new MutableTimeProvider(DateTimeOffset.UnixEpoch), maxRetries: 0);
        var factory = FactoryFailing(failures: int.MaxValue, callCount, () => Yield(1), () => ThrowStatus(500, body: NoRouteBody));

        for (var send = 0; send < 3; send++)
        {
            await AssertEx.ThrowsAsync<ClientResultException>(() => CollectAsync(sut.ExecuteStreamingAsync("endpoint", factory, CancellationToken.None)));
        }

        AssertEx.Equal(expected: 3, callCount.Value, "every no-route send reached the provider; none was fast-failed");
    }

    [Test]
    [Arguments("Thu, 22 Oct 2026 11:59:00 GMT", 0)]
    [Arguments("0", 0)]
    [Arguments(" 3 ", 3)]
    public void ProviderRetryAfterParse_ClampsAPastDateToZeroAndTrims(string value, int expectedSeconds)
    {
        var now = new DateTimeOffset(year: 2026, month: 10, day: 22, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

        AssertEx.Equal<TimeSpan?>(TimeSpan.FromSeconds(expectedSeconds), ProviderRetryAfter.Parse(value, now));
    }

    [Test]
    [Arguments("soon")]
    [Arguments("-5")]
    [Arguments("1.5")]
    [Arguments("")]
    public void ProviderRetryAfterParse_WhenNotDeltaSecondsOrHttpDate_ReturnsNull(string value)
    {
        AssertEx.Null(ProviderRetryAfter.Parse(value, DateTimeOffset.UnixEpoch));
    }

    // The retry count is a node setting read per send, so it rides the accessor; the backoff and breaker stay on the options.
    private static ProviderStreamResilience CreateSut(ProviderResilienceOptions options, TimeProvider? timeProvider = null, int maxRetries = StoredNodeSettings.DefaultProviderMaxRetries)
    {
        return new ProviderStreamResilience(Options.Create(options),
            StubNodeRuntimeSettings.Create().WithProviderMaxRetries(maxRetries).Build(),
            timeProvider ?? TimeProvider.System,
            NullLogger<ProviderStreamResilience>.Instance);
    }

    private static ProviderResilienceOptions BreakerAtTwo()
    {
        return new ProviderResilienceOptions
        {
            BaseDelayMilliseconds = 0,
            CircuitBreakerEnabled = true,
            CircuitBreakerFailureThreshold = 2,
            CircuitBreakerBreakDurationSeconds = 30
        };
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
        yield return await Task.FromException<int>(new SocketException((int)SocketError.ConnectionRefused));
    }

    private static async IAsyncEnumerable<int> ThrowHttp(HttpStatusCode statusCode)
    {
        await Task.Yield();
        yield return await Task.FromException<int>(new HttpRequestException("provider send failed", inner: null, statusCode));
    }

    private static async IAsyncEnumerable<int> ThrowStatus(int status, string? retryAfter = null, string? body = null)
    {
        await Task.Yield();
        yield return await Task.FromException<int>(body is null
            ? FakeClientResultException.Create(status, retryAfter)
            : FakeClientResultException.Create(status, retryAfter, "Internal Server Error", body));
    }

    // Records every timer's due time and fires it at once, so a backoff is asserted on the clock, never waited out.
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public List<TimeSpan> Delays { get; } = [];

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

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (Delays)
            {
                Delays.Add(dueTime);
            }

            return System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
}
