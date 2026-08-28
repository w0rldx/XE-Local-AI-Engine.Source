namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using Microsoft.Extensions.AI;
using OpenAI;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>
///     Builds the MEAI OpenAI <see cref="IChatClient" /> / <see cref="IEmbeddingGenerator{TInput,TEmbedding}" />
///     adapters over a llama-server endpoint's OpenAI-compatible base URL.
/// </summary>
/// <remarks>
///     <para>
///         The construction itself — chat-completions surface, pinned network timeout, no-retry policy — is the shared
///         <see cref="OpenAICompatibleClientFactory" />, which serves every OpenAI-compatible endpoint the node talks
///         to. What stays HERE is the one thing that is llama-server-specific: the credential.
///     </para>
///     <para>
///         The API key is irrelevant for llama-server — a local instance ignores it, so a fixed sentinel satisfies the
///         SDK ctor and never reaches a real provider (the endpoint is localhost-bound). That sentinel is deliberately
///         NOT the shared factory's default: an operator-registered external endpoint may genuinely validate the
///         <c>Authorization</c> header it is sent, so the shared factory sends NO header when it has no key, and only
///         this llama-server path opts into the sentinel.
///     </para>
///     <para>
///         The <see cref="OpenAIClientOptions.Endpoint" /> is the <c>…/v1</c> base address the supervisor resolved; the
///         SDK appends the operation path (e.g. <c>/chat/completions</c>) to it.
///     </para>
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
    ///     The transport-policy-pinned <see cref="OpenAIClientOptions" /> the adapters above are built with. Exposed
    ///     internally so a test can assert the pinned policy directly (per the pipeline-behavior lesson in
    ///     agent-knowledge §4).
    /// </summary>
    internal static OpenAIClientOptions BuildClientOptions(Uri baseAddress, TimeSpan networkTimeout)
    {
        return OpenAICompatibleClientFactory.BuildClientOptions(baseAddress, networkTimeout);
    }
}
