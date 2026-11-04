namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class GetModelLabExperimentEndpoint : Endpoint<ModelLabExperimentRouteRequest, ModelLabExperimentResponse>
{
    private readonly ModelLabExperimentService _experiments;

    public GetModelLabExperimentEndpoint(ModelLabExperimentService experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        _experiments = experiments;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelLab.Experiment);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabExperimentResponse>(StatusCodes.Status200OK)
                               .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ModelLabExperimentRouteRequest request, CancellationToken ct)
    {
        if (await _experiments.GetAsync(request.ExperimentId, ct) is not { } record)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(ModelLabEndpointMapper.ToResponse(record, includeResult: true), ct);
    }
}
