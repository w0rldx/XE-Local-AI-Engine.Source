namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;

/// <summary>
///     Builds the MEAI OpenAI <see cref="IChatClient" /> / <see cref="IEmbeddingGenerator{TInput,TEmbedding}" />
///     adapters over a llama-server endpoint's OpenAI-compatible base URL.
/// </summary>
/// <remarks>
///     <para>
///         llama-server exposes the OpenAI <em>chat-completions</em> surface (<c>/v1/chat/completions</c> +
///         <c>/v1/embeddings</c>), so the adapters are built off
///         <c>OpenAIClient.GetChatClient(modelId).AsIChatClient()</c> and
///         <c>GetEmbeddingClient(modelId).AsIEmbeddingGenerator()</c> — NOT the Responses adapter the Codex provider
///         uses. Verified against the pinned <c>Microsoft.Extensions.AI.OpenAI</c> 10.6.0 / <c>OpenAI</c> 2.9.1
///         surface (2026-06-18): the v10.6 extension method names are <c>AsIChatClient</c> / <c>AsIEmbeddingGenerator</c>.
///     </para>
///     <para>
///         The API key is irrelevant — a local llama-server ignores it, so a fixed sentinel satisfies the SDK ctor and
///         never reaches a real provider (the endpoint is localhost-bound). The
///         <see cref="OpenAIClientOptions.Endpoint" /> is the <c>…/v1</c> base address; the SDK appends the operation
///         path (e.g. <c>/chat/completions</c>) to it.
///     </para>
/// </remarks>
internal static class LlamaServerOpenAIAdapterFactory
{
    /// <summary>Sentinel credential that satisfies the SDK ctor; a localhost llama-server never validates it.</summary>
    private const string IgnoredApiKey = "ignored";

    internal static IChatClient CreateChatClient(Uri baseAddress, string modelId)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var openAiClient = BuildOpenAIClient(baseAddress);
        return openAiClient.GetChatClient(modelId).AsIChatClient();
    }

    internal static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseAddress, string modelId)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var openAiClient = BuildOpenAIClient(baseAddress);
        return openAiClient.GetEmbeddingClient(modelId).AsIEmbeddingGenerator();
    }

    private static OpenAIClient BuildOpenAIClient(Uri baseAddress)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = baseAddress
        };
        return new OpenAIClient(new ApiKeyCredential(IgnoredApiKey), options);
    }
}
