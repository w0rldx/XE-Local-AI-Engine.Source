namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Net.Sockets;
using System.Text;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the transport-failure translation in <see cref="DeferredLlamaServerEmbeddingGenerator" />: every failure it
///     can produce must land in the single <see cref="HttpRequestException" /> / <see cref="IOException" /> set that both
///     the knowledge-ingestion pipeline and the playbook ranker's lexical fallback already catch.
/// </summary>
/// <remarks>
///     <para>
///         REGRESSION (capture run 2026-08-01). A non-2xx from llama-server surfaced as
///         <c>System.ClientModel.ClientResultException</c> — the MEAI OpenAI adapter's own type — which matched NOBODY's
///         catch set. It escaped <c>KnowledgeChunkEmbedder</c>'s handler, escaped the ranker's, and reached
///         <c>KnowledgeIngestionService</c>'s catch-all, where every document was stamped
///         "Ingestion failed unexpectedly. Retry the upload." and the log recorded only the type NAME. The result was a
///         100%-reproducible failure with no status, no server response, and no failing step recorded anywhere.
///     </para>
///     <para>
///         These tests run against a real loopback <see cref="HttpListener" /> rather than a mocked adapter, because the
///         defect lived in what the SDK does with a real HTTP response — a hand-thrown exception would have proven
///         nothing about the shape the SDK actually raises.
///     </para>
/// </remarks>
public sealed class DeferredLlamaServerEmbeddingGeneratorFailureTests
{
    // The verbatim body a llama-server returns when a pooled embedding input exceeds the physical batch size. This is
    // the exact failure that broke knowledge-base ingestion, captured from a live nomic-embed-text-v1.5 server.
    private const string OversizedInputBody =
        """{"error":{"code":500,"message":"input (678 tokens) is too large to process. increase the physical batch size (current batch size: 512)","type":"server_error"}}""";

    [Test]
    public async Task GenerateAsync_WhenServerReturns500_ThrowsHttpRequestExceptionCarryingStatusAndServerDetail()
    {
        using var server = StubServer.Returning(HttpStatusCode.InternalServerError, OversizedInputBody);
        using var generator = new DeferredLlamaServerEmbeddingGenerator(SupervisorFor(server),
            "nomic-embed-text-v1.5",
            TimeSpan.FromSeconds(30));

        var exception = await AssertEx.ThrowsAsync<HttpRequestException>(() =>
            generator.GenerateAsync(["search_document: some chunk text"]));

        // The status must survive: KnowledgeChunkEmbedder keys "the server answered and rejected this" off StatusCode
        // being non-null, and reports a DIFFERENT, accurate reason for it than for an unreachable provider.
        AssertEx.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);

