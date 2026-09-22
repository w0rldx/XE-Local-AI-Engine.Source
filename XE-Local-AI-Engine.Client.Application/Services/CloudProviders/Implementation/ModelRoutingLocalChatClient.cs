namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     The single local-branch <see cref="IChatClient" />: per request it reads <see cref="ChatOptions.ModelId" />,
///     resolves that model to its provider, asks the provider for a model-specific chat client, and delegates.
/// </summary>
/// <remarks>
///     Resolution runs through <see cref="ILocalModelProviderResolver" /> (persisted map plus default), so switching model
///     mid-session reaches a different process or client without a node restart. Singleton, matching
///     <see cref="RuntimeChatClient" />'s lifetime and sitting behind its local branch; per-(provider, model) clients are
///     cached. <strong>Dispose ownership:</strong> the cached clients are disposed only when the node host disposes this
///     router — their inner adapters, never the supervisor's processes. Routing shape: docs/wiki/03-local-runtime-and-providers.md, "The local routing client".
/// </remarks>
public sealed class ModelRoutingLocalChatClient : IChatClient, ILocalChatClientCacheInvalidator
{
    private const string ResolvedClientOwnershipNote =
        "Resolved chat clients are cached and owned by this router (disposed in Dispose); the underlying model "
        + "processes are owned by the provider/supervisor. Disposing a resolved client per-call would be incorrect.";

    private readonly ConcurrentDictionary<ProviderModelKey, IChatClient> _clientsByProviderAndModel = new();
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
        var client = await ResolveClientAsync(options, cancellationToken);
        return await client.GetResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc />
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = ResolvedClientOwnershipNote)]
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        var client = await ResolveClientAsync(options, cancellationToken);
        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken))
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
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
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

    /// <inheritdoc />
    public void ClearClientCache()
    {
        // Atomically swap out the cached deferred clients and dispose them: a cleared one holds an inner adapter pointed at
        // an endpoint that may be gone (the operator switched the llama.cpp runtime variant). The next send re-resolves against the current binary; the supervisor's processes are unaffected.
        foreach (var key in _clientsByProviderAndModel.Keys)
        {
            if (_clientsByProviderAndModel.TryRemove(key, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    private async Task<IChatClient> ResolveClientAsync(ChatOptions? options, CancellationToken cancellationToken)
    {
        var modelName = string.IsNullOrWhiteSpace(options?.ModelId) ? _defaultModelName : options.ModelId;
        var providerName = await _resolver.ResolveProviderNameForModelAsync(modelName, cancellationToken);
        if (NodeManagedLlamaRoutingScope.CurrentModel is { } requiredModel
            && (!string.Equals(modelName, requiredModel, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(providerName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The invocation model is no longer routed through the node-managed llama provider.");
        }

        var cacheKey = new ProviderModelKey(providerName, modelName);
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

        // GetOrAdd may race with a concurrent first send for the same (provider, model): keep the first winner and dispose
        // the loser so no deferred client leaks. The deferred client's own single-flight starts the backing process at most once regardless.
        var stored = _clientsByProviderAndModel.GetOrAdd(cacheKey, created);
        if (!ReferenceEquals(stored, created))
        {
            created.Dispose();
        }

        return stored;
    }

    // Cache key for a resolved chat client: one client per (provider, model) pair, since the same model name can be
    // served by different providers.
    private readonly record struct ProviderModelKey(string Provider, string Model);
}
