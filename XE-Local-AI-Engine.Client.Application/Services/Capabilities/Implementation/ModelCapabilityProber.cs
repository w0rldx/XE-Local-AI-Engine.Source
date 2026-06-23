namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

using System.Data.Common;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Probes the node-local model runtime through the provider-neutral <see cref="IModelCapabilityClient" />:
///     installed-model inventory (short-lived cache), per-model context length (digest-keyed cache), runtime
///     reachability/version, and the active/running model. Collaborator behind <see cref="CapabilityReporter" />;
///     owns the probe caches and the configured-model fallback list.
/// </summary>
internal sealed class ModelCapabilityProber
{
    private const string DiagnosticOllamaUnreachable = "ollama-unreachable";
    private static readonly TimeSpan InstalledModelsCacheLifetime = TimeSpan.FromSeconds(10);

    private static readonly string[] ConfiguredModelKeys =
    [
        "Agent:LocalChat:DefaultModel",
        "Ollama:ChatModel",
        "Aspire:OllamaSharp:chat:SelectedModel",
        "Aspire:OllamaSharp:embeddings:SelectedModel"
    ];

    private static readonly string[] ModelConnectionStringNames = ["chat", "embeddings"];

    private readonly IReadOnlyList<string> _configuredModelNames;
    private readonly Lock _installedModelsCacheSync = new();
    private readonly ILogger<ModelCapabilityProber> _logger;
    private readonly IModelCapabilityClient _modelCapabilityClient;
    private readonly Dictionary<ModelContextCacheKey, int?> _modelContextCache = new();
    private readonly Lock _modelContextCacheSync = new();
    private readonly TimeProvider _timeProvider;
    private CachedInstalledModels? _installedModelsCache;

    public ModelCapabilityProber(IModelCapabilityClient modelCapabilityClient,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<ModelCapabilityProber> logger)
    {
        _modelCapabilityClient = modelCapabilityClient ?? throw new ArgumentNullException(nameof(modelCapabilityClient));
        ArgumentNullException.ThrowIfNull(configuration);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuredModelNames = ResolveConfiguredModelNames(configuration);
    }

    /// <summary>Checks whether the model runtime endpoint is reachable; propagates transport failures.</summary>
    public Task<bool> IsRuntimeReachableAsync(CancellationToken cancellationToken)
    {
        return _modelCapabilityClient.IsRuntimeReachableAsync(cancellationToken);
    }

    /// <summary>Returns the installed model names (discovered + configured fallbacks), using the inventory cache.</summary>
    public async Task<IReadOnlyList<string>> GetInstalledModelNamesAsync(CancellationToken cancellationToken)
    {
        var result = await GetInstalledModelInventoryAsync(cancellationToken).ConfigureAwait(false);
        return result.Models.Select(model => model.Name).ToArray();
    }

    /// <summary>Resolves the installed-model inventory, caching the result for a short window.</summary>
    public async Task<InstalledModelInventoryResult> GetInstalledModelInventoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cachedModels = TryGetCachedInstalledModels();
        if (cachedModels is not null)
        {
            return new InstalledModelInventoryResult(cachedModels, OllamaQuerySucceeded: true, []);
        }

