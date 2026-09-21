namespace XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

using Microsoft.Extensions.AI;

/// <summary>
///     Wraps the inner Responses <see cref="IChatClient" /> to enforce <c>store=false</c> on every call, pin the
///     request to a VALID Codex model id, and protect the factory's shared <see cref="HttpClient" /> from disposal.
/// </summary>
/// <remarks>
///     store=false is mandatory for the transport-only boundary, so each call's
///     <see cref="ChatOptions.RawRepresentationFactory" /> is set unconditionally. MEAI's Responses adapter prefers the
///     per-call <see cref="ChatOptions.ModelId" /> over the one the client was built with, so a locally-selected model
///     name leaking through would be rejected with HTTP 400 — hence the OVERWRITE. Disposal is left to the base
///     <see cref="DelegatingChatClient" />, since <c>HttpClientPipelineTransport</c> takes no ownership of the client.
/// </remarks>
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
        // Resolve the per-send effort from the INCOMING options so the store-disabling base options request reasoning
        // summaries at it. Codex-only: the local/Ollama path does not pass through this wrapper.
        var reasoningEffort = CodexResponseStoreDisabling.ResolveReasoningEffort(options);

        var result = CodexResponseStoreDisabling.WithStoredOutputDisabled(options?.Clone(), reasoningEffort);

        // Pin to a valid Codex model id, overwriting any local model name the agent send path forwarded (400 fix).
        result.ModelId = _modelId;

        // The subscription Codex backend matches the Codex CLI, which sends NO max_output_tokens (the opencode
        // reference strips it), so a local sampling override is cleared here rather than risk a rejection.
        result.MaxOutputTokens = null;
        return result;
    }

    /// <summary>
    ///     Builds the Codex-safe messages-and-options pair: applies <see cref="ApplyStoreDisabled" />, then moves any
    ///     system-role messages into the top-level Responses <c>instructions</c> field.
    /// </summary>
    /// <remarks>
    ///     The subscription Codex backend REJECTS system-role messages in the request input, and the Codex CLI and
    ///     opencode reference pass the system prompt through <c>instructions</c> instead, so every
    ///     <see cref="ChatRole.System" /> message's text is appended to <see cref="ChatOptions.Instructions" /> — which
    ///     MEAI's Responses adapter maps to that field — and removed from the input. Codex-side only: the local and
    ///     Ollama path does not go through this wrapper and keeps its system messages.
    /// </remarks>
    private CodexRequest PrepareCodexRequest(IEnumerable<ChatMessage> messages,
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
            return new CodexRequest(materialized, result);
        }

        result.Instructions = string.Join("\n\n",
            new[]
            {
                result.Instructions
            }.Concat(systemTexts).Where(text => !string.IsNullOrWhiteSpace(text)));

        var withoutSystem = materialized.Where(message => message.Role != ChatRole.System).ToList();
        return new CodexRequest(withoutSystem, result);
    }

    /// <summary>The Codex-safe request pair: the input messages with system roles lifted out, and the adjusted options.</summary>
    private sealed record CodexRequest(IEnumerable<ChatMessage> Messages, ChatOptions Options);

    // Dispose is left to the base DelegatingChatClient: the inner client does NOT own the factory's shared HttpClient,
    // so the shared client and handler are never torn down by disposing this wrapper.
}
