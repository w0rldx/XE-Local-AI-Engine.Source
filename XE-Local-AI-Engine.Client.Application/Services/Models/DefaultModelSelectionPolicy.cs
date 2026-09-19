namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal sealed class DefaultModelSelectionPolicy
{
    private readonly IGgufModelStore _ggufModelStore;
    private readonly ICloudModelResolver _cloudModelResolver;
    private readonly IActiveCloudChatClientFactory _activeCloudChatClientFactory;
    private readonly ModelNameValidator _modelNameValidator;

    public DefaultModelSelectionPolicy(
        IGgufModelStore ggufModelStore,
        ICloudModelResolver cloudModelResolver,
        IActiveCloudChatClientFactory activeCloudChatClientFactory,
        ModelNameValidator modelNameValidator)
    {
        _ggufModelStore = ggufModelStore;
        _cloudModelResolver = cloudModelResolver;
        _activeCloudChatClientFactory = activeCloudChatClientFactory;
        _modelNameValidator = modelNameValidator;
    }

    public async Task<DefaultModelSelectionValidation?> ValidateAsync(string? modelName,
        LocalModelSelectionPolicy policy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return new() { FailureCode = LocalModelAdministrationFailureCodes.InvalidModelName, DisplayMessage = "Model name is required." };
        }

        var canonicalName = modelName.Trim();
        var validationError = _modelNameValidator.GetValidationError(canonicalName);
        if (validationError is not null)
        {
            return new() { FailureCode = LocalModelAdministrationFailureCodes.InvalidModelName, DisplayMessage = validationError };
        }

        if (policy == LocalModelSelectionPolicy.InstalledLocalOnly
            && !await _ggufModelStore.ExistsAsync(canonicalName, cancellationToken))
        {
            return new() { FailureCode = LocalModelAdministrationFailureCodes.ModelNotInstalled, DisplayMessage = "The requested local model is not installed." };
        }

        return null;
    }

    public async Task InvalidateCacheForTransitionAsync(string? previousModelName,
        string? selectedModelName,
        CancellationToken cancellationToken)
    {
        if (await _cloudModelResolver.IsCloudModelAsync(previousModelName, cancellationToken)
            || await _cloudModelResolver.IsCloudModelAsync(selectedModelName, cancellationToken))
        {
            _activeCloudChatClientFactory.InvalidateSelectionCache();
        }
    }
}

internal sealed class DefaultModelSelectionValidation
{
    public required string FailureCode { get; init; }

    public required string DisplayMessage { get; init; }
}
