namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class CancelLlamaCppSourceBuildEndpoint : EndpointWithoutRequest<LlamaCppSourceBuildStatusResponse>
{
    private readonly LlamaCppRuntimeOrchestrationService _runtime;

    public CancelLlamaCppSourceBuildEndpoint(LlamaCppRuntimeOrchestrationService runtime)
    {
        _runtime = runtime;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.SourceBuildCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<LlamaCppSourceBuildStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        _runtime.CancelSourceBuild();
        await Send.OkAsync(_runtime.GetSourceBuildStatus().ToResponse(), ct);
    }
}
