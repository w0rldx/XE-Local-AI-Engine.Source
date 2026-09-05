namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.OpenAICompat;

internal sealed class LocalModelAdministrationService(
    ILocalModelDeletionCoordinator deletionCoordinator,
    ILocalModelProviderResolver providerResolver,
    INodeSettingsStore nodeSettingsStore,
    DefaultModelSelectionPolicy defaultModelSelectionPolicy,
    ModelNameValidator modelNameValidator,
    ILogger<LocalModelAdministrationService> logger) : ILocalModelAdministrationService
{
    public async Task<LocalModelDeletionResult> DeleteAsync(string? modelName, CancellationToken cancellationToken = default)
    {
        var validationFailure = Validate(modelName);
        if (validationFailure is not null)
        {
            return new LocalModelDeletionResult(false, null, false,
                LocalModelAdministrationFailureCodes.InvalidModelName, validationFailure);
        }

        var canonicalName = modelName!.Trim();
        var providerName = await providerResolver.ResolveProviderNameForModelAsync(canonicalName, cancellationToken).ConfigureAwait(false);
        if (string.Equals(providerName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            CommittedModelDeletion committed;
            try
            {
                committed = await deletionCoordinator.CommitDeleteAsync(canonicalName, cancellationToken).ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                return new LocalModelDeletionResult(true, canonicalName, false);
            }

            try
            {
                await deletionCoordinator.PurgeAfterSuccessAsync(committed, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "The committed deletion for local model {ModelName} could not be purged; startup reconciliation will retry it.",
                    canonicalName);
            }

            return new LocalModelDeletionResult(true, canonicalName, true);
        }

        try
        {
            await providerResolver.ResolveProvider(providerName).DeleteModelAsync(canonicalName, cancellationToken).ConfigureAwait(false);
        }
        catch (ExternalProviderOperationNotSupportedException exception)
        {
            // The external provider owns no weights on this node, so it refuses deletion rather than reporting a
            // success the model table would then render as a completed removal. Translated here, in the layer that
            // may reference the provider, so the host maps it to a 409 without taking a dependency of its own.
            throw new ModelOperationNotSupportedByProviderException(exception.Message);
        }

        providerResolver.InvalidateModelProviderMap();
        return new LocalModelDeletionResult(true, canonicalName, true);
    }

    public async Task<LocalModelSelectionResult> SelectDefaultAsync(string? modelName,
        LocalModelSelectionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var validationFailure = await defaultModelSelectionPolicy.ValidateAsync(modelName, policy, cancellationToken).ConfigureAwait(false);
        if (validationFailure is not null)
        {
            return new LocalModelSelectionResult(false, null, null,
                validationFailure.FailureCode, validationFailure.DisplayMessage);
        }

        var selectedModelName = modelName!.Trim();

        // Read-modify-write under the store's lock. The settings record is whole-file, so a save built from a record
        // loaded before validation would write back every field a concurrent writer (a machine-key mint, the
        // external-provider reconciliation pass) changed in that window. The previous name is read inside the mutation
        // for the same reason: the transition the cache is invalidated for is the one that actually happened on disk.
        string? previousModelName = null;
        await nodeSettingsStore.UpdateAsync(latest =>
        {
            previousModelName = latest.DefaultModelName;
            return latest with
            {
                DefaultModelName = selectedModelName
            };
        }, cancellationToken).ConfigureAwait(false);

        await defaultModelSelectionPolicy
              .InvalidateCacheForTransitionAsync(previousModelName, selectedModelName, cancellationToken)
              .ConfigureAwait(false);

        return new LocalModelSelectionResult(true, selectedModelName, previousModelName);
    }

    private string? Validate(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return "Model name is required.";
        }

        return modelNameValidator.GetValidationError(modelName);
    }
}
