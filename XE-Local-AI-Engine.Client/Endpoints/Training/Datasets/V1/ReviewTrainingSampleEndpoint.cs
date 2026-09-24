namespace XE_Local_AI_Engine.Client.Endpoints.Training.Datasets.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>Sample review verbs. Any accepted mutation bumps the dataset revision and recomputes its fingerprint.</summary>
public sealed class ReviewTrainingSampleEndpoint : Endpoint<ReviewTrainingSampleRequest, TrainingSampleResponse>
{
    private readonly TrainingDatasetService _datasets;

    public ReviewTrainingSampleEndpoint(TrainingDatasetService datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        _datasets = datasets;
    }

    public override void Configure()
    {
        Patch(LocalApiRoutes.Training.DatasetSampleById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ReviewTrainingSampleRequest req, CancellationToken ct)
    {
        var record = await _datasets.ReviewSampleAsync(new TrainingSampleReviewCommand
        {
            SampleId = req.SampleId,
            Verb = req.Verb,
            Label = req.Label
        }, ct);
        await Send.OkAsync(record.ToResponse(), ct);
    }
}
