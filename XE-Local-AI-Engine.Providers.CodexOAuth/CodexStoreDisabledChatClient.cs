namespace XE_Local_AI_Engine.Providers.CodexOAuth;

using Microsoft.Extensions.AI;

/// <summary>
///     Wraps the inner Responses <see cref="IChatClient" /> to enforce <c>store=false</c> on every call,
///     pin the request to a VALID Codex model id, and protect the factory's shared <see cref="HttpClient" /> from
///     disposal.
///     <para>
///         store=false is mandatory for the transport-only boundary, so it is applied unconditionally: each call's
///         <see cref="ChatOptions.RawRepresentationFactory" /> is set to the store-disabling factory
///         (<see cref="CodexResponseStoreDisabling" />), which MEAI's Responses mapper uses as the base request options.
///     </para>
///     <para>
///         <b>Model pinning (400 fix):</b> the agent send path sets <see cref="ChatOptions.ModelId" /> to the node's
///         LOCALLY-selected model (e.g. an Ollama model name such as <c>qwen3:8b</c>) because the local/Ollama transport
///         needs it. MEAI's Responses adapter uses that per-call <see cref="ChatOptions.ModelId" /> in preference to the
///         model the client was constructed with, so a leaked local name would reach the Codex backend as the request
///         model and be rejected with HTTP 400 (unknown model). This wrapper therefore OVERWRITES
///         <see cref="ChatOptions.ModelId" /> with the resolved Codex model id on every call, so only a valid Codex model
///         id can ever reach <c>chatgpt.com/backend-api/codex/responses</c> — regardless of what the upstream agent
///         forwarded. The local/Ollama path is unaffected because it does not go through this Codex-only wrapper.
///     </para>
///     <para>
///         Disposal is left to the base <see cref="DelegatingChatClient" />: the factory's shared <see cref="HttpClient" />
///         and <c>CodexAuthHandler</c> are owned by the factory and are not torn down by disposing this wrapper, because
///         <c>HttpClientPipelineTransport</c> does not take ownership of the supplied client.
///     </para>
/// </summary>
internal sealed class CodexStoreDisabledChatClient : DelegatingChatClient
{
    private readonly string _modelId;

    public CodexStoreDisabledChatClient(IChatClient innerClient, string modelId)
        : base(innerClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        _modelId = modelId;
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (codexMessages, codexOptions) = PrepareCodexRequest(messages, options);
        return base.GetResponseAsync(codexMessages, codexOptions, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (codexMessages, codexOptions) = PrepareCodexRequest(messages, options);
        return base.GetStreamingResponseAsync(codexMessages, codexOptions, cancellationToken);
    }

    private ChatOptions ApplyStoreDisabled(ChatOptions? options)
    {
        // Resolve the per-send reasoning effort from the INCOMING options' AdditionalProperties (the Codex side channel,
        // falling back to the Ollama-shaped think value) so the store-disabling base options also request reasoning
        // summaries at that effort. Codex-only: the local/Ollama path does not pass through this wrapper.
        var reasoningEffort = CodexResponseStoreDisabling.ResolveReasoningEffort(options);

        var result = CodexResponseStoreDisabling.WithStoredOutputDisabled(options?.Clone(), reasoningEffort);

        // Pin to a valid Codex model id, overwriting any local model name the agent send path forwarded (400 fix).
        result.ModelId = _modelId;

        // The ChatGPT-subscription Codex backend matches the Codex CLI, which sends NO max_output_tokens (the
        // opencode reference strips it: output.maxOutputTokens = undefined). A developer-gated MaxOutputTokens
        // override that rode in from the local sampling path could be rejected here, so it is cleared on the
        // Codex-only boundary. The local/Ollama path keeps its MaxOutputTokens (it does not pass through here).
        result.MaxOutputTokens = null;
        return result;
    }

    /// <summary>
    ///     Builds the Codex-safe (messages, options) pair. Applies the store-disabled + model-pin + max-tokens options
    ///     (<see cref="ApplyStoreDisabled" />), then moves any system-role messages into the top-level Responses
    ///     <c>instructions</c> field.
    ///     <para>
    ///         <b>System-message 400 fix:</b> the ChatGPT-subscription Codex backend rejects system-role messages in the
    ///         request input (<c>{"detail":"System messages are not allowed"}</c>). The Codex CLI / opencode reference pass
    ///         the system prompt via the top-level <c>instructions</c> field instead. So every <see cref="ChatRole.System" />
    ///         message's text is appended to <see cref="ChatOptions.Instructions" /> (which MEAI's Responses adapter maps to
    ///         <c>instructions</c>) and the system messages are removed from the input. Codex-side only — the local/Ollama
    ///         path does not go through this wrapper and keeps its system messages.
    ///     </para>
    /// </summary>
    private (IEnumerable<ChatMessage> Messages, ChatOptions Options) PrepareCodexRequest(IEnumerable<ChatMessage> messages,
        ChatOptions? options)
    {
        var result = ApplyStoreDisabled(options);

        var materialized = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        var systemTexts = materialized
                          .Where(message => message.Role == ChatRole.System)
                          .Select(message => message.Text)
                          .Where(text => !string.IsNullOrWhiteSpace(text))
                          .ToList();

        if (systemTexts.Count == 0)
        {
            return (materialized, result);
        }

        result.Instructions = string.Join("\n\n",
            new[]
            {
                result.Instructions
            }.Concat(systemTexts).Where(text => !string.IsNullOrWhiteSpace(text)));

        var withoutSystem = materialized.Where(message => message.Role != ChatRole.System).ToList();
        return (withoutSystem, result);
    }

    // Dispose is left to the base DelegatingChatClient: the inner MEAI/Responses client does NOT own the
    // factory's shared HttpClient (HttpClientPipelineTransport does not take ownership), so the shared
    // client/handler the factory owns is never torn down by disposing this wrapper.
}
