namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models;

/// <summary>
///     Sets (or clears, when the string is blank) the per-model extra <c>llama-server</c> launch-argument override
///     (developer/advanced), taking effect the next time the model is (re)loaded.
/// </summary>
/// <remarks>
///     What may be set, the length cap and the model-name check all live in
///     <c>SetModelLaunchArgumentsRequestValidator</c>; what remains here is the write. A permitted flag is stored
///     verbatim and appended to the process on the next cold load, so the operator can experiment with it.
/// </remarks>
public sealed class PutModelLaunchArgumentsEndpoint : Endpoint<SetModelLaunchArgumentsRequest, ModelLaunchArgumentsResponse>
{
    private readonly ModelLaunchArgumentsService _launchArguments;

    public PutModelLaunchArgumentsEndpoint(ModelLaunchArgumentsService launchArguments)
    {
        ArgumentNullException.ThrowIfNull(launchArguments);
        _launchArguments = launchArguments;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.LocalModels.ModelLaunchArguments);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SetModelLaunchArgumentsRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so store the decoded
        // canonical name. The validator decoded it the same way to check it.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        var raw = (req.RawArguments ?? string.Empty).Trim();

        // A blank override is a clear-to-default, not a stored empty row.
        if (raw.Length == 0)
        {
            await _launchArguments.ClearAsync(decodedModelName!, ct);
            await Send.OkAsync(new ModelLaunchArgumentsResponse
                {
                    ModelName = decodedModelName!,
                    RawArguments = string.Empty
                },
                ct);
            return;
        }

        var result = await _launchArguments.SaveAsync(decodedModelName!, raw, ct);
        await Send.OkAsync(new ModelLaunchArgumentsResponse
            {
                ModelName = result.ModelName,
                RawArguments = result.RawArguments
            },
            ct);
    }
}
