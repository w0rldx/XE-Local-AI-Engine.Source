namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

/// <summary>
///     Reads the stored cloud configuration to classify model ids. The three local-model endpoints (list, details,
///     select) each used to carry their own copy of this read plus its best-effort catch; the catch now lives here
///     only. Log LEVELS differ by call site on purpose: a failed classification degrades silently (Debug — the details
///     endpoint polls per selected model and would flood the console), while a failed model-list read is unexpected
///     enough to warrant a Warning.
/// </summary>
public sealed class CloudModelResolver : ICloudModelResolver
{
    private readonly ICloudCredentialStore _cloudCredentialStore;
    private readonly ILogger<CloudModelResolver> _logger;
    private readonly IModelTrustResolver _modelTrustResolver;

    public CloudModelResolver(
        ICloudCredentialStore cloudCredentialStore,
        IModelTrustResolver modelTrustResolver,
        ILogger<CloudModelResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(cloudCredentialStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(modelTrustResolver);
        _cloudCredentialStore = cloudCredentialStore;
        _logger = logger;
        _modelTrustResolver = modelTrustResolver;
    }

    public async Task<bool> IsCloudModelAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        // An external model is cloud unless it is POSITIVELY declared local. Unresolved counts as cloud here for the
        // same reason it does everywhere else: this answer feeds egress cues and gates, and "we could not tell" must
        // never render as "it stays on the machine".
        if (ExternalModelId.HasExternalScheme(modelName))
        {
            return await _modelTrustResolver.ResolveAsync(modelName, cancellationToken) != ModelTrustLocality.Local;
        }

        return CodexModelCatalog.IsCodexModel(modelName)
               || await IsAzureFoundryDeploymentAsync(modelName, cancellationToken);
    }

    public async Task<bool> IsAzureFoundryDeploymentAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        try
        {
            var config = await _cloudCredentialStore.LoadConfigAsync(cancellationToken);
            var connection = config?.AzureFoundry;
            return connection is { Models.Count: > 0 }
                   && connection.Models.Any(model => string.Equals(model.DeploymentName, modelName, StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Azure Foundry deployment match could not be resolved for '{ModelName}'.", modelName);
            return false;
        }
    }

    public async Task<StoredAzureFoundryConnection?> ResolveAzureFoundryConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await _cloudCredentialStore.LoadConfigAsync(cancellationToken);
            return config?.AzureFoundry;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Azure Foundry cloud model list could not be resolved.");
            return null;
        }
    }
}
