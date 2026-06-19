namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Default <see cref="ILocalModelProviderResolver" />. Holds the registered provider set keyed by provider name
///     and reads the persisted per-model→provider map through a fresh DI scope per lookup (so a singleton router can
///     consume the scoped <see cref="IModelProviderMapStore" /> safely). Unmapped models route to the configured
///     default provider.
/// </summary>
public sealed class LocalModelProviderResolver : ILocalModelProviderResolver
{
    private readonly string _defaultProviderName;
    private readonly IReadOnlyDictionary<string, ILocalModelProvider> _providersByName;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    ///     Builds the resolver over every registered <see cref="ILocalModelProvider" /> (llama-server + the optional
    ///     Ollama provider), the scope factory used to read the per-model map, the configured default provider for
    ///     unmapped models, and the loaded-process cap surfaced to the preview cap check.
    /// </summary>
    public LocalModelProviderResolver(IEnumerable<ILocalModelProvider> providers,
        IServiceScopeFactory scopeFactory,
        string defaultProviderName,
        int maxLoadedProcesses)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProviderName);

        // Last registration wins per key so a host can override a provider; provider keys are case-insensitive to match
        // LocalModelSelection routing across the persisted map and capability payloads.
        var byName = new Dictionary<string, ILocalModelProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            if (provider is null)
            {
                continue;
            }

            byName[provider.ProviderName] = provider;
        }

        if (byName.Count == 0)
        {
            throw new InvalidOperationException("No ILocalModelProvider is registered; cannot resolve a local model runtime.");
        }

        if (!byName.TryGetValue(defaultProviderName, out var defaultProvider))
        {
            throw new InvalidOperationException($"The configured default local model provider '{defaultProviderName}' is not registered.");
        }

        _providersByName = byName;
        _defaultProviderName = defaultProviderName;
        DefaultProvider = defaultProvider;
        MaxLoadedProcesses = maxLoadedProcesses;
    }

    /// <inheritdoc />
    public int MaxLoadedProcesses { get; }

    /// <inheritdoc />
    public ILocalModelProvider DefaultProvider { get; }

    /// <inheritdoc />
    public async Task<string> ResolveProviderNameForModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var mapStore = scope.ServiceProvider.GetRequiredService<IModelProviderMapStore>();
        var mapped = await mapStore.GetProviderForModelAsync(modelName, cancellationToken).ConfigureAwait(false);

        // An unmapped model routes to the configured default provider; a mapped row wins.
        return string.IsNullOrWhiteSpace(mapped) ? _defaultProviderName : mapped;
    }

    /// <inheritdoc />
    public ILocalModelProvider ResolveProvider(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (_providersByName.TryGetValue(providerName, out var provider))
        {
            return provider;
        }

        throw new InvalidOperationException($"No registered local model provider matches '{providerName}'.");
    }

    /// <inheritdoc />
    public async Task<ILocalModelProvider> ResolveProviderForModelAsync(string modelName, CancellationToken cancellationToken = default)
    {
        var providerName = await ResolveProviderNameForModelAsync(modelName, cancellationToken).ConfigureAwait(false);
        return ResolveProvider(providerName);
    }
}
