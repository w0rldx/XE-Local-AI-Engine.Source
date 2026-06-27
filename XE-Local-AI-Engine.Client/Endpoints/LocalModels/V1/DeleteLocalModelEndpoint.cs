namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public sealed class DeleteLocalModelEndpoint(
    IGgufModelStore ggufModelStore,
    ModelNameValidator modelNameValidator) : Endpoint<DeleteLocalModelRequest, DeleteLocalModelResponse>
{
    private readonly IGgufModelStore _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalModels.ModelByName);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteLocalModelRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so validate and delete
        // the decoded canonical name to keep "validated name == deleted name" true.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        if (!await ValidateModelNameAsync(decodedModelName, ct).ConfigureAwait(false))
        {
            return;
        }

        var modelName = decodedModelName!.Trim();

        // Local models are GGUF files served by the bundled llama.cpp runtime (Ollama is no longer a runtime), so delete
        // via the GGUF store. Its DeleteModelAsync is idempotent — an already-ejected/uninstalled model removes cleanly
        // rather than 500ing — so no pre-existence probe is needed.
        await _ggufModelStore.DeleteModelAsync(modelName, ct).ConfigureAwait(false);
        await Send.OkAsync(new DeleteLocalModelResponse
        {
            ModelName = modelName,
            Deleted = true
        }, ct).ConfigureAwait(false);
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
