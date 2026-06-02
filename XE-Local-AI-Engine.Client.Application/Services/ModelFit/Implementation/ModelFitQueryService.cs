namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Default <see cref="IModelFitQueryService" />: a thin cache reader over the M1 stores. It composes the
///     approved-image registry store, the sanitized snapshot-summary store and the normalized recommendation store. It
///     takes NO dependency on the utility runner or the refresh service, so a read can never start an llmfit run.
/// </summary>
public sealed class ModelFitQueryService(
    IApprovedUtilityImageStore approvedImageStore,
    IModelFitSnapshotStore snapshotStore,
    IModelFitRecommendationStore recommendationStore) : IModelFitQueryService
{
    private readonly IApprovedUtilityImageStore _approvedImageStore = approvedImageStore ?? throw new ArgumentNullException(nameof(approvedImageStore));
    private readonly IModelFitSnapshotStore _snapshotStore = snapshotStore ?? throw new ArgumentNullException(nameof(snapshotStore));
    private readonly IModelFitRecommendationStore _recommendationStore = recommendationStore ?? throw new ArgumentNullException(nameof(recommendationStore));

    public Task<IReadOnlyList<ApprovedUtilityImageRecord>> ListApprovedImagesAsync(CancellationToken cancellationToken = default) =>
        _approvedImageStore.ListAsync(cancellationToken);

    public async Task<ModelFitLatestRecommendationsView?> GetLatestRecommendationsAsync(
        string? useCase,
        string providerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        // Cache-only: the latest successful recommendation snapshot for this key. A recommendation snapshot has a null
        // model-name (the latest-successful key matches on null), so model-name is fixed to null here.
        var summary = await _snapshotStore.GetLatestSuccessfulSummaryAsync(
                ModelFitOperation.Recommend,
                useCase,
                providerName,
                modelName: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (summary is null)
        {
            // No cached recommendation snapshot has ever succeeded for this key — the caller surfaces the empty state.
            return null;
        }

        var recommendations = await _recommendationStore.ListForSnapshotAsync(summary.Id, cancellationToken).ConfigureAwait(false);

        return new ModelFitLatestRecommendationsView(
            summary.Id,
            summary.Status,
            summary.ApprovedImageId,
            summary.UseCase,
            summary.ProviderName,
            summary.CompletedAtUtc,
            recommendations);
    }
}
