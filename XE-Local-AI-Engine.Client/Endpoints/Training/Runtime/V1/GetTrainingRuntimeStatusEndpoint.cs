namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runtime;

public sealed class GetTrainingRuntimeStatusEndpoint : EndpointWithoutRequest<TrainingRuntimeStatusResponse>
{
    private readonly TrainingRuntimeOrchestrationService _runtime;

    public GetTrainingRuntimeStatusEndpoint(TrainingRuntimeOrchestrationService runtime)
    {
        _runtime = runtime;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.RuntimeStatus);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<TrainingRuntimeStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(_runtime.GetStatus().ToResponse(), ct);
    }
}
