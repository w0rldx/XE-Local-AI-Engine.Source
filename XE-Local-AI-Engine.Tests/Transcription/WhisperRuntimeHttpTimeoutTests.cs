namespace XE_Local_AI_Engine.Tests.Transcription;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The runtime HTTP client's deadline contract. One client serves three requests whose right budgets differ by
///     four orders of magnitude, so it carries NO client-level timeout and NO resilience pipeline, and every call site
///     owns an explicit deadline instead.
/// </summary>
/// <remarks>
///     <para>
///         <b>The container under test must have the Aspire defaults installed, or every assertion here is vacuous.</b>
///         <c>AddServiceDefaults</c> installs a standard resilience handler on every named client through
///         <c>ConfigureHttpClientDefaults</c> — but only when <c>ASPIRE_ENABLED</c> is <c>true</c>. A test that skips
///         that flag passes whether or not <c>RemoveAllResilienceHandlers</c> was ever called, which is exactly the
///         shape of test that would have let the inherited ten-second attempt timeout ship.
///     </para>
///     <para>
///         <c>[NotInParallel]</c> because the slow cases hold a real loopback listener and are sized in wall-clock
///         seconds; running them against a contended box alongside the rest of the module makes them flaky rather than
///         meaningful.
///     </para>
/// </remarks>
[NotInParallel]
public sealed class WhisperRuntimeHttpTimeoutTests
{
    [Test]
    public void RuntimeClient_WithServiceDefaultsInstalled_HasAnInfiniteTimeout()
    {
        // Cheap, and it is what catches a dropped Timeout.InfiniteTimeSpan before the slow case below does. The 100
        // second default would abort a long transcription on its own, regardless of any linked-token deadline.
        using var provider = BuildProviderWithAspireDefaults();
        using var client = CreateRuntimeClient(provider);

        AssertEx.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Test]
    public async Task InferenceRequest_AgainstASlowServer_SurvivesPastTenSeconds()
    {
        // This is what fails if the inherited standard pipeline is still attached: its per-attempt timeout is ten
        // seconds, while a CPU transcription legitimately runs for minutes.
        using var server = new SlowLoopbackServer(TimeSpan.FromSeconds(12));
        using var provider = BuildProviderWithAspireDefaults();
        using var client = CreateRuntimeClient(provider);

        using var timeout = new CancellationTokenSource(TestBudgets.Contended);
        var stopwatch = Stopwatch.StartNew();
        // real-timer: the subject IS elapsed wall-clock time against a real socket. A fake clock cannot express "the
        // handler did not abort this request at its own ten-second mark", because the handler reads the real clock.
        using var response = await client.GetAsync(server.Uri, timeout.Token);
        stopwatch.Stop();

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.True(stopwatch.Elapsed > TimeSpan.FromSeconds(10),
            $"The request completed in {stopwatch.Elapsed.TotalSeconds:F1}s, which is inside the standard pipeline's "
            + "attempt timeout — the inherited handler was not removed, so this proves nothing.");
    }

    [Test]
    public async Task InferencePost_AgainstAFailingServer_IsNotRetried()
    {
        // Neither the transcription nor the model load is idempotent, and the inherited pipeline retries every method
        // by default. A retried transcription would bill the operator's CPU twice for one request.
        using var server = new SlowLoopbackServer(TimeSpan.Zero, HttpStatusCode.ServiceUnavailable);
        using var provider = BuildProviderWithAspireDefaults();
        using var client = CreateRuntimeClient(provider);

        using var timeout = new CancellationTokenSource(TestBudgets.Contended);
        using var content = new StringContent(string.Empty);
        using var response = await client.PostAsync(server.Uri, content, timeout.Token);

        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertEx.Equal(expected: 1, server.RequestCount, "A non-idempotent POST must reach the daemon exactly once.");
    }

    [Test]
    public async Task Request_WhenTheCallerCancels_PropagatesTheCallersCancellation()
    {
        // The infinite client timeout is safe only because the caller's token still governs. This is that half.
        using var server = new SlowLoopbackServer(TimeSpan.FromSeconds(30));
        using var provider = BuildProviderWithAspireDefaults();
        using var client = CreateRuntimeClient(provider);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => client.GetAsync(server.Uri, cts.Token));
    }

    [Test]
    public async Task HealthProbe_AgainstAStalledServer_FailsWithinItsOwnShortBudget()
    {
        // The counterpart to the infinite client timeout: every call site owns its deadline, and the health probe's is
        // the shortest of the three. Without it a bound-but-silent daemon would hold one probe open for the whole
        // readiness window on a client that never times out, and the supervisor would report "not ready in time"
        // minutes after the daemon stopped answering.
        using var server = new SlowLoopbackServer(TimeSpan.FromMinutes(5));
        var options = new WhisperRuntimeOptions
        {
            HealthProbeTimeout = TimeSpan.FromSeconds(2)
        };
        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var probe = new WhisperServerReadinessProbe(httpClient, options);

        var stopwatch = Stopwatch.StartNew();
        // real-timer: the assertion IS elapsed wall-clock time against a real socket that never answers. The probe
        // takes no TimeProvider — its budget is a CancelAfter on the real clock — so a fake clock cannot express it.
        var responsive = await probe.CheckResponsiveAsync(server.Uri, CancellationToken.None);
        stopwatch.Stop();

        AssertEx.False(responsive, "A server that never answers must not be reported as responsive.");
        AssertEx.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"The probe took {stopwatch.Elapsed.TotalSeconds:F1}s against a stalled server, so it is not bounded by "
            + "HealthProbeTimeout — the infinite client timeout is then unbounded in practice.");
    }

    private static ServiceProvider BuildProviderWithAspireDefaults()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing"
        });

        // AddServiceDefaults installs the standard resilience handler on every named client ONLY under this flag,
        // which the AppHost sets. Without it the container is not in the shape this test exists to check.
        builder.Configuration["ASPIRE_ENABLED"] = "true";
        builder.AddServiceDefaults();
        builder.Services.AddWhisperCppRuntime();

        return builder.Services.BuildServiceProvider();
    }

    private static HttpClient CreateRuntimeClient(IServiceProvider provider) =>
        provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(WhisperCppServiceCollectionExtensions.RuntimeHttpClientName);

    /// <summary>A loopback listener that withholds its response for a fixed delay and counts the requests it saw.</summary>
    private sealed class SlowLoopbackServer : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TimeSpan _delay;
        private readonly TcpListener _listener;
        private readonly HttpStatusCode _status;
        private int _requestCount;

        public SlowLoopbackServer(TimeSpan delay, HttpStatusCode status = HttpStatusCode.OK)
        {
            _delay = delay;
            _status = status;

            // Bound and KEPT bound for the whole test: a port obtained by binding :0 and releasing it is a candidate,
            // not a reservation.
            _listener = new TcpListener(IPAddress.Loopback, port: 0);
            _listener.Start();
            Uri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        }

        public Uri Uri { get; }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    _ = Task.Run(() => ServeAsync(client, ct), ct);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The listener was stopped by Dispose.
            }
        }

        private async Task ServeAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _requestCount);

                    if (_delay > TimeSpan.Zero)
                    {
                        // real-timer: withholding the response for a real interval IS the subject — the point is that
                        // no handler in the pipeline aborts the request while the server is simply slow.
                        await Task.Delay(_delay, ct).ConfigureAwait(false);
                    }

                    var response = $"HTTP/1.1 {(int)_status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
                {
                    // The caller went away, or the test ended. Either is expected here.
                }
            }
        }
    }
}
