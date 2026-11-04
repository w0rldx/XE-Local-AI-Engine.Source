namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class GetModelLabExperimentTraceEndpoint : Endpoint<ModelLabExperimentRouteRequest, ModelLabTraceResponse>
{
    private readonly ModelLabExperimentService _experiments;

    public GetModelLabExperimentTraceEndpoint(ModelLabExperimentService experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        _experiments = experiments;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelLab.ExperimentTrace);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabTraceResponse>(StatusCodes.Status200OK)
                               .Produces(StatusCodes.Status404NotFound)
                               .ProducesProblemDetails(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(ModelLabExperimentRouteRequest request, CancellationToken ct)
    {
        var trace = await _experiments.ReadTraceAsync(request.ExperimentId, ct);
        switch (trace.Status)
        {
            case ModelLabTraceReadStatus.Found:
                await Send.OkAsync(new ModelLabTraceResponse
                {
                    Records =
                    [
                        .. trace.Records.Select(static record => new ModelLabTraceRecordResponse
                        {
                            Step = record.Step,
                            Layer = record.Layer,
                            Kind = record.Kind,
                            Norm = record.Norm,
                            Percentiles = record.Percentiles,
                            TopIndices = record.TopIndices,
                            TopValues = record.TopValues
                        })
                    ],
                    Truncated = trace.Footer?.Truncated ?? false,
                    DroppedRecords = trace.Footer?.Dropped ?? 0,
                    ByteCap = trace.Footer?.Cap ?? 0
                }, ct);
                break;
            case ModelLabTraceReadStatus.NotFound:
                await Send.NotFoundAsync(ct);
                break;
            default:
                AddError("The stored trace could not be read or failed its integrity check.");
                await Send.ErrorsAsync(StatusCodes.Status422UnprocessableEntity, ct);
                break;
        }
    }
}
