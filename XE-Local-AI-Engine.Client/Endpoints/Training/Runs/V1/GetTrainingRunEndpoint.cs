namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

public sealed class GetTrainingRunEndpoint : Endpoint<TrainingRunByIdRequest, TrainingRunResponse>
{
    private readonly ITrainingRunService _runs;

    public GetTrainingRunEndpoint(ITrainingRunService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(TrainingRunByIdRequest req, CancellationToken ct)
    {
        var run = await _runs.GetAsync(req.RunId, ct);
        if (run is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(run.ToResponse(), ct);
    }
}
