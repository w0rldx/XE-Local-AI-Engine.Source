namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Validation;

/// <summary>
///     FastEndpoints handler for the select local model local API operation.
/// </summary>
public sealed class SelectLocalModelEndpoint(
    INodeSettingsStore nodeSettingsStore,
    ModelNameValidator modelNameValidator) : Endpoint<SelectLocalModelRequest, SelectLocalModelResponse>
{
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));
    private readonly INodeSettingsStore _nodeSettingsStore = nodeSettingsStore ?? throw new ArgumentNullException(nameof(nodeSettingsStore));

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

        var selectedModelName = req.ModelName!.Trim();
        var settings = await _nodeSettingsStore.LoadAsync(ct).ConfigureAwait(false);
        await _nodeSettingsStore.SaveAsync(settings with
        {
            DefaultModelName = selectedModelName
        }, ct).ConfigureAwait(false);

        await Send.OkAsync(new SelectLocalModelResponse
        {
            SelectedModelName = selectedModelName
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
