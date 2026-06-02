namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Validation;

public sealed class DeleteLocalModelEndpoint(
    IOllamaModelService modelService,
    ModelNameValidator modelNameValidator) : Endpoint<DeleteLocalModelRequest, DeleteLocalModelResponse>
{
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalModels.ModelByName);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteLocalModelRequest req, CancellationToken ct)
    {
        if (!await ValidateModelNameAsync(req.ModelName, ct).ConfigureAwait(false))
        {
            return;
        }

        var modelName = req.ModelName!.Trim();
        await _modelService.DeleteModelAsync(modelName, ct).ConfigureAwait(false);
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
