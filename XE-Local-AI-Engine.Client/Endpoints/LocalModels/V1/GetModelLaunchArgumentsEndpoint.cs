namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;

/// <summary>
///     Reads the per-model extra <c>llama-server</c> launch-argument override (developer/advanced). Returns an empty
///     string when the model has no override.
/// </summary>
public sealed class GetModelLaunchArgumentsEndpoint : Endpoint<GetModelLaunchArgumentsRequest, ModelLaunchArgumentsResponse>
{
    private readonly ModelLaunchArgumentsService _launchArguments;

    public GetModelLaunchArgumentsEndpoint(
        ModelLaunchArgumentsService launchArguments)
    {
        ArgumentNullException.ThrowIfNull(launchArguments);
        _launchArguments = launchArguments;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalModels.ModelLaunchArguments);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetModelLaunchArgumentsRequest req, CancellationToken ct)
    {
        // Decode again here: GetModelLaunchArgumentsRequestValidator already ran the grammar over the decoded name,
        // and the name that is read must be that same decoded one. See ModelRouteName.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);

        var raw = await _launchArguments.GetRawArgumentsAsync(decodedModelName!, ct);
        await Send.OkAsync(new ModelLaunchArgumentsResponse
            {
                ModelName = decodedModelName!,
                RawArguments = raw ?? string.Empty
            },
            ct);
    }
}
