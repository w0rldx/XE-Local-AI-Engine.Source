namespace XE_Local_AI_Engine.Client.Services.Models.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

internal sealed class DefaultModelSelectionPolicy
{
    private readonly IGgufModelStore _ggufModelStore;
    private readonly ICloudModelResolver _cloudModelResolver;
    private readonly IActiveCloudChatClientFactory _activeCloudChatClientFactory;
    private readonly ModelNameValidator _modelNameValidator;
    private readonly IServiceScopeFactory _scopeFactory;

    public DefaultModelSelectionPolicy(IGgufModelStore ggufModelStore,
        ICloudModelResolver cloudModelResolver,
        IActiveCloudChatClientFactory activeCloudChatClientFactory,
        ModelNameValidator modelNameValidator,
        IServiceScopeFactory scopeFactory)
    {
        _ggufModelStore = ggufModelStore;
        _cloudModelResolver = cloudModelResolver;
        _activeCloudChatClientFactory = activeCloudChatClientFactory;
        _modelNameValidator = modelNameValidator;
        _scopeFactory = scopeFactory;
    }

    public async Task<DefaultModelSelectionValidation?> ValidateAsync(string? modelName,
        LocalModelSelectionPolicy policy,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return new()
            {
                FailureCode = LocalModelAdministrationFailureCodes.InvalidModelName,
                DisplayMessage = "Model name is required."
            };
        }

        var canonicalName = modelName.Trim();
        var validationError = _modelNameValidator.GetValidationError(canonicalName);
        if (validationError is not null)
        {
            return new()
            {
                FailureCode = LocalModelAdministrationFailureCodes.InvalidModelName,
                DisplayMessage = validationError
            };
        }

        if (policy == LocalModelSelectionPolicy.InstalledLocalOnly
            && !await _ggufModelStore.ExistsAsync(canonicalName, cancellationToken))
        {
            return new()
            {
                FailureCode = LocalModelAdministrationFailureCodes.ModelNotInstalled,
                DisplayMessage = "The requested local model is not installed."
            };
        }

        // The default serves chat, so an embedder, reranker, drafter or decision model is refused by the same rule the
        // dataset, benchmark and workflow gates use. The classification store is scoped; this policy is a singleton.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var classification = await scope.ServiceProvider.GetRequiredService<IModelClassificationStore>()
                                        .GetByNameAsync(canonicalName, cancellationToken);
        var decisionType = await LocalGgufModelKindClassifier.ReadDecisionTypeAsync(_ggufModelStore, canonicalName, cancellationToken);
        if (!LocalGgufModelKindClassifier.IsChatModel(canonicalName, classification, decisionType))
        {
            return new()
            {
                FailureCode = LocalModelAdministrationFailureCodes.ModelKindNotChat,
                DisplayMessage = "The requested model is not a chat model (an embedder, reranker, draft or decision model cannot be the default)."
            };
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
