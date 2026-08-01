namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Text;
using XE_Local_AI_Engine.Providers.LlamaServer;
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
            using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port: 0))
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
