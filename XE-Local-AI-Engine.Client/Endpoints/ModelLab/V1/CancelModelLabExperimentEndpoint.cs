namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class CancelModelLabExperimentEndpoint : Endpoint<ModelLabExperimentRouteRequest>
{
    private readonly ModelLabExperimentService _experiments;

    public CancelModelLabExperimentEndpoint(ModelLabExperimentService experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        _experiments = experiments;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelLab.ExperimentCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces(StatusCodes.Status202Accepted)
                               .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ModelLabExperimentRouteRequest request, CancellationToken ct)
    {
        // 404 also when it already finished: a cancel only applies to the job running now, and the page refetches on either answer.
        if (_experiments.Cancel(request.ExperimentId))
        {
            await Send.ResultAsync(TypedResults.Accepted((string?)null));
            return;
        }

        await Send.NotFoundAsync(ct);
    }
}
