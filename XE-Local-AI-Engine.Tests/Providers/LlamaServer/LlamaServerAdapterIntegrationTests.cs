namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Extensions.AI;
using OpenAI;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Integration: points the MEAI
///     OpenAI adapter at a locally-running <c>llama-server</c> base URL and confirms the <see cref="IChatClient" /> and
///     <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> round-trip over its <c>/v1/chat/completions</c> +
///     <c>/v1/embeddings</c> surface — with no additional dependency beyond the pinned MEAI OpenAI family.
/// </summary>
/// <remarks>
///     The two round-trip tests are SKIPPED in CI (they require a live llama-server); the transport-policy tests
///     (<c>BuildClientOptions_*</c> / <c>BuiltClient_DoesNotRetry_*</c>) run everywhere — they assert the AUD4-18 pinned
///     NetworkTimeout + no-retry policy without a server. Set <c>RUN_LLAMASERVER_INTEGRATION=true</c> and provide the chat /
///     embedding base URLs + model ids (<c>LLAMASERVER_CHAT_BASEURL</c>, <c>LLAMASERVER_CHAT_MODEL</c>,
///     <c>LLAMASERVER_EMBED_BASEURL</c>, <c>LLAMASERVER_EMBED_MODEL</c>; base URLs default to
///     <c>http://127.0.0.1:18100/v1</c> / <c>:18101/v1</c>) to execute it. The chat process must be launched with
///     <c>--jinja</c> and the embedding process with a non-<c>none</c> pooling type.
/// </remarks>
public sealed class LlamaServerAdapterIntegrationTests
{
    [Test]
    public async Task ChatClient_RoundTrips_AgainstLocalLlamaServer()
    {
        SkipUnlessEnabled();

        var baseUrl = ResolveBaseUrl("LLAMASERVER_CHAT_BASEURL", "http://127.0.0.1:18100/v1");
        var model = ResolveModel("LLAMASERVER_CHAT_MODEL");

        using var client = LlamaServerOpenAIAdapterFactory.CreateChatClient(baseUrl, model, TimeSpan.FromSeconds(600));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Reply with the single word: pong.")],
            options: null,
            CancellationToken.None);

        AssertEx.NotNullOrEmpty(response.Text);
    }

    [Test]
    public async Task EmbeddingGenerator_RoundTrips_AgainstLocalLlamaServer()
    {
        SkipUnlessEnabled();

        var baseUrl = ResolveBaseUrl("LLAMASERVER_EMBED_BASEURL", "http://127.0.0.1:18101/v1");
        var model = ResolveModel("LLAMASERVER_EMBED_MODEL");

        using var generator = LlamaServerOpenAIAdapterFactory.CreateEmbeddingGenerator(baseUrl, model, TimeSpan.FromSeconds(600));

        var embeddings = await generator.GenerateAsync(["llama-server embedding round-trip"], options: null, CancellationToken.None);

        AssertEx.True(embeddings[0].Dimensions > 0, "Expected a non-empty embedding vector.");
    }

    [Test]
    public void BuildClientOptions_PinsNetworkTimeoutAndDisablesRetries()
    {
        var options = LlamaServerOpenAIAdapterFactory.BuildClientOptions(new Uri("http://127.0.0.1:18100/v1"), TimeSpan.FromSeconds(600));

        // NetworkTimeout is pinned from the supplied value, NOT left to the SDK's 100 s default.
        AssertEx.Equal(TimeSpan.FromSeconds(600), options.NetworkTimeout);
        // A retry policy is set EXPLICITLY (the default is a retrying ClientRetryPolicy); the no-retry behavior is asserted
        // end-to-end below, since ClientRetryPolicy does not expose its max-retry count publicly.
        AssertEx.NotNull(options.RetryPolicy);
    }

    [Test]
    public async Task BuiltClient_DoesNotRetry_OnRetryableFailure()
    {
        // Fire the assembled pipeline through a capturing transport (the agent-knowledge §4 pattern): a 503 is a
        // classically retryable status, so a DEFAULT retry policy would re-issue it. With ClientRetryPolicy(0) the
        // transport must be hit EXACTLY ONCE — proving a non-idempotent completion is never silently replayed.
        using var handler = new CountingHandler(HttpStatusCode.ServiceUnavailable);
        using var httpClient = new HttpClient(handler, disposeHandler: false);
        var options = LlamaServerOpenAIAdapterFactory.BuildClientOptions(new Uri("http://127.0.0.1:18100/v1"), TimeSpan.FromSeconds(30));
        options.Transport = new HttpClientPipelineTransport(httpClient);

        var client = new OpenAIClient(new ApiKeyCredential("ignored"), options).GetChatClient("local-model").AsIChatClient();

        _ = await AssertEx.ThrowsAsync<ClientResultException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        AssertEx.Equal(1, handler.CallCount);
    }

    private sealed class CountingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("{}"),
                RequestMessage = request
            });
        }
    }

    private static void SkipUnlessEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("RUN_LLAMASERVER_INTEGRATION"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            Skip.Test("Set RUN_LLAMASERVER_INTEGRATION=true (and a live llama-server) to execute this integration test.");
        }
    }

    private static Uri ResolveBaseUrl(string environmentVariable, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        return new Uri(string.IsNullOrWhiteSpace(configured) ? fallback : configured, UriKind.Absolute);
    }

    private static string ResolveModel(string environmentVariable)
    {
        var configured = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(configured) ? "local-model" : configured;
    }
}
