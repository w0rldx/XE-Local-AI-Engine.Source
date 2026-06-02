namespace XE_Local_AI_Engine.Tests.ModelFit;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Tests.ModelFit.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Marker 4 <see cref="ModelFitQueryService" /> tests: the cache reader returns the assembled view when a latest
///     successful recommendation snapshot exists, returns null on a cache-miss, maps the approved-image registry rows,
///     and — by construction — has NO dependency on the utility runner (it cannot run llmfit). The constructor's
///     three-store signature is the structural proof of the no-runner invariant.
/// </summary>
public sealed class ModelFitQueryServiceTests
{
    private const string ApprovedImageId = "llmfit-recommender-0-9-30";
    private const string ProviderName = "ollama";

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenNoSuccessfulSnapshot_ReturnsNull()
    {
        var harness = Harness.Create();

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.Null(view, "a cache-miss must return null so the endpoint can render the empty state.");
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenLatestSnapshotExists_ReturnsViewWithRows()
    {
        var harness = Harness.Create();
        var snapshotId = harness.SeedLatestRecommendationSnapshot(useCase: "coding");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput(1, "qwen2.5-coder:7b", "qwen2.5-coder:7b", 82.5, "Good", "GPU", "Q5_K_M", 48.2, 6144d, null, 16384, true, "qwen2.5-coder:7b", null),
            new ModelFitRecommendationInput(2, "deepseek-coder:1.3b", "deepseek-coder:1.3b", 61.0, "Marginal", "CPU", "Q4_K_M", 12.0, 1536d, null, 16384, false, "deepseek-coder:1.3b", null));

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.NotNull(view);
        AssertEx.Equal(snapshotId, view!.SnapshotId);
        AssertEx.Equal(ModelFitRunStatus.Succeeded, view.Status);
        AssertEx.Equal(ApprovedImageId, view.ApprovedImageId);
        AssertEx.Equal("coding", view.UseCase ?? string.Empty);
        AssertEx.Equal(ProviderName, view.ProviderName);
        AssertEx.Equal(2, view.Recommendations.Count);
        AssertEx.Equal(1, view.Recommendations[0].Rank);
        AssertEx.Equal("qwen2.5-coder:7b", view.Recommendations[0].ModelName);
        AssertEx.Equal(2, view.Recommendations[1].Rank);
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenUseCaseDiffers_ReturnsNull()
    {
        var harness = Harness.Create();
        var snapshotId = harness.SeedLatestRecommendationSnapshot(useCase: "coding");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput(1, "qwen2.5-coder:7b", null, 82.5, null, null, null, null, null, null, null, false, null, null));

        // The latest snapshot is for "coding"; a "reasoning" query is a different key → cache-miss.
        var view = await harness.Service.GetLatestRecommendationsAsync("reasoning", ProviderName, CancellationToken.None);

        AssertEx.Null(view, "the latest-successful key includes use-case, so a different use-case is a cache-miss.");
    }

    [Test]
    public async Task ListApprovedImagesAsync_MapsStoreRecords()
    {
        var harness = Harness.Create();

        var images = await harness.Service.ListApprovedImagesAsync(CancellationToken.None);

        AssertEx.Equal(1, images.Count);
        AssertEx.Equal(ApprovedImageId, images[0].ApprovedImageId);
        AssertEx.Equal(UtilityImagePurpose.ModelRecommendation | UtilityImagePurpose.ModelBenchmark, images[0].Purpose);
    }

    private sealed class Harness
    {
        public required ModelFitQueryService Service { get; init; }
        public required InMemoryModelFitSnapshotStore SnapshotStore { get; init; }
        public required SeedableRecommendationStore RecommendationStore { get; init; }
        public required InMemoryApprovedUtilityImageStore ApprovedImageStore { get; init; }

        public static Harness Create()
        {
            var snapshotStore = new InMemoryModelFitSnapshotStore();
            var recommendationStore = new SeedableRecommendationStore();
            var approvedImageStore = new InMemoryApprovedUtilityImageStore(Descriptor());

            var service = new ModelFitQueryService(approvedImageStore, snapshotStore, recommendationStore);

            return new Harness
            {
                Service = service,
                SnapshotStore = snapshotStore,
                RecommendationStore = recommendationStore,
                ApprovedImageStore = approvedImageStore
            };
        }

        public Guid SeedLatestRecommendationSnapshot(string? useCase)
        {
            // Open then mark Succeeded so the in-memory store sets is_latest_successful via its real transition path.
            var summary = SnapshotStore.CreateRunningAsync(new ModelFitSnapshotInput(
                ApprovedImageId, ModelFitOperation.Recommend, useCase, ProviderName, ModelName: null, ModelFitRunStatus.Running, StartedAtUtc: 1L)).GetAwaiter().GetResult();
            SnapshotStore.MarkTerminalAsync(summary.Id, ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 100, rawJson: "{}", stderrExcerpt: null, diagnosticsJson: "{}", completedAtUtc: 2L)
                .GetAwaiter().GetResult();
            return summary.Id;
        }

        private static ApprovedUtilityImageRecord Descriptor() =>
            new(
                ApprovedImageId: ApprovedImageId,
                DisplayName: "llmfit",
                Description: null,
                Purpose: UtilityImagePurpose.ModelRecommendation | UtilityImagePurpose.ModelBenchmark,
                ImageReference: "ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a5197257a3d34a22a52b1e4ea5aecefc1973788c0f6a0a8fd5a4f93c7f93c",
                SourceUrl: null,
                UpstreamVersion: "0.9.30",
                Enabled: true,
                DeprecatedAtUtc: null,
                ReplacementApprovedImageId: null,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0,
                LastUsedAtUtc: null,
                LastSuccessfulRunAtUtc: null,
                DiagnosticsJson: null);
    }

    /// <summary>
    ///     A thin recommendation store that supports direct seeding of rows for a snapshot (the query service only reads).
    ///     Wraps the shared <see cref="InMemoryModelFitRecommendationStore" /> replace/read contract.
    /// </summary>
    private sealed class SeedableRecommendationStore : IModelFitRecommendationStore
    {
        private readonly InMemoryModelFitRecommendationStore _inner = new();

        public void Seed(Guid snapshotId, params ModelFitRecommendationInput[] rows) =>
            _inner.ReplaceForSnapshotAsync(snapshotId, rows).GetAwaiter().GetResult();

        public Task<int> ReplaceForSnapshotAsync(Guid snapshotId, IReadOnlyList<ModelFitRecommendationInput> recommendations, CancellationToken cancellationToken = default) =>
            _inner.ReplaceForSnapshotAsync(snapshotId, recommendations, cancellationToken);

        public Task<IReadOnlyList<ModelFitRecommendationRecord>> ListForSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default) =>
            _inner.ListForSnapshotAsync(snapshotId, cancellationToken);
    }
}
