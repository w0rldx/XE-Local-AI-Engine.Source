namespace XE_Local_AI_Engine.Client.Endpoints.Training.Datasets.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class ListTrainingDatasetsEndpoint : EndpointWithoutRequest<ListTrainingDatasetsResponse>
{
    private readonly TrainingDatasetService _datasets;

    public ListTrainingDatasetsEndpoint(TrainingDatasetService datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        _datasets = datasets;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Datasets);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var records = await _datasets.ListAsync(ct);
        await Send.OkAsync(new ListTrainingDatasetsResponse
        {
            Items = records.Select(record => record.ToResponse()).ToArray()
        }, ct);
    }
}

public sealed class GetTrainingDatasetEndpoint : Endpoint<GetTrainingDatasetRequest, TrainingDatasetResponse>
{
    private readonly TrainingDatasetService _datasets;

    public GetTrainingDatasetEndpoint(TrainingDatasetService datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        _datasets = datasets;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.DatasetById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetTrainingDatasetRequest req, CancellationToken ct)
    {
        var record = await _datasets.GetAsync(req.DatasetId, ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}

public sealed class DeleteTrainingDatasetEndpoint : Endpoint<DeleteTrainingDatasetRequest>
{
    private readonly TrainingDatasetService _datasets;

    public DeleteTrainingDatasetEndpoint(TrainingDatasetService datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        _datasets = datasets;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.DatasetById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteTrainingDatasetRequest req, CancellationToken ct)
    {
        await _datasets.DeleteAsync(req.DatasetId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
    }
}

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

public sealed class ListTrainingSamplesEndpoint : Endpoint<ListTrainingSamplesRequest, ListTrainingSamplesResponse>
{
    private readonly TrainingDatasetService _datasets;

    public ListTrainingSamplesEndpoint(TrainingDatasetService datasets)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        _datasets = datasets;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.DatasetSamples);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListTrainingSamplesRequest req, CancellationToken ct)
    {
        if (req.Page < 1 || req.PageSize is < 1 or > 200)
        {
            AddError("Page must be positive and pageSize must be between 1 and 200.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var page = await _datasets.ListSamplesAsync(new TrainingSampleQuery(req.DatasetId, req.Page, req.PageSize, req.Label, req.ReviewState, req.Kind), ct);
        await Send.OkAsync(new ListTrainingSamplesResponse
        {
            Items = page.Items.Select(item => item.ToResponse()).ToArray(),
            TotalCount = page.TotalCount,
            Page = req.Page,
            PageSize = req.PageSize
        }, ct);
    }
}

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
        var record = await _datasets.ReviewSampleAsync(new TrainingSampleReviewCommand(req.SampleId, req.Verb, req.Label), ct);
        await Send.OkAsync(record.ToResponse(), ct);
    }
}

public sealed class ExportTrainingDatasetEndpoint : Endpoint<ExportTrainingDatasetRequest, ExportTrainingDatasetResponse>
{
    private readonly IDatasetExportService _export;

    public ExportTrainingDatasetEndpoint(IDatasetExportService export)
    {
        ArgumentNullException.ThrowIfNull(export);
        _export = export;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.DatasetExport);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ExportTrainingDatasetRequest req, CancellationToken ct)
    {
        var content = await _export.ExportAsync(req.DatasetId, req.Format, ct);
        await Send.OkAsync(new ExportTrainingDatasetResponse
        {
            DatasetId = req.DatasetId,
            Format = req.Format,
            Content = content,
            LineCount = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
        }, ct);
    }
}
