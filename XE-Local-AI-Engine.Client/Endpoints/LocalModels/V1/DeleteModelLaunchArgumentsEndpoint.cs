namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;

/// <summary>
///     Clears the per-model extra <c>llama-server</c> launch-argument override (developer/advanced). Idempotent: a model
///     with no override still reports success with an empty string.
/// </summary>
public sealed class DeleteModelLaunchArgumentsEndpoint : Endpoint<GetModelLaunchArgumentsRequest, ModelLaunchArgumentsResponse>
{
    private readonly ModelLaunchArgumentsService _launchArguments;
    private readonly ModelNameValidator _modelNameValidator;

    public DeleteModelLaunchArgumentsEndpoint(
        ModelLaunchArgumentsService launchArguments,
        ModelNameValidator modelNameValidator)
    {
        ArgumentNullException.ThrowIfNull(launchArguments);
        ArgumentNullException.ThrowIfNull(modelNameValidator);
        _launchArguments = launchArguments;
        _modelNameValidator = modelNameValidator;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalModels.ModelLaunchArguments);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetModelLaunchArgumentsRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so validate and clear
        // the decoded canonical name.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        var validationError = _modelNameValidator.GetValidationError(decodedModelName);
        if (validationError is not null)
        {
            AddError(validationError);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        await _launchArguments.ClearAsync(decodedModelName!, ct);
        await Send.OkAsync(new ModelLaunchArgumentsResponse
            {
                ModelName = decodedModelName!,
                RawArguments = string.Empty
            },
            ct);
    }
}
