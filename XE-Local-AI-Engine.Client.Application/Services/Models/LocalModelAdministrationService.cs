namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.OpenAICompat;

internal sealed class LocalModelAdministrationService : ILocalModelAdministrationService
{
    private readonly ILocalModelDeletionCoordinator _deletionCoordinator;
    private readonly ILocalModelProviderResolver _providerResolver;
    private readonly INodeSettingsStore _nodeSettingsStore;
    private readonly DefaultModelSelectionPolicy _defaultModelSelectionPolicy;
    private readonly ModelNameValidator _modelNameValidator;
    private readonly ILogger<LocalModelAdministrationService> _logger;

    public LocalModelAdministrationService(
        ILocalModelDeletionCoordinator deletionCoordinator,
        ILocalModelProviderResolver providerResolver,
        INodeSettingsStore nodeSettingsStore,
        DefaultModelSelectionPolicy defaultModelSelectionPolicy,
        ModelNameValidator modelNameValidator,
        ILogger<LocalModelAdministrationService> logger)
    {
        _deletionCoordinator = deletionCoordinator;
        _providerResolver = providerResolver;
        _nodeSettingsStore = nodeSettingsStore;
        _defaultModelSelectionPolicy = defaultModelSelectionPolicy;
        _modelNameValidator = modelNameValidator;
        _logger = logger;
    }

    public async Task<LocalModelDeletionResult> DeleteAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        var validationFailure = Validate(modelName);
        if (validationFailure is not null)
        {
            return new LocalModelDeletionResult
            {
                Succeeded = false,
                ModelName = null,
                Deleted = false,
                FailureCode = LocalModelAdministrationFailureCodes.InvalidModelName,
                DisplayMessage = validationFailure
            };
        }

        var canonicalName = modelName!.Trim();
        var providerName = await _providerResolver.ResolveProviderNameForModelAsync(canonicalName, cancellationToken);
        if (string.Equals(providerName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            CommittedModelDeletion committed;
            try
            {
                committed = await _deletionCoordinator.CommitDeleteAsync(canonicalName, cancellationToken);
            }
            catch (KeyNotFoundException)
            {
                return new LocalModelDeletionResult { Succeeded = true, ModelName = canonicalName, Deleted = false };
            }

            try
            {
                await _deletionCoordinator.PurgeAfterSuccessAsync(committed, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "The committed deletion for local model {ModelName} could not be purged; startup reconciliation will retry it.",
                    canonicalName);
            }

            return new LocalModelDeletionResult { Succeeded = true, ModelName = canonicalName, Deleted = true };
        }

        try
        {
            await _providerResolver.ResolveProvider(providerName).DeleteModelAsync(canonicalName, cancellationToken);
        }
        catch (ExternalProviderOperationNotSupportedException exception)
        {
            // The external provider owns no weights on this node, so it refuses deletion rather than reporting a success the model table
            // would render as a removal. Translated in this layer, which may reference the provider, so the host maps a 409 without one.
            throw new ModelOperationNotSupportedByProviderException(exception.Message, exception);
        }

        _providerResolver.InvalidateModelProviderMap();
        return new LocalModelDeletionResult { Succeeded = true, ModelName = canonicalName, Deleted = true };
    }

    public async Task<LocalModelSelectionResult> SelectDefaultAsync(string? modelName,
        LocalModelSelectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var validationFailure = await _defaultModelSelectionPolicy.ValidateAsync(modelName, policy, cancellationToken);
        if (validationFailure is not null)
        {
            return new LocalModelSelectionResult
            {
                Succeeded = false,
                SelectedModelName = null,
                PreviousModelName = null,
                FailureCode = validationFailure.FailureCode,
                DisplayMessage = validationFailure.DisplayMessage
            };
        }

        var selectedModelName = modelName!.Trim();

        // Read-modify-write under the store's lock: the settings record is whole-file, so a save built from a record loaded before validation
        // would revert a concurrent writer. The previous name is read inside the mutation so the invalidated transition is the one on disk.
        string? previousModelName = null;
        await _nodeSettingsStore.UpdateAsync(latest =>
        {
            previousModelName = latest.DefaultModelName;
            return latest with
            {
                DefaultModelName = selectedModelName
            };
        }, cancellationToken);

        await _defaultModelSelectionPolicy
              .InvalidateCacheForTransitionAsync(previousModelName, selectedModelName, cancellationToken);

        return new LocalModelSelectionResult { Succeeded = true, SelectedModelName = selectedModelName, PreviousModelName = previousModelName };
    }

    private string? Validate(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return "Model name is required.";
        }

        return _modelNameValidator.GetValidationError(modelName);
    }
}
