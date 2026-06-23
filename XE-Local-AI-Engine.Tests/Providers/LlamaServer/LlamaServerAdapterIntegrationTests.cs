namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Integration: points the MEAI
///     OpenAI adapter at a locally-running <c>llama-server</c> base URL and confirms the <see cref="IChatClient" /> and
///     <see cref="IEmbeddingGenerator{TInput,TEmbedding}" /> round-trip over its <c>/v1/chat/completions</c> +
///     <c>/v1/embeddings</c> surface — with no additional dependency beyond the pinned MEAI OpenAI family.
/// </summary>
/// <remarks>
///     SKIPPED in CI: requires a live llama-server. Set <c>RUN_LLAMASERVER_INTEGRATION=true</c> and provide the chat /
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

        using var client = LlamaServerOpenAIAdapterFactory.CreateChatClient(baseUrl, model);

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

        using var generator = LlamaServerOpenAIAdapterFactory.CreateEmbeddingGenerator(baseUrl, model);

        var embeddings = await generator.GenerateAsync(["llama-server embedding round-trip"], options: null, CancellationToken.None);

        AssertEx.True(embeddings[0].Dimensions > 0, "Expected a non-empty embedding vector.");
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
