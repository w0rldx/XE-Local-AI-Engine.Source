namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using System.Net;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <inheritdoc cref="IModelLabPromptTokensService" />
/// <remarks>
///     Reads the endpoint of the chat process Direct Inference ran on and never ensures one, so no model loads and no GPU
///     gate or invocation slot is taken: rendering and tokenizing run no inference. A process gone mid-call reads as not loaded.
/// </remarks>
public sealed class ModelLabPromptTokensService : IModelLabPromptTokensService
{
    /// <summary>The most tokens one answer lists; the count still covers the whole prompt.</summary>
    internal const int MaxListedTokens = 8_192;

    private readonly IGgufModelStore _ggufModelStore;
    private readonly IModelTrustResolver _modelTrustResolver;
    private readonly ILocalModelProviderResolver _localModelProviderResolver;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly ILlamaServerNativeClient _nativeClient;

    public ModelLabPromptTokensService(IGgufModelStore ggufModelStore,
        IModelTrustResolver modelTrustResolver,
        ILocalModelProviderResolver localModelProviderResolver,
        ILlamaServerProcessSupervisor supervisor,
        ILlamaServerNativeClient nativeClient)
    {
        ArgumentNullException.ThrowIfNull(ggufModelStore);
        ArgumentNullException.ThrowIfNull(modelTrustResolver);
        ArgumentNullException.ThrowIfNull(localModelProviderResolver);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(nativeClient);
        _ggufModelStore = ggufModelStore;
        _modelTrustResolver = modelTrustResolver;
        _localModelProviderResolver = localModelProviderResolver;
        _supervisor = supervisor;
        _nativeClient = nativeClient;
    }

    public async Task<ModelLabPromptTokens?> GetPromptTokensAsync(ModelLabPromptTokensQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var modelName = query.ModelName.Trim();
        if (!await NodeLocalModelGate.IsInstalledNodeLocalLlamaModelAsync(modelName, _ggufModelStore, _modelTrustResolver, _localModelProviderResolver, cancellationToken)
            || _supervisor.FindRunningEndpoint(modelName, ModelRole.Chat) is not { } endpoint)
        {
            return null;
        }

        // The supervisor binds its children to loopback; anything else is not a process this node launched.
        if (!IsLoopbackHttp(endpoint.BaseAddress))
        {
            throw new InvalidOperationException("The running model's endpoint is not a loopback HTTP address.");
        }

        try
        {
            return await TokenizeAsync(endpoint.BaseAddress, query, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && LlamaServerConnectionFailure.IsServerGone(exception))
        {
            return null;
        }
    }

    private async Task<ModelLabPromptTokens> TokenizeAsync(Uri baseAddress, ModelLabPromptTokensQuery query, CancellationToken cancellationToken)
    {
        // As DirectInferenceService builds the run's messages: a blank system prompt sends no system message.
        var systemPrompt = string.IsNullOrWhiteSpace(query.SystemPrompt) ? null : query.SystemPrompt;
        var rendered = await _nativeClient.ApplyTemplateAsync(baseAddress, systemPrompt, query.Prompt, query.DisableThinking, cancellationToken);

        // The flags a chat completion tokenizes its templated prompt with: BOS added, control-token text parsed. A server
        // with a vision projector tokenizes a text-only prompt with the same flags (process_mtmd_prompt), so counts match.
        var tokens = await _nativeClient.TokenizeWithPiecesAsync(baseAddress, rendered, addSpecial: true, parseSpecial: true, cancellationToken);
        var listed = tokens.Count > MaxListedTokens ? tokens.Take(MaxListedTokens).ToArray() : tokens;
        var special = await FindControlTokensAsync(baseAddress, listed, cancellationToken);
        return new ModelLabPromptTokens
        {
            RenderedPrompt = rendered,
            Tokens = [.. listed.Select(token => new ModelLabPromptToken
            {
                Id = token.Id,
                Piece = token.Piece,
                Special = special.Contains(token.Id)
            })],
            TokenCount = tokens.Count,
            Truncated = tokens.Count > listed.Count
        };
    }

    /// <summary>
    ///     The ids among <paramref name="tokens" /> llama.cpp matches only while parsing special tokens: its control tokens.
    ///     Each distinct piece is re-tokenized alone with special parsing off, then on; a control token's text round-trips to its id only with it on.
    /// </summary>
    private async Task<HashSet<int>> FindControlTokensAsync(Uri baseAddress, IReadOnlyList<LlamaServerTokenPiece> tokens, CancellationToken cancellationToken)
    {
        var special = new HashSet<int>();
        var checkedIds = new HashSet<int>();
        foreach (var token in tokens)
        {
            // A byte piece is part of a character, never a control token; the id set bounds the calls to the listed tokens' distinct ids.
            if (token.IsBytes || !checkedIds.Add(token.Id))
            {
                continue;
            }

            if (IsSingle(await _nativeClient.TokenizeWithPiecesAsync(baseAddress, token.Piece, addSpecial: false, parseSpecial: false, cancellationToken), token.Id))
            {
                continue;
            }

            if (IsSingle(await _nativeClient.TokenizeWithPiecesAsync(baseAddress, token.Piece, addSpecial: false, parseSpecial: true, cancellationToken), token.Id))
            {
                special.Add(token.Id);
            }
        }

        return special;
    }

    private static bool IsSingle(IReadOnlyList<LlamaServerTokenPiece> tokens, int id) =>
        tokens.Count == 1 && tokens[0].Id == id;

    private static bool IsLoopbackHttp(Uri address)
    {
        if (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        return string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(address.Host, out var parsed) && IPAddress.IsLoopback(parsed));
    }
}
