namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Ollama;

public sealed class PullLocalModelEndpoint(
    IOllamaModelService modelService,
    IModelProviderMapStore modelProviderMapStore,
    ModelNameValidator modelNameValidator) : Endpoint<PullLocalModelRequest, PullLocalModelResponse>
{
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    private readonly IModelProviderMapStore _modelProviderMapStore = modelProviderMapStore ?? throw new ArgumentNullException(nameof(modelProviderMapStore));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalModels.Pull);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(PullLocalModelRequest req, CancellationToken ct)
    {
        if (!await ValidateModelNameAsync(req.ModelName, ct).ConfigureAwait(false))
        {
            return;
        }

        var modelName = req.ModelName!.Trim();
        var status = "Complete";
        long? totalBytes = null;
        long? completedBytes = null;

        await foreach (var progress in _modelService.PullModelAsync(modelName, ct).ConfigureAwait(false))
        {
            status = string.IsNullOrWhiteSpace(progress.Status) ? status : progress.Status;
            totalBytes = progress.Total;
            completedBytes = progress.Completed;
        }

        // Explicitly route this Ollama model to the Ollama runtime: the unmapped-routing default is now "llamacpp", so a
        // node-pulled Ollama model must persist a "ollama" map row or a later send would dial llama.cpp by default.
        // Symmetric to the GGUF download coordinator's llamacpp map-write.
        await _modelProviderMapStore.UpsertAsync(modelName, OllamaLocalModelProvider.OllamaProviderName, ct).ConfigureAwait(false);

        await Send.OkAsync(LocalModelsMapper.ToPullResponse(modelName, status, totalBytes, completedBytes), ct).ConfigureAwait(false);
    }

    private async Task<bool> ValidateModelNameAsync(string? modelName, CancellationToken ct)
    {
        var validationError = _modelNameValidator.GetValidationError(modelName);
        if (validationError is null)
        {
            return true;
        }

        AddError(validationError);
        await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        return false;
    }
}