        try
        {
            var models = await _modelCapabilityClient.ListInstalledModelsAsync(cancellationToken).ConfigureAwait(false);
            var discoveredModels = models
                                   .Select(model => new
                                   {
                                       Name = NormalizeModelName(model.Name),
                                       Digest = NormalizeModelName(model.Digest)
                                   })
                                   .Where(model => !string.IsNullOrWhiteSpace(model.Name))
                                   .Select(model => new InstalledModelInfo(model.Name!, model.Digest, IsDiscovered: true))
                                   .ToArray();
            var configuredModels = _configuredModelNames.Select(modelName => new InstalledModelInfo(modelName, Digest: null, IsDiscovered: false));
            var normalizedModels = discoveredModels
                                   .Concat(configuredModels)
                                   .DistinctBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
                                   .ToArray();

            _logger.LogInformation(
                "Detected {DiscoveredModelCount} Ollama model(s), {ConfiguredModelCount} configured model fallback(s), reporting {ReportedModelCount} installed model(s): {ReportedModels}.",
                discoveredModels.Length,
                _configuredModelNames.Count,
                normalizedModels.Length,
                string.Join(", ", normalizedModels.Select(model => model.Name)));

            CacheInstalledModels(normalizedModels);
            return new InstalledModelInventoryResult(normalizedModels, OllamaQuerySucceeded: true, []);
        }
        catch (HttpRequestException exception)
        {
            // Debug, not Warning: an unreachable Ollama endpoint is the expected/benign case in desktop mode (no Ollama
            // daemon). The graceful configured-fallback below keeps the node functional; full stack traces here would
            // just flood the operator console on every capability report.
            _logger.LogDebug(exception, "Ollama not reachable while querying installed models; reporting {ConfiguredModelCount} configured fallback(s): {ConfiguredModels}.",
                _configuredModelNames.Count,
                string.Join(", ", _configuredModelNames));
            var configuredModels = _configuredModelNames.Select(modelName => new InstalledModelInfo(modelName, Digest: null, IsDiscovered: false)).ToArray();
            return new InstalledModelInventoryResult(configuredModels, OllamaQuerySucceeded: false, [DiagnosticOllamaUnreachable]);
        }
    }

    /// <summary>Builds per-model metadata (digest + max context tokens) for the given inventory.</summary>
    public async Task<IReadOnlyList<ClientModelMetadata>> GetInstalledModelMetadataAsync(IReadOnlyList<InstalledModelInfo> installedModels,
        CancellationToken cancellationToken)
    {
        var metadata = new List<ClientModelMetadata>(installedModels.Count);
        foreach (var installedModel in installedModels)
        {
            var maxContextTokens = installedModel.IsDiscovered && !string.IsNullOrWhiteSpace(installedModel.Digest)
                ? await GetMaxContextTokensAsync(installedModel, cancellationToken).ConfigureAwait(false)
                : null;

            metadata.Add(new ClientModelMetadata
            {
                Name = installedModel.Name,
                Digest = installedModel.Digest,
                MaxContextTokens = maxContextTokens
            });
        }

        return metadata;
    }

    /// <summary>Probes the runtime reachability and version.</summary>
    public async Task<OllamaRuntimeStatus> DetectOllamaRuntimeAsync(CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();

        try
        {
            if (!await _modelCapabilityClient.IsRuntimeReachableAsync(cancellationToken).ConfigureAwait(false))
            {
                diagnostics.Add(DiagnosticOllamaUnreachable);
                return new OllamaRuntimeStatus(Reachable: false, Version: null, diagnostics);
            }

            var version = await _modelCapabilityClient.GetRuntimeVersionAsync(cancellationToken).ConfigureAwait(false);
            return new OllamaRuntimeStatus(Reachable: true, NormalizeModelName(version), diagnostics);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(exception, "Ollama runtime not reachable (expected in desktop mode without an Ollama daemon).");
            diagnostics.Add(DiagnosticOllamaUnreachable);
            return new OllamaRuntimeStatus(Reachable: false, Version: null, diagnostics);
        }
    }

    /// <summary>Probes the runtime for the currently active/loaded model.</summary>
    public async Task<ActiveModelInfo> DetectActiveModelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var runningModels = await _modelCapabilityClient.ListRunningModelsAsync(cancellationToken).ConfigureAwait(false);
            var active = runningModels.Count > 0 ? runningModels[0] : null;
            if (active is null)
            {
                return ActiveModelInfo.None;
            }

            var modelName = NormalizeModelName(active.Name) ?? NormalizeModelName(active.ModelName);
            if (modelName is null)
            {
                return ActiveModelInfo.None;
            }

            return new ActiveModelInfo(modelName, active.ExpiresAt);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(exception, "Ollama not reachable while querying running models (expected in desktop mode).");
            return ActiveModelInfo.None;
        }
    }

    private async Task<int?> GetMaxContextTokensAsync(InstalledModelInfo installedModel, CancellationToken cancellationToken)
    {
        var cacheKey = new ModelContextCacheKey(installedModel.Name, installedModel.Digest!);
        lock (_modelContextCacheSync)
        {
            if (_modelContextCache.TryGetValue(cacheKey, out var cachedContextLength))
            {
                return cachedContextLength;
            }
        }

        try
        {
            var details = await _modelCapabilityClient.GetModelDetailAsync(installedModel.Name, cancellationToken).ConfigureAwait(false);
            if (details.MaxContextTokens is null)
            {
                _logger.LogWarning("Ollama /api/show for model '{ModelName}' succeeded but did not include a supported *.context_length model_info key.",
                    installedModel.Name);
            }

            lock (_modelContextCacheSync)
            {
                _modelContextCache[cacheKey] = details.MaxContextTokens;
            }

            return details.MaxContextTokens;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(exception, "Ollama /api/show not reachable for model '{ModelName}'; reporting unknown max context tokens.", installedModel.Name);
            return null;
        }
    }

    private static IReadOnlyList<string> ResolveConfiguredModelNames(IConfiguration configuration)
    {
        var configuredModelNames = ConfiguredModelKeys
                                   .Select(configuration.GetValue<string>)
                                   .Select(NormalizeModelName);
        var connectionStringModelNames = ModelConnectionStringNames
                                         .Select(configuration.GetConnectionString)
                                         .Select(TryExtractModelName);

        return configuredModelNames
               .Concat(connectionStringModelNames)
               .Where(modelName => !string.IsNullOrWhiteSpace(modelName))
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .OrderBy(modelName => modelName, StringComparer.OrdinalIgnoreCase)
               .Cast<string>()
               .ToArray();
    }

    private static string? TryExtractModelName(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var connectionStringBuilder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        return connectionStringBuilder.TryGetValue("Model", out var modelValue)
               && modelValue is string modelName
            ? NormalizeModelName(modelName)
            : null;
    }

    private static string? NormalizeModelName(string? modelName)
    {
        var normalized = modelName?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private IReadOnlyList<InstalledModelInfo>? TryGetCachedInstalledModels()
    {
        lock (_installedModelsCacheSync)
        {
            if (_installedModelsCache is null)
            {
                return null;
            }

            if (_timeProvider.GetUtcNow() >= _installedModelsCache.ExpiresAt)
            {
                _installedModelsCache = null;
                return null;
            }

            return _installedModelsCache.Models;
        }
    }

    private void CacheInstalledModels(IReadOnlyList<InstalledModelInfo> models)
    {
        lock (_installedModelsCacheSync)
        {
            _installedModelsCache = new CachedInstalledModels(models, _timeProvider.GetUtcNow().Add(InstalledModelsCacheLifetime));
        }
    }

    private sealed record CachedInstalledModels(IReadOnlyList<InstalledModelInfo> Models, DateTimeOffset ExpiresAt);

    private sealed record ModelContextCacheKey(string ModelName, string Digest);
}
