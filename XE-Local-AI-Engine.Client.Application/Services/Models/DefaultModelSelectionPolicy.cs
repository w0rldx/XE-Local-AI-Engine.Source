namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal sealed class DefaultModelSelectionPolicy(
    IGgufModelStore ggufModelStore,
    ICloudModelResolver cloudModelResolver,
    IActiveCloudChatClientFactory activeCloudChatClientFactory,
    ModelNameValidator modelNameValidator)
{
    public async Task<DefaultModelSelectionValidation?> ValidateAsync(string? modelName,
        LocalModelSelectionPolicy policy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return new(LocalModelAdministrationFailureCodes.InvalidModelName, "Model name is required.");
        }

        var canonicalName = modelName.Trim();
        var validationError = modelNameValidator.GetValidationError(canonicalName);
        if (validationError is not null)
        {
            return new(LocalModelAdministrationFailureCodes.InvalidModelName, validationError);
        }

        if (policy == LocalModelSelectionPolicy.InstalledLocalOnly
            && !await ggufModelStore.ExistsAsync(canonicalName, cancellationToken).ConfigureAwait(false))
        {
            return new(LocalModelAdministrationFailureCodes.ModelNotInstalled, "The requested local model is not installed.");
        }

        return null;
    }

    public async Task InvalidateCacheForTransitionAsync(string? previousModelName,
        string? selectedModelName,
        CancellationToken cancellationToken)
    {
        if (await cloudModelResolver.IsCloudModelAsync(previousModelName, cancellationToken).ConfigureAwait(false)
            || await cloudModelResolver.IsCloudModelAsync(selectedModelName, cancellationToken).ConfigureAwait(false))
        {
            activeCloudChatClientFactory.InvalidateSelectionCache();
        }
    }
}

internal sealed record DefaultModelSelectionValidation(string FailureCode, string DisplayMessage);
