namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class GetLlamaCppSourceBuildPrerequisitesEndpoint : Endpoint<GetLlamaCppSourceBuildPrerequisitesRequest, LlamaCppSourceBuildPrerequisitesResponse>
{
    private readonly LlamaCppRuntimeOrchestrationService _runtime;

    public GetLlamaCppSourceBuildPrerequisitesEndpoint(LlamaCppRuntimeOrchestrationService runtime)
    {
        _runtime = runtime;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.SourceBuildPrerequisites);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<LlamaCppSourceBuildPrerequisitesResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(GetLlamaCppSourceBuildPrerequisitesRequest request, CancellationToken ct)
    {
        var backend = request.Backend.ToContract();
        var report = await _runtime.ProbeSourceBuildPrerequisitesAsync(backend, ct);
        await Send.OkAsync(report.ToResponse(backend), ct);
    }
}
