namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>The last index built for an installed model on the installed runtime, re-joined to its descriptor; 404 when there is none.</summary>
public sealed class GetModelLabPyTorchIndexEndpoint : Endpoint<GetModelLabArchitectureRequest, ModelLabPyTorchIndexResponse>
{
    private readonly ModelLabPyTorchSourceService _sources;

    public GetModelLabPyTorchIndexEndpoint(ModelLabPyTorchSourceService sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelLab.ArchitecturePyTorch);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabPyTorchIndexResponse>(StatusCodes.Status200OK)
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GetModelLabArchitectureRequest req, CancellationToken ct)
    {
        await Send.OkAsync(await _sources.GetLastAsync(ModelRouteName.Decode(req.ModelName)!.Trim(), ct), ct);
    }
}
