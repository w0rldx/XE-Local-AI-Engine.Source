namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runs.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runs;

public sealed class CancelTrainingRunEndpoint : Endpoint<TrainingRunByIdRequest>
{
    private readonly ITrainingRunService _runs;

    public CancelTrainingRunEndpoint(ITrainingRunService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.RunCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        // The run id is the whole request and it comes from the route, so this POST has no body. Without declaring
        // that, FastEndpoints requires a JSON body and a bodyless cancel is answered with 415 instead of acting.
        Description(builder => builder.Accepts<TrainingRunByIdRequest>());
    }

    public override async Task HandleAsync(TrainingRunByIdRequest req, CancellationToken ct)
    {
        if (!await _runs.CancelAsync(req.RunId, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
