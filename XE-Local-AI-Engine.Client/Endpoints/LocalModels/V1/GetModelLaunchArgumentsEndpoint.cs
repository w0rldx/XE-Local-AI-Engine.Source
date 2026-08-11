namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Validation;

/// <summary>
///     Reads the per-model extra <c>llama-server</c> launch-argument override (developer/advanced). Returns an empty
///     string when the model has no override.
/// </summary>
public sealed class GetModelLaunchArgumentsEndpoint(
    IModelLaunchArgumentsStore store,
    ModelNameValidator modelNameValidator) : Endpoint<GetModelLaunchArgumentsRequest, ModelLaunchArgumentsResponse>
{
    private readonly IModelLaunchArgumentsStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalModels.ModelLaunchArguments);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetModelLaunchArgumentsRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so validate and read
        // the decoded canonical name.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        var validationError = _modelNameValidator.GetValidationError(decodedModelName);
        if (validationError is not null)
        {
            AddError(validationError);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var raw = await _store.GetRawArgumentsAsync(decodedModelName!, ct).ConfigureAwait(false);
        await Send.OkAsync(new ModelLaunchArgumentsResponse
            {
                ModelName = decodedModelName!,
                RawArguments = raw ?? string.Empty
            },
            ct).ConfigureAwait(false);
    }
}
