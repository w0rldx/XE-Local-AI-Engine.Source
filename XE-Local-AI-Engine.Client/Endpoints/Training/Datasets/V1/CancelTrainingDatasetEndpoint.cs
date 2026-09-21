namespace XE_Local_AI_Engine.Client.Endpoints.Training.Datasets.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>
///     Cancels generation. Mirrors the training-run cancel: a queued dataset is terminalized, a generating one is
///     signalled through the executor's registry, and an unknown or already-finished dataset is a 404.
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
        Description(builder => builder.Accepts<CancelTrainingDatasetRequest>());
    }

    public override async Task HandleAsync(CancelTrainingDatasetRequest req, CancellationToken ct)
    {
        if (!await _generation.CancelAsync(req.DatasetId, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
