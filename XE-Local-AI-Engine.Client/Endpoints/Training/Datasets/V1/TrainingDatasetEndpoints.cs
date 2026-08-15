namespace XE_Local_AI_Engine.Client.Endpoints.Training.Datasets.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

public sealed class ListTrainingDatasetsEndpoint(ITrainingDatasetStore store)
    : EndpointWithoutRequest<ListTrainingDatasetsResponse>
{
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Datasets);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var records = await _store.ListDatasetsAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListTrainingDatasetsResponse
        {
            Items = records.Select(record => record.ToResponse()).ToArray()
        }, ct).ConfigureAwait(false);
    }
}

public sealed class GetTrainingDatasetEndpoint(ITrainingDatasetStore store)
    : Endpoint<GetTrainingDatasetRequest, TrainingDatasetResponse>
{
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.DatasetById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetTrainingDatasetRequest req, CancellationToken ct)
    {
        var record = await _store.GetDatasetAsync(req.DatasetId, ct).ConfigureAwait(false);
        if (record is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class DeleteTrainingDatasetEndpoint(ITrainingDatasetStore store)
    : Endpoint<DeleteTrainingDatasetRequest>
{
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.DatasetById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteTrainingDatasetRequest req, CancellationToken ct)
    {
        try
        {
            await _store.DeleteDatasetAsync(req.DatasetId, req.ExpectedVersion, ct).ConfigureAwait(false);
            await Send.NoContentAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (TrainingEndpointSupport.IsHandled(exception))
        {
            await Send.ResultAsync(TrainingEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

public sealed class ListTrainingSamplesEndpoint(ITrainingDatasetStore store)
    : Endpoint<ListTrainingSamplesRequest, ListTrainingSamplesResponse>
{
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));

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
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var page = await _store.ListSamplesAsync(new TrainingSampleQuery(req.DatasetId, req.Page, req.PageSize, req.Label, req.ReviewState, req.Kind), ct)
                                   .ConfigureAwait(false);
            await Send.OkAsync(new ListTrainingSamplesResponse
            {
                Items = page.Items.Select(item => item.ToResponse()).ToArray(),
                TotalCount = page.TotalCount,
                Page = req.Page,
                PageSize = req.PageSize
            }, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (TrainingEndpointSupport.IsHandled(exception))
        {
            await Send.ResultAsync(TrainingEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

/// <summary>Sample review verbs. Any accepted mutation bumps the dataset revision and recomputes its fingerprint.</summary>
public sealed class ReviewTrainingSampleEndpoint(ITrainingDatasetStore store)
    : Endpoint<ReviewTrainingSampleRequest, TrainingSampleResponse>
{
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Patch(LocalApiRoutes.Training.DatasetSampleById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ReviewTrainingSampleRequest req, CancellationToken ct)
    {
        try
        {
            var record = await _store.ReviewSampleAsync(new TrainingSampleReviewCommand(req.SampleId, req.Verb, req.Label), ct).ConfigureAwait(false);
            await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (TrainingEndpointSupport.IsHandled(exception))
        {
            await Send.ResultAsync(TrainingEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

public sealed class ExportTrainingDatasetEndpoint(IDatasetExportService export)
    : Endpoint<ExportTrainingDatasetRequest, ExportTrainingDatasetResponse>
{
    private readonly IDatasetExportService _export = export ?? throw new ArgumentNullException(nameof(export));

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.DatasetExport);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ExportTrainingDatasetRequest req, CancellationToken ct)
    {
        try
        {
            var content = await _export.ExportAsync(req.DatasetId, req.Format, ct).ConfigureAwait(false);
            await Send.OkAsync(new ExportTrainingDatasetResponse
            {
                DatasetId = req.DatasetId,
                Format = req.Format,
                Content = content,
                LineCount = content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            }, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (TrainingEndpointSupport.IsHandled(exception))
        {
            await Send.ResultAsync(TrainingEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}
