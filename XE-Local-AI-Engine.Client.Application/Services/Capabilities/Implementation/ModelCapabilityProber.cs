namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

using System.Data.Common;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Probes the node-local model runtime through the provider-neutral <see cref="IModelCapabilityClient" />: installed-model inventory
///     (short-lived cache) and runtime reachability/version.
/// </summary>
/// <remarks>
///     Collaborator behind <see cref="CapabilityReporter" />; owns the inventory cache and the configured-model fallback list.
/// </remarks>
internal sealed class ModelCapabilityProber
{
    private const string DiagnosticOllamaUnreachable = "ollama-unreachable";
    private static readonly TimeSpan InstalledModelsCacheLifetime = TimeSpan.FromSeconds(10);

    private static readonly string[] ConfiguredModelKeys =
    [
        "Agent:LocalChat:DefaultModel",
        "Ollama:ChatModel"
    ];

    private static readonly string[] ModelConnectionStringNames = ["chat", "embeddings"];

    private readonly IReadOnlyList<string> _configuredModelNames;
    private readonly Lock _installedModelsCacheSync = new();
    private readonly ILogger<ModelCapabilityProber> _logger;
    private readonly IModelCapabilityClient _modelCapabilityClient;
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
        var result = await GetInstalledModelInventoryAsync(cancellationToken);
        return result.Models.Select(model => model.Name).ToArray();
    }

    /// <summary>Resolves the installed-model inventory, caching the result for a short window.</summary>
    public async Task<InstalledModelInventoryResult> GetInstalledModelInventoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cachedModels = TryGetCachedInstalledModels();
        if (cachedModels is not null)
        {
            return new InstalledModelInventoryResult
            {
                Models = cachedModels,
                OllamaQuerySucceeded = true,
                Diagnostics = []
            };
        }

        try
        {
            var models = await _modelCapabilityClient.ListInstalledModelsAsync(cancellationToken);
            var discoveredModels = models
                                   .Select(model => new
                                   {
                                       Name = NormalizeModelName(model.Name),
                                       Digest = NormalizeModelName(model.Digest)
                                   })
                                   .Where(model => !string.IsNullOrWhiteSpace(model.Name))
                                   .Select(model => new InstalledModelInfo
                                   {
                                       Name = model.Name!,
                                       Digest = model.Digest,
                                       IsDiscovered = true
                                   })
                                   .ToArray();
            var configuredModels = _configuredModelNames.Select(modelName => new InstalledModelInfo
            {
                Name = modelName,
                Digest = null,
                IsDiscovered = false
            });
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
            return new InstalledModelInventoryResult
            {
                Models = normalizedModels,
                OllamaQuerySucceeded = true,
                Diagnostics = []
            };
        }
        catch (HttpRequestException exception)
        {
            // Debug, not Warning: an unreachable Ollama endpoint is the expected/benign case in desktop mode (no Ollama daemon). The graceful
            // configured-fallback below keeps the node functional; full stack traces here would just flood the operator console on every capability report.
            _logger.LogDebug(exception, "Ollama not reachable while querying installed models; reporting {ConfiguredModelCount} configured fallback(s): {ConfiguredModels}.",
                _configuredModelNames.Count,
                string.Join(", ", _configuredModelNames));
            var configuredModels = _configuredModelNames.Select(modelName => new InstalledModelInfo
            {
                Name = modelName,
                Digest = null,
                IsDiscovered = false
            }).ToArray();
            return new InstalledModelInventoryResult
            {
                Models = configuredModels,
                OllamaQuerySucceeded = false,
                Diagnostics = [DiagnosticOllamaUnreachable]
            };
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
            _installedModelsCache = new CachedInstalledModels
            {
                Models = models,
                ExpiresAt = _timeProvider.GetUtcNow().Add(InstalledModelsCacheLifetime)
            };
        }
    }

    private sealed record CachedInstalledModels
    {
        public required IReadOnlyList<InstalledModelInfo> Models { get; init; }

        public required DateTimeOffset ExpiresAt { get; init; }
    }
}
