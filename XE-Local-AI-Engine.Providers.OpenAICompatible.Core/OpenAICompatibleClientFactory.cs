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
///     <para>
///         The adapters are built off <c>OpenAIClient.GetChatClient(modelId).AsIChatClient()</c> and
///         <c>GetEmbeddingClient(modelId).AsIEmbeddingGenerator()</c> — the CHAT-COMPLETIONS surface, not the Responses
///         surface the Codex provider uses. Only <c>POST /v1/chat/completions</c> is universal across
///         OpenAI-compatible servers; the Responses API is effectively OpenAI-only.
///     </para>
///     <para>
///         Auth is a deliberate two-branch decision rather than a single sentinel: a supplied key rides the SDK's
///         standard <c>Authorization: Bearer</c> credential, and NO key means no header at all (see
///         <see cref="UnauthenticatedPipelinePolicy" />). A shared "ignored" sentinel would be wrong here — this
///         factory also serves endpoints that genuinely validate the header they are sent.
///     </para>
///     <para>
///         The transport policy is pinned EXPLICITLY rather than left to the System.ClientModel defaults.
///         <c>NetworkTimeout</c> comes from the caller (a generous outer floor) so a single call never inherits the
///         SDK's 100 s default and aborts a legitimately long generation, and <c>RetryPolicy</c> is pinned to
///         <c>ClientRetryPolicy(0)</c> so the SDK NEVER re-issues a request: a chat completion is non-idempotent and a
///         transient-looking failure must surface rather than silently produce a second generation.
///     </para>
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
    /// <param name="transport">
    ///     An explicit transport — the seam through which a caller injects a hardened <see cref="HttpClient" /> (an
    ///     outbound-guard handler, a connect timeout) or a test's capturing handler. <see langword="null" /> uses the
    ///     SDK default transport.
    /// </param>
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
    ///     Creates an embedding generator for <paramref name="modelId" /> at <paramref name="baseAddress" />. Same
    ///     transport and auth contract as <see cref="CreateChatClient" />.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(Uri baseAddress,
        string modelId,
        string? apiKey,
        TimeSpan networkTimeout,
        PipelineTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var openAiClient = CreateClient(baseAddress, apiKey, networkTimeout, transport);
        return openAiClient.GetEmbeddingClient(modelId).AsIEmbeddingGenerator();
    }

    /// <summary>
    ///     Builds the underlying <see cref="OpenAIClient" />. Exposed so a test can assert the assembled pipeline (and
    ///     the resulting wire headers) directly rather than inferring them — see the pipeline-behavior lesson in
    ///     <c>docs/agent-knowledge.md</c> §4.
    /// </summary>
    // OPENAI001: OpenAIClient(AuthenticationPolicy, OpenAIClientOptions) is experimental. It is the ONLY constructor
    // that puts a caller-supplied policy in the SDK's FIXED authentication slot, which is what makes "send no
    // Authorization header at all" expressible; every other route leaves the SDK's placeholder-credential policy as
    // the last writer. Same scoped-suppression pattern the Azure Foundry v1 builders use.
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
