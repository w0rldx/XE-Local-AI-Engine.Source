namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.ClientModel;
using System.ClientModel.Primitives;
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
///     <para>
///         AUD4-18: the built client's transport policy is pinned EXPLICITLY rather than left to the System.ClientModel
///         defaults. <c>NetworkTimeout</c> is set from <paramref name="networkTimeout" /> (a generous outer floor — see
///         <see cref="XE_Local_AI_Engine.Providers.LlamaServer.Options.LlamaServerSupervisorOptions.HttpNetworkTimeout" />)
///         so a single call never inherits the SDK's 100 s default and abort a legitimately long local generation, and the
///         <c>RetryPolicy</c> is pinned to <c>ClientRetryPolicy(0)</c> so the SDK NEVER re-issues a request: a local chat
///         completion is non-idempotent and a transient-looking failure must surface, not silently replay a second
///         generation. This mirrors the Codex factory's explicit <c>RetryPolicy = new ClientRetryPolicy(0)</c>.
///     </para>
/// </remarks>
internal static class LlamaServerOpenAIAdapterFactory
{
    /// <summary>Sentinel credential that satisfies the SDK ctor; a localhost llama-server never validates it.</summary>
    private const string IgnoredApiKey = "ignored";

    internal static IChatClient CreateChatClient(Uri baseAddress, string modelId, TimeSpan networkTimeout)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var openAiClient = BuildOpenAIClient(baseAddress, networkTimeout);
        return openAiClient.GetChatClient(modelId).AsIChatClient();
    }

    internal static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseAddress, string modelId, TimeSpan networkTimeout)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var openAiClient = BuildOpenAIClient(baseAddress, networkTimeout);
        return openAiClient.GetEmbeddingClient(modelId).AsIEmbeddingGenerator();
    }

    private static OpenAIClient BuildOpenAIClient(Uri baseAddress, TimeSpan networkTimeout)
    {
        return new OpenAIClient(new ApiKeyCredential(IgnoredApiKey), BuildClientOptions(baseAddress, networkTimeout));
    }

    /// <summary>
    ///     Builds the transport-policy-pinned <see cref="OpenAIClientOptions" /> (AUD4-18): explicit
    ///     <see cref="OpenAIClientOptions.NetworkTimeout" /> (never the SDK's 100 s default) and a
    ///     <c>ClientRetryPolicy(0)</c> so the SDK cannot re-issue a non-idempotent completion. Exposed internally so a test
    ///     can assert the pinned policy directly (per the pipeline-behavior lesson in agent-knowledge §4).
    /// </summary>
    internal static OpenAIClientOptions BuildClientOptions(Uri baseAddress, TimeSpan networkTimeout)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        return new OpenAIClientOptions
        {
            Endpoint = baseAddress,
            // A non-positive value would be rejected by the SDK; the options validator guarantees a positive one, but the
            // guard keeps a direct/test caller from tripping it and makes the "always positive" contract explicit here.
            NetworkTimeout = networkTimeout > TimeSpan.Zero ? networkTimeout : Timeout.InfiniteTimeSpan,
            // Non-idempotent completion: never let the SDK re-issue a request on a transient failure (would duplicate a
            // generation). The stream-idle watchdog / invocation timeout own the real deadlines.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
        };
    }
}
