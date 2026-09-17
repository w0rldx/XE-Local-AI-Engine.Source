namespace XE_Local_AI_Engine.Tests.Proxy;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Proxy;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The behaviour of the raw-model proxy forwarder: it provisions the requested local model and reverse-proxies the
///     request VERBATIM to the llama-server child's own OpenAI endpoint. The load-bearing guarantees under test are that
///     it forwards to the exact supervisor-provided loopback URL (no SSRF from the caller's path), that it is
///     llama.cpp-only and refuses any unknown/cloud model name with a 404 (so an external tool can never reach cloud
///     credentials), and that a busy runtime is a retryable 503 rather than a hang.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LocalModelProxyForwarderTests
{
    private const string InstalledModel = "test-model";
    private static readonly Uri ChildEndpoint = new("http://127.0.0.1:18100/v1");

    [Test]
    public async Task WriteModelsAsync_ProjectsTheInstalledLlamaCatalogAsAnOpenAiModelList()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext(body: string.Empty, out var responseBody);

        await forwarder.WriteModelsAsync(context);

        using var document = JsonDocument.Parse(responseBody.ToArray());
        AssertEx.Equal("list", document.RootElement.GetProperty("object").GetString());
        var data = document.RootElement.GetProperty("data");
        AssertEx.Equal(1, data.GetArrayLength());
        AssertEx.Equal(InstalledModel, data[0].GetProperty("id").GetString());
        AssertEx.Equal("model", data[0].GetProperty("object").GetString());
        AssertEx.Equal(LlamaServerProviderConstants.ProviderName, data[0].GetProperty("owned_by").GetString());
    }

    [Test]
    public async Task ForwardChatCompletions_WithAKnownModel_ForwardsVerbatimToTheChildAndStreamsTheResponseBack()
    {
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "text/event-stream", "data: hello\n\n");
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}", out var responseBody);

        await forwarder.ForwardChatCompletionsAsync(context);

        _ = supervisor.Received(1).EnsureRunningAsync(InstalledModel, ModelRole.Chat, Arg.Any<CancellationToken>());
        AssertEx.NotNull(upstream.LastRequest);
        AssertEx.Equal("http://127.0.0.1:18100/v1/chat/completions", upstream.LastRequest!.RequestUri!.AbsoluteUri);
        AssertEx.Equal("{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}", upstream.LastRequestBody);
        AssertEx.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        AssertEx.Equal("data: hello\n\n", Encoding.UTF8.GetString(responseBody.ToArray()));
    }

    [Test]
    public async Task ForwardChatCompletions_PropagatesTheChildStatusCode()
    {
        using var upstream = new CapturingHandler(HttpStatusCode.BadRequest, "application/json", "{\"error\":\"bad\"}");
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Test]
    public async Task ForwardChatCompletions_WithAnUnknownModel_Returns404AndNeverProvisionsOrForwards()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        // A cloud-ish / unmapped model name is exactly the case that must never reach any other provider.
        var context = BuildContext("{\"model\":\"gpt-5.6-terra\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        AssertEx.Null(upstream.LastRequest);
        _ = supervisor.DidNotReceive().EnsureRunningAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ForwardChatCompletions_WithNoModelField_Returns400()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Test]
    public async Task ForwardChatCompletions_WithMalformedJson_Returns400()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("not json at all", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheRuntimeCannotProvision_Returns503WithRetryAfter()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.EnsureRunningAsync(InstalledModel, ModelRole.Chat, Arg.Any<CancellationToken>())
                  .Returns<Task<LlamaServerEndpoint>>(_ => throw new LlamaRuntimeException("At capacity."));
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        AssertEx.Equal("5", context.Response.Headers.RetryAfter.ToString());
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheModelIsBeingEjected_Returns503()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.TryAcquireInferenceLease(InstalledModel, ModelRole.Chat).Returns(LlamaServerLeaseAcquisition.Evicting);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        AssertEx.Null(upstream.LastRequest);
    }

    [Test]
    public async Task ForwardChatCompletions_WhenProfilingOwnsTheKey_ReEnsuresAndForwardsAfterProfilingEnds()
    {
        // Profiling replaced the process between EnsureRunning and the lease lookup. Forwarding on the endpoint we
        // already resolved would reach the measurement process, whose port the spawn commonly inherits.
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", "{}");
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.TryAcquireInferenceLease(InstalledModel, ModelRole.Chat)
                  .Returns(LlamaServerLeaseAcquisition.ProfilingOwned, LlamaServerLeaseAcquisition.NotRunning);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        _ = supervisor.Received(2).EnsureRunningAsync(InstalledModel, ModelRole.Chat, Arg.Any<CancellationToken>());
        AssertEx.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        AssertEx.NotNull(upstream.LastRequest);
    }

    [Test]
    public async Task ForwardChatCompletions_WhenProfilingNeverReleasesTheKey_Returns503WithoutForwarding()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.TryAcquireInferenceLease(InstalledModel, ModelRole.Chat).Returns(LlamaServerLeaseAcquisition.ProfilingOwned);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        AssertEx.Null(upstream.LastRequest, "Nothing may be forwarded to the measurement process.");
    }

    [Test]
    public async Task ForwardEmbeddings_TargetsTheEmbeddingRoleAndTheEmbeddingsSubpath()
    {
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", "{\"data\":[]}");
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.EnsureRunningAsync(InstalledModel, ModelRole.Embedding, Arg.Any<CancellationToken>())
                  .Returns(new LlamaServerEndpoint(InstalledModel, ModelRole.Embedding, ChildEndpoint));
        var context = BuildContext("{\"model\":\"test-model\",\"input\":\"hi\"}", out _);

        await forwarder.ForwardEmbeddingsAsync(context);

        _ = supervisor.Received(1).EnsureRunningAsync(InstalledModel, ModelRole.Embedding, Arg.Any<CancellationToken>());
        AssertEx.Equal("http://127.0.0.1:18100/v1/embeddings", upstream.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheChildConnectionFails_Returns503WithRetryAfter()
    {
        // The child exited/refused after provisioning — an HttpRequestException before any response bytes. This must map
        // to the same retryable 503 as an at-capacity spawn, not fall through to a generic 500.
        using var upstream = new ThrowingHandler();
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        AssertEx.Equal("5", context.Response.Headers.RetryAfter.ToString());
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheUpstreamStreamStalls_AbortsWithinTheIdleDeadlineInsteadOfHanging()
    {
        // A child that sends response headers then goes silent WITHOUT closing the socket. Without the inter-read idle
        // deadline the forward would hang forever (infinite client timeout) and never release the inference lease.
        using var upstream = new StallingHandler();
        var forwarder = CreateForwarder(out _, out _, upstream, idleTimeout: TimeSpan.FromMilliseconds(150));
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        // WaitAsync turns a regression (the hang returning) into a fast, legible failure instead of a stuck test run.
        await forwarder.ForwardChatCompletionsAsync(context).WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.True(context.RequestAborted.IsCancellationRequested,
            "The idle watchdog must abort the request when the upstream goes silent, so the inference lease is released.");
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheUpstreamDiesMidEventStream_EndsWithAnErrorFrameAndDone()
    {
        // A forced eject kills the child mid-stream: the open body ends as HttpIOException(ResponseEnded). That is an
        // expected operator action, so the caller must get a reason and a stream terminator rather than a stream that
        // stops mid-token (and the node must not log it as an unhandled pipeline error).
        using var upstream = new DyingHandler("text/event-stream");
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[],\"stream\":true}", out var responseBody);

        await forwarder.ForwardChatCompletionsAsync(context);

        var written = Encoding.UTF8.GetString(responseBody.ToArray());
        AssertEx.True(written.StartsWith(DyingStream.PartialLine, StringComparison.Ordinal),
            $"The bytes already relayed must survive. Got: {written}");
        // The child died mid-line, so the frame must open its OWN event: appended straight onto the partial line it
        // would read as "…\"content\":\"heldata: {\"error\"…" and no SSE parser would ever dispatch it.
        AssertEx.Contains(written, "\n\ndata: {\"error\":{\"message\":");
        AssertEx.Contains(written, "\"type\":\"server_error\"");
        AssertEx.True(written.EndsWith("data: [DONE]\n\n", StringComparison.Ordinal),
            $"An SSE client needs the stream terminator after the error frame. Got: {written}");
        AssertEx.False(written.Contains("finish_reason", StringComparison.Ordinal),
            "A finish_reason chunk would claim the generation completed cleanly, which it did not.");
        AssertEx.False(context.RequestAborted.IsCancellationRequested,
            "An event stream is ended deliberately, not aborted — the caller must be able to read the terminal frames.");
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheUpstreamDiesMidNonEventStream_AbortsInsteadOfWritingAFrame()
    {
        // A half-written JSON document cannot be repaired into a valid one, so the only honest end is the same abort
        // the idle watchdog uses.
        using var upstream = new DyingHandler("application/json");
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out var responseBody);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.True(context.RequestAborted.IsCancellationRequested,
            "A truncated JSON body must abort the connection rather than pretend to be a complete document.");
        AssertEx.False(Encoding.UTF8.GetString(responseBody.ToArray()).Contains("\"error\"", StringComparison.Ordinal),
            "No error envelope may be appended to a half-written JSON document.");
    }

    private static CapturingHandler Idle()
    {
        return new CapturingHandler(HttpStatusCode.OK, "application/json", "{}");
    }

    private static LocalModelProxyForwarder CreateForwarder(out IGgufModelStore ggufStore,
        out ILlamaServerProcessSupervisor supervisor,
        HttpMessageHandler upstream,
        TimeSpan? idleTimeout = null)
    {
        ggufStore = Substitute.For<IGgufModelStore>();
        ggufStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<LocalModelDescriptor>>(_ => [InstalledDescriptor()]);

        supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EnsureRunningAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo => new LlamaServerEndpoint(callInfo.ArgAt<string>(0), callInfo.ArgAt<ModelRole>(1), ChildEndpoint));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        // disposeHandler:false — the test owns the handler via `using var`, so the client must not dispose it too.
        httpClientFactory.CreateClient(LocalModelProxyForwarder.HttpClientName)
                         .Returns(_ => new HttpClient(upstream, disposeHandler: false));

        return new LocalModelProxyForwarder(ggufStore, supervisor, httpClientFactory, NullLogger<LocalModelProxyForwarder>.Instance, idleTimeout);
    }

    private static LocalModelDescriptor InstalledDescriptor()
    {
        return new LocalModelDescriptor
        {
            ModelName = InstalledModel,
            ProviderName = LlamaServerProviderConstants.ProviderName,
            IsAvailable = true,
            SizeBytes = 1024,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = 4096
        };
    }

    private static DefaultHttpContext BuildContext(string body, out MemoryStream responseBody)
    {
        var context = new DefaultHttpContext();
        // A lifetime feature so context.Abort() (used by the idle watchdog) is a well-defined no-op under test rather
        // than an NRE, and so RequestAborted is a real, uncancelled token.
        context.Features.Set<IHttpRequestLifetimeFeature>(new StubRequestLifetime());
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = "application/json";
        responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        return context;
    }

    private sealed class StubRequestLifetime : IHttpRequestLifetimeFeature
    {
        private bool _aborted;

        // A fresh read after Abort() reports cancellation; the forwarder captured RequestAborted (uncancelled) at entry,
        // so its in-flight token is unaffected — exactly the real feature's contract, without owning a disposable CTS.
        public CancellationToken RequestAborted
        {
            get => _aborted ? new CancellationToken(canceled: true) : CancellationToken.None;
            set => _ = value;
        }

        public void Abort()
        {
            _aborted = true;
        }
    }

    /// <summary>Stands in for the llama-server child: captures the forwarded request and returns a canned response.</summary>
    private sealed class CapturingHandler(HttpStatusCode statusCode, string contentType, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return response;
        }
    }

    /// <summary>Stands in for a child that exited or refused the connection: the send itself fails.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"));
        }
    }

    /// <summary>Stands in for a child that sends response headers then goes silent without closing the socket.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream())
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }

    /// <summary>Stands in for a child killed MID-RESPONSE (forced eject): some bytes arrive, then the body ends prematurely.</summary>
    private sealed class DyingHandler(string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DyingStream())
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        }
    }

    /// <summary>
    ///     Yields one chunk, then fails the way a killed llama-server's open body does — the exact shape
    ///     <see cref="XE_Local_AI_Engine.Tests.Providers.LlamaServer.DeferredLlamaServerChatClientServerGoneTests" />
    ///     pins as "the server is gone".
    /// </summary>
    private sealed class DyingStream : Stream
    {
        // A PARTIAL SSE line: a child is killed wherever it happens to be, which is far more often mid-line than on
        // an event boundary. Anything appended without its own terminator is swallowed into this broken event.
        internal const string PartialLine = "data: {\"choices\":[{\"delta\":{\"content\":\"hel";

        private static readonly byte[] FirstChunk = Encoding.UTF8.GetBytes(PartialLine);

        private bool _delivered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_delivered)
            {
                throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            }

            _delivered = true;
            FirstChunk.CopyTo(buffer);
            return ValueTask.FromResult(FirstChunk.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>A readable stream whose reads never complete until the read's own token is cancelled (the idle deadline).</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
