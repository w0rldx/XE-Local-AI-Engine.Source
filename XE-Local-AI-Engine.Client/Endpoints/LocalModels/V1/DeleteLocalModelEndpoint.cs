namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;

public sealed class DeleteLocalModelEndpoint(
    ILocalModelAdministrationService administrationService,
    ModelNameValidator modelNameValidator) : Endpoint<DeleteLocalModelRequest, DeleteLocalModelResponse>
{
    private readonly ILocalModelAdministrationService _administrationService = administrationService ?? throw new ArgumentNullException(nameof(administrationService));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalModels.ModelByName);
        Policies(NodeAuthorizationPolicies.Operator);

        // The coordinator's three refusals (dependent adapters, provider conflict, superseded provider map) are typed
        // conflict exceptions the global ConflictExceptionHandler turns into this envelope — never caught here.
        Description(builder => builder
                               .Produces<DeleteLocalModelResponse>(StatusCodes.Status200OK)
                               .ProducesConflictProblemDetails());
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

        var result = await _administrationService.DeleteAsync(decodedModelName, ct).ConfigureAwait(false);

        await Send.OkAsync(new DeleteLocalModelResponse
        {
            ModelName = result.ModelName!,
            Deleted = result.Deleted
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