        // The server's own diagnostic must reach the message — omitting it is what made this undiagnosable. It is
        // llama-server's error text, never the caller's input, so it carries no document content.
        AssertEx.True(exception.Message.Contains("physical batch size", StringComparison.Ordinal),
            $"Expected llama-server's response detail in the message, got: {exception.Message}");
        AssertEx.True(exception.Message.Contains("500", StringComparison.Ordinal),
            $"Expected the HTTP status in the message, got: {exception.Message}");
    }

    [Test]
    public async Task GenerateAsync_WhenServerReturns400_ThrowsHttpRequestException_NotClientResultException()
    {
        // Any non-2xx, not just 500 — the SDK raises the same ClientResultException for all of them.
        using var server = StubServer.Returning(HttpStatusCode.BadRequest, """{"error":{"message":"bad request"}}""");
        using var generator = new DeferredLlamaServerEmbeddingGenerator(SupervisorFor(server),
            "nomic-embed-text-v1.5",
            TimeSpan.FromSeconds(30));

        var exception = await AssertEx.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync(["chunk"]));

        AssertEx.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    [Test]
    public async Task GenerateAsync_WhenServerSucceeds_ReturnsVectors()
    {
        // The positive control: the translation must not swallow or reshape a successful round-trip.
        using var server = StubServer.Returning(HttpStatusCode.OK,
            """{"object":"list","model":"m","usage":{"prompt_tokens":4,"total_tokens":4},"data":[{"object":"embedding","index":0,"embedding":[0.25,-0.5,0.75]}]}""");
        using var generator = new DeferredLlamaServerEmbeddingGenerator(SupervisorFor(server),
            "nomic-embed-text-v1.5",
            TimeSpan.FromSeconds(30));

        var embeddings = await generator.GenerateAsync(["chunk"]);

        AssertEx.Equal(expected: 1, embeddings.Count);
        AssertEx.Equal(expected: 3, embeddings[0].Vector.Length);
    }

    [Test]
    public async Task GenerateAsync_CachesTheAdapter_UntilTheServerIsGone_ThenReEnsuresOnTheNextCall()
    {
        // The generator binds ONE endpoint for its lifetime. That is safe today only because all four callers scope it to
        // a single document/search; without a self-heal seam a longer-lived caller would retry a dead address forever.
        // Mirrors DeferredLlamaServerChatClient.InvalidateInner — see DeferredLlamaServerChatClientServerGoneTests.
        // The two stub servers answer with DIFFERENT vector widths, which is how each assertion identifies the endpoint
        // that actually served the call without reaching into the generator's private state.
        var original = StubServer.Returning(HttpStatusCode.OK, EmbeddingResponseWith("[0.25,-0.5,0.75]"));
        using var replacement = StubServer.Returning(HttpStatusCode.OK, EmbeddingResponseWith("[0.5,-0.25]"));
        using var lease = new RecordingInferenceLease();
        var supervisor = new FakeProcessSupervisor
        {
            EnsureEndpoint = original.BaseAddress,

            // A warm, registered process — which is what a cached adapter is supposed to be pointing at. Without a
            // lease the generator treats a cached adapter as unresolved and re-ensures, which is a different test.
            LeaseAcquisition = LlamaServerLeaseAcquisition.Granted(lease)
        };

        using var generator = new DeferredLlamaServerEmbeddingGenerator(supervisor, "nomic-embed-text-v1.5", TimeSpan.FromSeconds(30));

        AssertEx.Equal(expected: 3, (await generator.GenerateAsync(["chunk"]))[0].Vector.Length);

        // Move the endpoint the supervisor hands out. A call that still lands on the ORIGINAL server proves the adapter
        // is cached — the deferred start is per generator, not a per-call endpoint resolution.
        supervisor.EnsureEndpoint = replacement.BaseAddress;
        AssertEx.Equal(expected: 3, (await generator.GenerateAsync(["chunk"]))[0].Vector.Length);

        // The process behind the cached endpoint goes away.
        original.Dispose();
        await AssertEx.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync(["chunk"]));

        // That failure must have dropped the adapter, so this call re-ensures and lands on the replacement.
        AssertEx.Equal(expected: 2, (await generator.GenerateAsync(["chunk"]))[0].Vector.Length);
    }

    [Test]
    public async Task GenerateAsync_WhenTheServerRejectsTheRequest_KeepsTheCachedAdapter()
    {
        // A non-2xx means the server ANSWERED: it is alive and its endpoint is still correct. Invalidating here would
        // turn every oversized-batch rejection into a needless respawn round-trip.
        using var rejecting = StubServer.Returning(HttpStatusCode.InternalServerError, OversizedInputBody);
        using var healthy = StubServer.Returning(HttpStatusCode.OK, EmbeddingResponseWith("[0.5,-0.25]"));
        using var lease = new RecordingInferenceLease();
        var supervisor = new FakeProcessSupervisor
        {
            EnsureEndpoint = rejecting.BaseAddress,
            LeaseAcquisition = LlamaServerLeaseAcquisition.Granted(lease)
        };

        using var generator = new DeferredLlamaServerEmbeddingGenerator(supervisor, "nomic-embed-text-v1.5", TimeSpan.FromSeconds(30));

        await AssertEx.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync(["chunk"]));

        // Even with a healthy endpoint now on offer, the retained adapter must still be talking to the rejecting server —
        // a success here would mean the 500 had wrongly invalidated it.
        supervisor.EnsureEndpoint = healthy.BaseAddress;
        await AssertEx.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync(["chunk"]));
    }

    private static string EmbeddingResponseWith(string vector)
    {
        return $$"""{"object":"list","model":"m","usage":{"prompt_tokens":4,"total_tokens":4},"data":[{"object":"embedding","index":0,"embedding":{{vector}}}]}""";
    }

    // An embedding request held no lease at all, so ActiveLeases stayed 0, profiling's pre-spawn claim succeeded and
    // the removal tree-killed the live request.

    [Test]
    public async Task GenerateAsync_HoldsAnInferenceLeaseForTheEmbeddingRole_AndReleasesIt()
    {
        using var lease = new RecordingInferenceLease();
        var supervisor = new FakeProcessSupervisor
        {
            EnsureEndpoint = new Uri("http://127.0.0.1:9/v1"),
            LeaseAcquisition = LlamaServerLeaseAcquisition.Granted(lease)
        };
        using var generator = new DeferredLlamaServerEmbeddingGenerator(supervisor, "nomic-embed-text-v1.5", TimeSpan.FromSeconds(5));

        // Nothing listens on the endpoint, so the call fails at transport — the lease bracket around it is the point.
        await AssertEx.ThrowsAsync<Exception>(() => generator.GenerateAsync(["search_document: chunk"]));

        AssertEx.Contains(supervisor.LeasedRoles, ModelRole.Embedding);
        AssertEx.True(lease.Disposed, "The request-lifetime lease must be released when the request ends.");
    }

    [Test]
    public async Task GenerateAsync_WhenProfilingOwnsTheKey_ReEnsuresInsteadOfEmbeddingAgainstTheMeasurement()
    {
        var supervisor = new FakeProcessSupervisor
        {
            EnsureEndpoint = new Uri("http://127.0.0.1:9/v1")
        };
        supervisor.LeaseSequence.Enqueue(LlamaServerLeaseAcquisition.ProfilingOwned);
        supervisor.LeaseSequence.Enqueue(LlamaServerLeaseAcquisition.NotRunning);
        using var generator = new DeferredLlamaServerEmbeddingGenerator(supervisor, "nomic-embed-text-v1.5", TimeSpan.FromSeconds(5));

        await AssertEx.ThrowsAsync<Exception>(() => generator.GenerateAsync(["search_document: chunk"]));

        AssertEx.Equal(expected: 2, supervisor.EnsureCalls,
            "A profiling refusal must drop the cached adapter and re-ensure, not embed against the measurement process.");
    }

    [Test]
    public async Task GenerateAsync_FromACachedAdapter_ReEnsuresBeforeTrustingANotRunningLease()
    {
        // This generator can serve a whole request from its cached adapter without ensuring anything, so "not running"
        // read there is also exactly what profiling's remove-then-register window looks like — and the freed port is
        // commonly re-handed to the measurement spawn. A 400 keeps the cache (it is not a server-gone shape), so the
        // second call is served from cache and must re-ensure before proceeding leaseless.
        using var server = StubServer.Returning(HttpStatusCode.BadRequest, OversizedInputBody);
        var supervisor = new FakeProcessSupervisor
        {
            EnsureEndpoint = server.BaseAddress,
            LeaseAcquisition = LlamaServerLeaseAcquisition.NotRunning
        };
        using var generator = new DeferredLlamaServerEmbeddingGenerator(supervisor, "nomic-embed-text-v1.5", TimeSpan.FromSeconds(30));

        _ = await AssertEx.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync(["search_document: first"]));
        AssertEx.Equal(expected: 1, supervisor.EnsureCalls);

        _ = await AssertEx.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync(["search_document: second"]));

        AssertEx.Equal(expected: 2, supervisor.EnsureCalls,
            "A cached adapter plus a 'not running' lease must be re-resolved, not trusted.");
    }

    [Test]
    public async Task GenerateAsync_WhenProfilingNeverReleasesTheKey_DegradesToTheCallersTransportFailureSet()
    {
        var supervisor = new FakeProcessSupervisor
        {
            EnsureEndpoint = new Uri("http://127.0.0.1:9/v1"),
            LeaseAcquisition = LlamaServerLeaseAcquisition.ProfilingOwned
        };
        using var generator = new DeferredLlamaServerEmbeddingGenerator(supervisor, "nomic-embed-text-v1.5", TimeSpan.FromSeconds(5));

        // IOException, not a bare throw: the ranker's lexical fallback and the ingestion pipeline both key off this set.
        _ = await AssertEx.ThrowsAsync<IOException>(() => generator.GenerateAsync(["search_document: chunk"]));
    }

    private static FakeProcessSupervisor SupervisorFor(StubServer server)
    {
        return new FakeProcessSupervisor
        {
            EnsureEndpoint = server.BaseAddress
        };
    }

    /// <summary>
    ///     A single-shot loopback HTTP server that answers every request with one fixed status + JSON body. Binds an
    ///     ephemeral port so parallel tests never collide.
    /// </summary>
    private sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        private StubServer(HttpListener listener, Uri baseAddress)
        {
            _listener = listener;
            BaseAddress = baseAddress;
        }

        public Uri BaseAddress { get; }

        public static StubServer Returning(HttpStatusCode status, string body)
        {
            // Probe for a free ephemeral port: HttpListener has no "bind port 0" mode, so ask the OS for one via a
            // throwaway TcpListener and reuse the number. A racing bind is possible in principle but the window is
            // sub-millisecond and the alternative (a fixed port) collides with parallel test classes deterministically.
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, port: 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            var server = new StubServer(listener, new Uri($"http://127.0.0.1:{port}/v1"));
            server.Serve(status, body);
            return server;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            _cts.Dispose();
        }

        private void Serve(HttpStatusCode status, string body)
        {
            _ = Task.Run(async () =>
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        HttpListenerContext context;
                        try
                        {
                            context = await _listener.GetContextAsync().ConfigureAwait(false);
                        }
                        catch (HttpListenerException)
                        {
                            return; // Disposed.
                        }
                        catch (ObjectDisposedException)
                        {
                            return;
                        }

                        var payload = Encoding.UTF8.GetBytes(body);
                        context.Response.StatusCode = (int)status;
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = payload.Length;
                        await context.Response.OutputStream.WriteAsync(payload, _cts.Token).ConfigureAwait(false);
                        context.Response.Close();
                    }
                },
                _cts.Token);
        }
    }
}
