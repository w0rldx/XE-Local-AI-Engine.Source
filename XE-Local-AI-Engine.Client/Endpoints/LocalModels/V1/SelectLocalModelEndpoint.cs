namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;

public sealed class SelectLocalModelEndpoint(
    ILocalModelAdministrationService administrationService,
    ModelNameValidator modelNameValidator) : Endpoint<SelectLocalModelRequest, SelectLocalModelResponse>
{
    private readonly ILocalModelAdministrationService _administrationService = administrationService ?? throw new ArgumentNullException(nameof(administrationService));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    public override void Configure()
    {
        Post(LocalApiRoutes.LocalModels.Select);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SelectLocalModelRequest req, CancellationToken ct)
    {
        if (!await ValidateModelNameAsync(req.ModelName, ct).ConfigureAwait(false))
        {
            return;
        }

        var result = await _administrationService
                           .SelectDefaultAsync(req.ModelName, LocalModelSelectionPolicy.ConfiguredModel, ct)
                           .ConfigureAwait(false);

        await Send.OkAsync(new SelectLocalModelResponse
        {
            SelectedModelName = result.SelectedModelName!
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
