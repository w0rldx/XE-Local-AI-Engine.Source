namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.AI;
using OpenAI;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>
///     Builds the MEAI OpenAI <see cref="IChatClient" /> / <see cref="IEmbeddingGenerator{TInput,TEmbedding}" />
///     adapters over a llama-server endpoint's OpenAI-compatible base URL.
/// </summary>
/// <remarks>
///     Construction itself — chat-completions surface, pinned network timeout, no-retry policy — is the shared
///     <see cref="OpenAICompatibleClientFactory" /> serving every OpenAI-compatible endpoint the node talks to; what
///     stays HERE is the one llama-server-specific thing, the credential. A local instance ignores the API key, so a
///     fixed sentinel satisfies the SDK ctor and, the endpoint being localhost-bound, never reaches a real provider. It
///     is NOT the shared factory's default, which sends no header at all: an external endpoint may validate one.
/// </remarks>
internal static class LlamaServerOpenAIAdapterFactory
{
    /// <summary>Sentinel credential that satisfies the SDK ctor; a localhost llama-server never validates it.</summary>
    private const string IgnoredApiKey = "ignored";

    internal static IChatClient CreateChatClient(Uri baseAddress, string modelId, TimeSpan networkTimeout)
    {
        return OpenAICompatibleClientFactory.CreateChatClient(baseAddress, modelId, IgnoredApiKey, networkTimeout);
    }

    internal static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseAddress, string modelId, TimeSpan networkTimeout)
    {
        return OpenAICompatibleClientFactory.CreateEmbeddingGenerator(baseAddress, modelId, IgnoredApiKey, networkTimeout);
    }

    /// <summary>
    ///     The transport-policy-pinned <see cref="OpenAIClientOptions" /> the adapters above are built with, exposed
    ///     internally so a test can assert the pinned policy directly.
    /// </summary>
    /// <remarks>
    ///     <paramref name="baseAddress" /> becomes <see cref="OpenAIClientOptions.Endpoint" />: the <c>…/v1</c> base the
    ///     supervisor resolved, onto which the SDK appends the operation path such as <c>/chat/completions</c>.
    /// </remarks>
    internal static OpenAIClientOptions BuildClientOptions(Uri baseAddress, TimeSpan networkTimeout)
    {
        return OpenAICompatibleClientFactory.BuildClientOptions(baseAddress, networkTimeout);
    }
}
