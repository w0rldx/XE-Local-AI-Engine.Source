namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The single local-branch <see cref="IChatClient" /> that replaces the
///     fixed-model local client. Per request it reads <see cref="ChatOptions.ModelId" />, resolves that model to its
///     provider through <see cref="ILocalModelProviderResolver" /> (persisted map + default), asks the provider for a
///     model-specific chat client, and delegates. For llama-server the provider hands back a <em>deferred</em> client
///     that ensure-runs the right per-model process on first use; for Ollama it hands back the single-daemon client
///     with ModelId hot-swap. Switching model mid-session therefore reaches a different process/client without a node
///     restart.
/// </summary>
/// <remarks>
///     <para>
///         Singleton — matches <see cref="RuntimeChatClient" />'s lifetime and sits behind its local branch. The
///         per-(provider, model) chat clients are cached so repeated sends to the same model reuse one deferred client
///         (which owns the single-flight cold-start). The router builds the request's effective ModelId once and keys
///         the cache on (provider, model).
///     </para>
///     <para>
///         <strong>Dispose ownership:</strong> the resolved/cached chat clients are owned by the provider stack (the
///         llama-server supervisor owns the underlying processes; the deferred client owns only its inner adapter) and
///         are NOT disposed at this boundary — mirror <see cref="RuntimeChatClient" />'s ownership note. This router is
///         disposed by the node host on shutdown; it disposes the deferred clients it cached then (their inner adapters,
///         not the supervisor processes).
///     </para>
/// </remarks>
public sealed class ModelRoutingLocalChatClient : IChatClient
{
    private const string ResolvedClientOwnershipNote =
        "Resolved chat clients are cached and owned by this router (disposed in Dispose); the underlying model "
        + "processes are owned by the provider/supervisor. Disposing a resolved client per-call would be incorrect.";

    private readonly ConcurrentDictionary<(string Provider, string Model), IChatClient> _clientsByProviderAndModel = new();
    private readonly string _defaultModelName;

    private readonly ILocalModelProviderResolver _resolver;

    private int _disposed;

    /// <summary>
    ///     Creates the router over the provider resolver and the default model used when a request omits
    ///     <see cref="ChatOptions.ModelId" /> (mirrors the previous fixed-model local client's configured default).
    /// </summary>
    public ModelRoutingLocalChatClient(ILocalModelProviderResolver resolver, string defaultModelName)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultModelName);
        _defaultModelName = defaultModelName;
    }

    /// <inheritdoc />
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = ResolvedClientOwnershipNote)]
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var client = await ResolveClientAsync(options, cancellationToken).ConfigureAwait(false);
        return await client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = ResolvedClientOwnershipNote)]
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var client = await ResolveClientAsync(options, cancellationToken).ConfigureAwait(false);
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        // The router itself satisfies an IChatClient/router request; there is no single active inner client to forward
        // metadata to (the active model is per-request), so other service lookups return null.
        return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Dispose the cached deferred clients (their inner adapters). The underlying model processes are owned by the
        // supervisor and are torn down by its own shutdown, not here.
        foreach (var client in _clientsByProviderAndModel.Values)
        {
            client.Dispose();
        }

        _clientsByProviderAndModel.Clear();
    }

    private async Task<IChatClient> ResolveClientAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        var modelName = string.IsNullOrWhiteSpace(options?.ModelId) ? _defaultModelName : options!.ModelId!;
        var providerName = await _resolver.ResolveProviderNameForModelAsync(modelName, cancellationToken).ConfigureAwait(false);

        var cacheKey = (Provider: providerName, Model: modelName);
        if (_clientsByProviderAndModel.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var provider = _resolver.ResolveProvider(providerName);
        var created = provider.CreateChatClient(new LocalModelSelection
        {
            ModelName = modelName,
            ProviderName = providerName
        });

        // GetOrAdd may race with a concurrent first send for the same (provider, model); keep the first winner and
        // dispose the loser so we never leak a deferred client. The deferred client's own single-flight ensures the
        // backing process is started at most once regardless.
        var stored = _clientsByProviderAndModel.GetOrAdd(cacheKey, created);
        if (!ReferenceEquals(stored, created))
        {
            created.Dispose();
        }

        return stored;
    }
}
