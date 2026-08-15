namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Training.Contracts;

public sealed class GetTrainingRuntimePrerequisitesEndpoint(ITrainingRuntimePrerequisiteProbe prerequisiteProbe)
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
        var report = await prerequisiteProbe.ProbeAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(report.ToResponse(), ct).ConfigureAwait(false);
    }
}
