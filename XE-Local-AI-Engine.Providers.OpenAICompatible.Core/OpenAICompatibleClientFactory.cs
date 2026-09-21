namespace XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

/// <summary>
///     Builds the MEAI OpenAI <see cref="IChatClient" /> / <see cref="IEmbeddingGenerator{TInput,TEmbedding}" />
///     adapters over ANY OpenAI-compatible <c>…/v1</c> endpoint — the bundled llama-server, or an operator-registered
///     external connection (vLLM, LM Studio, a hosted OpenAI-compatible API).
/// </summary>
/// <remarks>
///     Built on the CHAT-COMPLETIONS surface, not the Responses surface the Codex provider uses, because only
///     <c>POST /v1/chat/completions</c> is universal across OpenAI-compatible servers. Auth is two branches rather than
///     one sentinel — a supplied key rides the standard bearer credential and NO key means no header at all (see
///     <see cref="UnauthenticatedPipelinePolicy" />) — since this factory also serves endpoints that validate what
///     they are sent. The transport policy is pinned explicitly, never left to the System.ClientModel defaults.
/// </remarks>
public static class OpenAICompatibleClientFactory
{
    /// <summary>
    ///     Creates a chat client for <paramref name="modelId" /> at <paramref name="baseAddress" />.
    /// </summary>
    /// <param name="baseAddress">The <c>…/v1</c> base address; the SDK appends the operation path to it.</param>
    /// <param name="modelId">The backing model id sent as the request's <c>model</c> field.</param>
    /// <param name="apiKey">The bearer key, or <see langword="null" />/blank for a keyless endpoint.</param>
    /// <param name="networkTimeout">The outer per-call network timeout.</param>
    /// <param name="transport">The seam for a hardened or capturing <see cref="HttpClient" />; <see langword="null" /> uses the SDK default.</param>
    public static IChatClient CreateChatClient(Uri baseAddress,
        string modelId,
        string? apiKey,
        TimeSpan networkTimeout,
        PipelineTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var openAiClient = CreateClient(baseAddress, apiKey, networkTimeout, transport);
        return openAiClient.GetChatClient(modelId).AsIChatClient();
    }

    /// <summary>
    ///     Creates an embedding generator for <paramref name="modelId" /> at <paramref name="baseAddress" />, on the
    ///     same transport and auth contract as <see cref="CreateChatClient" />.
    /// </summary>
    /// <remarks>
    ///     Its gen_ai span is metadata-only, and <c>EnableSensitiveData</c> is hard-coded false, deliberately NOT
    ///     reading <c>AgentTelemetryOptions</c>: embeddings carry conversation, memory and knowledge-base text this
    ///     node keeps on-box. Setting it explicitly also beats the ambient capture variable Aspire injects as true.
    /// </remarks>
    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseAddress,
        string modelId,
        string? apiKey,
        TimeSpan networkTimeout,
        PipelineTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var openAiClient = CreateClient(baseAddress, apiKey, networkTimeout, transport);

        // Metadata-only gen_ai span under the source name the agent DI pipeline pins, because MEAI's own default is
        // never exported by the ServiceDefaults wildcard. See this member's remarks for the sensitive-data rule.
        return openAiClient.GetEmbeddingClient(modelId)
                           .AsIEmbeddingGenerator()
                           .AsBuilder()
                           .UseOpenTelemetry(sourceName: "Microsoft.Extensions.AI",
                               configure: static openTelemetryGenerator => openTelemetryGenerator.EnableSensitiveData = false)
                           .Build();
    }

    /// <summary>Builds the underlying <see cref="OpenAIClient" />.</summary>
    /// <remarks>
    ///     Exposed so a test can assert the assembled pipeline, and the resulting wire headers, directly rather than
    ///     inferring them — see the pipeline-behavior lesson in <c>docs/agent-knowledge.md</c> §4.
    /// </remarks>
    // OPENAI001: OpenAIClient(AuthenticationPolicy, OpenAIClientOptions) is experimental, but it is the ONLY ctor that
    // puts a caller policy in the SDK's FIXED authentication slot, which is what makes "send no header" expressible.
#pragma warning disable OPENAI001
    public static OpenAIClient CreateClient(Uri baseAddress, string? apiKey, TimeSpan networkTimeout, PipelineTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        var options = BuildClientOptions(baseAddress, networkTimeout, transport);
        return string.IsNullOrWhiteSpace(apiKey)
            ? new OpenAIClient(UnauthenticatedPipelinePolicy.Instance, options)
            : new OpenAIClient(new ApiKeyCredential(apiKey), options);
    }
#pragma warning restore OPENAI001

    /// <summary>
    ///     Builds the transport-policy-pinned <see cref="OpenAIClientOptions" />: an explicit
    ///     <see cref="OpenAIClientOptions.NetworkTimeout" /> (never the SDK's 100 s default) and a
    ///     <c>ClientRetryPolicy(0)</c> so the SDK cannot re-issue a non-idempotent completion.
    /// </summary>
    public static OpenAIClientOptions BuildClientOptions(Uri baseAddress, TimeSpan networkTimeout, PipelineTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);

        var options = new OpenAIClientOptions
        {
            Endpoint = baseAddress,
            // A non-positive value would be rejected by the SDK; callers guarantee a positive one, but the guard keeps a
            // direct/test caller from tripping it and makes the "always positive" contract explicit here.
            NetworkTimeout = networkTimeout > TimeSpan.Zero ? networkTimeout : Timeout.InfiniteTimeSpan,
            // Non-idempotent completion: never let the SDK re-issue a request on a transient failure (would duplicate a
            // generation). The stream-idle watchdog / invocation timeout own the real deadlines.
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
        };

        if (transport is not null)
        {
            options.Transport = transport;
        }

        return options;
    }
}
