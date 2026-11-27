namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>404 for a run that recorded no routing (saved before contract 8, a model with no router) or a steering row; 422 when the stored lines fail their check.</summary>
public sealed class GetModelLabExperimentRoutingEndpoint : Endpoint<ModelLabExperimentRouteRequest, ModelLabRoutingResponse>
{
    private readonly ModelLabExperimentReader _experiments;

    public GetModelLabExperimentRoutingEndpoint(ModelLabExperimentReader experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        _experiments = experiments;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelLab.ExperimentRouting);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabRoutingResponse>(StatusCodes.Status200OK)
                               .Produces(StatusCodes.Status404NotFound)
                               .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(ModelLabExperimentRouteRequest request, CancellationToken ct)
    {
        var routing = await _experiments.ReadRoutingAsync(request.ExperimentId, ct);
        switch (routing)
        {
            case { Status: ModelLabTraceReadStatus.Found, Summary: { } summary }:
                await Send.OkAsync(new ModelLabRoutingResponse
                {
                    Layers = summary.Layers,
                    Experts = summary.Experts,
                    ExpertsPerToken = summary.ExpertsPerToken,
                    NormalizedTopK = summary.NormalizedTopK,
                    Steps =
                    [
                        .. routing.Records.Select(record => new ModelLabRoutingStepResponse
                        {
                            Step = record.Step,
                            Blocks =
                            [
                                .. summary.Layers.Select((layer, block) => new ModelLabRoutingBlockResponse
                                {
                                    Layer = layer,
                                    Experts = record.Experts[block],
                                    Weights = [.. record.WeightBits[block].Select(static bits => (double)ModelLabTraceSpec.RoutingWeight(bits))]
                                })
                            ]
                        })
                    ],
                    Truncated = routing.Footer?.Truncated ?? false,
                    DroppedSteps = routing.Footer?.Dropped ?? 0
                }, ct);
                break;
            case { Status: ModelLabTraceReadStatus.NotFound }:
                await Send.NotFoundAsync(ct);
                break;
            default:
                AddError("The stored routing could not be read or failed its integrity check.");
                await Send.ErrorsAsync(StatusCodes.Status422UnprocessableEntity, ct);
                break;
        }
    }
}
