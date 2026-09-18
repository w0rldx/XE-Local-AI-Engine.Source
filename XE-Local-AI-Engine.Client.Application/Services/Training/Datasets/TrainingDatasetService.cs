namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The dataset and sample endpoints' only door onto the training dataset store: the dataset list, the dataset read,
///     the dataset delete, the sample page and the sample review verb. Each call arrives here unchanged — this type adds
///     no policy of its own, because the definition service owns definitions and the generation service owns creation
///     and cancel.
/// </summary>
public sealed class TrainingDatasetService
{
    private readonly ITrainingDatasetStore _store;

    public TrainingDatasetService(ITrainingDatasetStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <summary>Every dataset the node holds, newest first, exactly as the store orders them.</summary>
    public Task<IReadOnlyList<TrainingDatasetRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        _store.ListDatasetsAsync(cancellationToken);

    /// <summary>The dataset, or <c>null</c> when no dataset carries that id.</summary>
    public Task<TrainingDatasetRecord?> GetAsync(Guid datasetId, CancellationToken cancellationToken = default) =>
        _store.GetDatasetAsync(datasetId, cancellationToken);

    /// <summary>Deletes the dataset. Refused while a non-terminal generation work item still references it.</summary>
    public Task DeleteAsync(Guid datasetId, long expectedVersion, CancellationToken cancellationToken = default) =>
        _store.DeleteDatasetAsync(datasetId, expectedVersion, cancellationToken);

    /// <summary>One filtered, paged slice of a dataset's samples.</summary>
    public Task<TrainingSamplePage> ListSamplesAsync(TrainingSampleQuery query, CancellationToken cancellationToken = default) =>
        _store.ListSamplesAsync(query, cancellationToken);

    /// <summary>Applies a review verb to one sample and returns the sample as it now stands.</summary>
    public Task<TrainingSampleRecord> ReviewSampleAsync(TrainingSampleReviewCommand command, CancellationToken cancellationToken = default) =>
        _store.ReviewSampleAsync(command, cancellationToken);
}
