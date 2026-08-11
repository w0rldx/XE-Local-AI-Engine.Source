namespace XE_Local_AI_Engine.Tests.Proxy;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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

        await forwarder.WriteModelsAsync(context).ConfigureAwait(false);

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

        await forwarder.ForwardChatCompletionsAsync(context).ConfigureAwait(false);

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

        await forwarder.ForwardChatCompletionsAsync(context).ConfigureAwait(false);

        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Test]
    public async Task ForwardChatCompletions_WithAnUnknownModel_Returns404AndNeverProvisionsOrForwards()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        // A cloud-ish / unmapped model name is exactly the case that must never reach any other provider.
        var context = BuildContext("{\"model\":\"gpt-5.6-terra\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context).ConfigureAwait(false);

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

        await forwarder.ForwardChatCompletionsAsync(context).ConfigureAwait(false);

        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Test]
    public async Task ForwardChatCompletions_WithMalformedJson_Returns400()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("not json at all", out _);

        await forwarder.ForwardChatCompletionsAsync(context).ConfigureAwait(false);

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

        await forwarder.ForwardChatCompletionsAsync(context).ConfigureAwait(false);

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

        await forwarder.ForwardChatCompletionsAsync(context).ConfigureAwait(false);

        AssertEx.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        AssertEx.Null(upstream.LastRequest);
    }

    [Test]
    public async Task ForwardEmbeddings_TargetsTheEmbeddingRoleAndTheEmbeddingsSubpath()
    {
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", "{\"data\":[]}");
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.EnsureRunningAsync(InstalledModel, ModelRole.Embedding, Arg.Any<CancellationToken>())
                  .Returns(new LlamaServerEndpoint(InstalledModel, ModelRole.Embedding, ChildEndpoint));
        var context = BuildContext("{\"model\":\"test-model\",\"input\":\"hi\"}", out _);

        await forwarder.ForwardEmbeddingsAsync(context).ConfigureAwait(false);

        _ = supervisor.Received(1).EnsureRunningAsync(InstalledModel, ModelRole.Embedding, Arg.Any<CancellationToken>());
        AssertEx.Equal("http://127.0.0.1:18100/v1/embeddings", upstream.LastRequest!.RequestUri!.AbsoluteUri);
    }

    private static CapturingHandler Idle()
    {
        return new CapturingHandler(HttpStatusCode.OK, "application/json", "{}");
    }

    private static LocalModelProxyForwarder CreateForwarder(out IGgufModelStore ggufStore,
        out ILlamaServerProcessSupervisor supervisor,
        CapturingHandler upstream)
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

        return new LocalModelProxyForwarder(ggufStore, supervisor, httpClientFactory, TimeProvider.System, NullLogger<LocalModelProxyForwarder>.Instance);
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
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = "application/json";
        responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        return context;
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
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return response;
        }
    }
}
