namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;

/// <summary>Reports an installed GGUF's header metadata and tensor list, resolving the file from the model registry; an unknown model is 404.</summary>
public sealed class GetModelGgufInspectionEndpoint : Endpoint<GetModelGgufInspectionRequest, ModelGgufInspectionResponse>
{
    private readonly GgufInspectionService _inspectionService;

    public GetModelGgufInspectionEndpoint(GgufInspectionService inspectionService)
    {
        ArgumentNullException.ThrowIfNull(inspectionService);
        _inspectionService = inspectionService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.LocalModels.GgufInspection);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetModelGgufInspectionRequest req, CancellationToken ct)
    {
        // The validator ran the grammar over the decoded name; inspect that same decoded name. See ModelRouteName.
        var modelName = ModelRouteName.Decode(req.ModelName)!.Trim();
        var inspection = await _inspectionService.InspectAsync(modelName, ct);
        await Send.OkAsync(inspection.ToResponse(modelName), ct);
    }
}
