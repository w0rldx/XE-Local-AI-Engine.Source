namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runtime;

public sealed class GetTrainingRuntimePrerequisitesEndpoint(TrainingRuntimeOrchestrationService runtime)
    : EndpointWithoutRequest<TrainingRuntimePrerequisitesResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Training.RuntimePrerequisites);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<TrainingRuntimePrerequisitesResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var report = await runtime.ProbeAsync(ct);
        await Send.OkAsync(report.ToResponse(), ct);
    }
}
