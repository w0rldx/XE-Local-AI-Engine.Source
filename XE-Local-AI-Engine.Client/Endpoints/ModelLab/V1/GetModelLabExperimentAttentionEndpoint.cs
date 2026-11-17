namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>404 for a run that recorded no attention, a block it did not capture, or a steering row; 422 when the stored rows fail their check.</summary>
public sealed class GetModelLabExperimentAttentionEndpoint : Endpoint<ModelLabExperimentAttentionRouteRequest, ModelLabAttentionResponse>
{
    private readonly ModelLabExperimentService _experiments;

    public GetModelLabExperimentAttentionEndpoint(ModelLabExperimentService experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        _experiments = experiments;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelLab.ExperimentAttention);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabAttentionResponse>(StatusCodes.Status200OK)
                               .Produces(StatusCodes.Status404NotFound)
                               .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(ModelLabExperimentAttentionRouteRequest request, CancellationToken ct)
    {
        var attention = await _experiments.ReadAttentionAsync(request.ExperimentId, request.Layer, ct);
        switch (attention)
        {
            case { Status: ModelLabTraceReadStatus.Found, Summary: { } summary }:
                double scale = summary.WeightScale;
                await Send.OkAsync(new ModelLabAttentionResponse
                {
                    Layer = request.Layer,
                    Heads = summary.Heads,
                    KvHeads = summary.KvHeads,
                    TopKeys = summary.TopKeys,
                    Rows =
                    [
                        .. attention.Records.Select(row => new ModelLabAttentionRowResponse
                        {
                            Step = row.Step,
                            QueryPosition = row.QueryPosition,
                            Heads =
                            [
                                .. row.Keys.Select((keys, head) => new ModelLabAttentionHeadResponse
                                {
                                    Keys = keys,
                                    Weights = [.. row.Weights[head].Select(weight => weight / scale)],
                                    Rest = row.Rest[head] / scale
                                })
                            ]
                        })
                    ],
                    Truncated = attention.Footer?.Truncated ?? false,
                    DroppedRecords = attention.Footer?.Dropped ?? 0
                }, ct);
                break;
            case { Status: ModelLabTraceReadStatus.NotFound }:
                await Send.NotFoundAsync(ct);
                break;
            default:
                AddError("The stored attention rows could not be read or failed their integrity check.");
                await Send.ErrorsAsync(StatusCodes.Status422UnprocessableEntity, ct);
                break;
        }
    }
}
