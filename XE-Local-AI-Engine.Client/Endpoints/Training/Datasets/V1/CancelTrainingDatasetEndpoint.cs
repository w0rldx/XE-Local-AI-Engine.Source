namespace XE_Local_AI_Engine.Client.Endpoints.Training.Datasets.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>
///     Cancels generation like the training-run cancel: a queued dataset is terminalized, a generating one signaled.
///     An unknown dataset is a 404 and a finished one a 409, both from <c>TrainingExceptionHandler</c>.
/// </summary>
public sealed class CancelTrainingDatasetEndpoint : Endpoint<CancelTrainingDatasetRequest>
{
    private readonly IDatasetGenerationService _generation;

    public CancelTrainingDatasetEndpoint(IDatasetGenerationService generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        _generation = generation;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.DatasetCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        // The dataset id is the whole request and it comes from the route, so this POST has no body. Without declaring
        // that, FastEndpoints requires a JSON body and a bodyless cancel is answered with 415 instead of acting.
        Description(builder => builder
                               .Accepts<CancelTrainingDatasetRequest>()
                               .Produces(StatusCodes.Status204NoContent)
                               .Produces<TrainingErrorResponse>(StatusCodes.Status404NotFound)
                               .Produces<TrainingErrorResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancelTrainingDatasetRequest req, CancellationToken ct)
    {
        await _generation.CancelAsync(req.DatasetId, ct);
        await Send.NoContentAsync(ct);
    }
}
