namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;

/// <summary>An installed GGUF's architecture descriptor, built from its header alone; an unknown model is 404.</summary>
public sealed class GetModelLabArchitectureEndpoint : Endpoint<GetModelLabArchitectureRequest, ModelLabArchitectureResponse>
{
    private readonly GgufInspectionService _inspectionService;

    public GetModelLabArchitectureEndpoint(GgufInspectionService inspectionService)
    {
        ArgumentNullException.ThrowIfNull(inspectionService);
        _inspectionService = inspectionService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelLab.Architecture);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetModelLabArchitectureRequest req, CancellationToken ct)
    {
        // The validator ran the grammar over the decoded name; inspect that same decoded name. See ModelRouteName.
        var modelName = ModelRouteName.Decode(req.ModelName)!.Trim();
        var inspection = await _inspectionService.InspectAsync(modelName, ct);
        await Send.OkAsync(ModelLabArchitectureBuilder.Build(modelName, inspection), ct);
    }
}
