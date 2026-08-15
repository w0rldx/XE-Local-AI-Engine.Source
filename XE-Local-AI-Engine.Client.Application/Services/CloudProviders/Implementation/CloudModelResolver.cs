namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;

/// <summary>
///     Reads the stored cloud configuration to classify model ids. The three local-model endpoints (list, details,
///     select) each used to carry their own copy of this read plus its best-effort catch; the catch now lives here
///     only. Log LEVELS differ by call site on purpose: a failed classification degrades silently (Debug — the details
///     endpoint polls per selected model and would flood the console), while a failed model-list read is unexpected
///     enough to warrant a Warning.
/// </summary>
public sealed class CloudModelResolver(ICloudCredentialStore cloudCredentialStore, ILogger<CloudModelResolver> logger)
    : ICloudModelResolver
{
    private readonly ICloudCredentialStore _cloudCredentialStore = cloudCredentialStore ?? throw new ArgumentNullException(nameof(cloudCredentialStore));
    private readonly ILogger<CloudModelResolver> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<bool> IsCloudModelAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        return CodexModelCatalog.IsCodexModel(modelName)
               || await IsAzureFoundryDeploymentAsync(modelName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsAzureFoundryDeploymentAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        try
        {
            var config = await _cloudCredentialStore.LoadConfigAsync(cancellationToken).ConfigureAwait(false);
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
            var config = await _cloudCredentialStore.LoadConfigAsync(cancellationToken).ConfigureAwait(false);
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
