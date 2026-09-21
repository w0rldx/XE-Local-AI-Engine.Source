namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;

/// <summary>
///     Clears the per-model extra <c>llama-server</c> launch-argument override (developer/advanced). Idempotent: a model
///     with no override still reports success with an empty string.
/// </summary>
public sealed class DeleteModelLaunchArgumentsEndpoint : Endpoint<GetModelLaunchArgumentsRequest, ModelLaunchArgumentsResponse>
{
    private readonly ModelLaunchArgumentsService _launchArguments;

    public DeleteModelLaunchArgumentsEndpoint(
        ModelLaunchArgumentsService launchArguments)
    {
        ArgumentNullException.ThrowIfNull(launchArguments);
        _launchArguments = launchArguments;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.LocalModels.ModelLaunchArguments);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetModelLaunchArgumentsRequest req, CancellationToken ct)
    {
        // Decode again here: GetModelLaunchArgumentsRequestValidator already ran the grammar over the decoded name,
        // and the name that is cleared must be that same decoded one. See ModelRouteName.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);

        await _launchArguments.ClearAsync(decodedModelName!, ct);
        await Send.OkAsync(new ModelLaunchArgumentsResponse
            {
                ModelName = decodedModelName!,
                RawArguments = string.Empty
            },
            ct);
    }
}
