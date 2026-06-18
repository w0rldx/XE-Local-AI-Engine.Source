namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.ModelFit.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelFitQueryService" /> tests: the cache reader returns the assembled view when a latest
///     successful recommendation snapshot exists, returns null on a cache-miss, and — by construction — has NO dependency
///     on the refresh service / advisor (it cannot run a recommendation). The approved-image listing was removed in
///     Lane C (plan §8); the constructor's two-store signature is the structural proof of the no-runner invariant.
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
    public async Task GetLatestRecommendationsAsync_WhenPullModelOnNode_MarksInstalledFromNodeNotLlmfit()
    {
        // The node has the tag though the snapshot stored installed:false (offline recommend); a different row stored
        // installed:true is NOT on the node. Install state must follow the node, not llmfit's offline flag.
        var harness = Harness.Create(installedModelNames: ["qwen2.5-coder:7b"]);
        var snapshotId = harness.SeedLatestRecommendationSnapshot(useCase: "coding");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput(1, "qwen2.5-coder:7b", "qwen2.5-coder:7b", 82.5, null, null, null, null, null, null, 16384, false, "qwen2.5-coder:7b", null),
            new ModelFitRecommendationInput(2, "deepseek-coder:1.3b", null, 61.0, null, null, null, null, null, null, 16384, true, "deepseek-coder:1.3b", null));

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.NotNull(view);
        AssertEx.True(view!.Recommendations[0].IsInstalled, "a row whose tag is installed on the node is Installed despite llmfit's stored false.");
        AssertEx.False(view.Recommendations[1].IsInstalled, "a row whose tag is absent on the node is not Installed despite llmfit's stored true.");
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenPullModelNameNull_IsNotInstalled()
    {
        // An HF-only model with no Ollama tag cannot be matched to the install list → not installed (unknown).
        var harness = Harness.Create(installedModelNames: ["qwen2.5-coder:7b"]);
        var snapshotId = harness.SeedLatestRecommendationSnapshot(useCase: "coding");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput(1, "Some/HF-Only-Model", null, 50.0, null, null, null, null, null, null, null, true, null, null));

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.NotNull(view);
        AssertEx.False(view!.Recommendations[0].IsInstalled);
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenTagOmitsLatest_MatchesInstalledLatest()
    {
        // The recommendation carries the bare name; the node lists the `:latest` form — they must match.
        var harness = Harness.Create(installedModelNames: ["Mistral:latest"]);
        var snapshotId = harness.SeedLatestRecommendationSnapshot(useCase: "general");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput(1, "mistral", "mistral", 70.0, null, null, null, null, null, null, null, false, "mistral", null));

        var view = await harness.Service.GetLatestRecommendationsAsync("general", ProviderName, CancellationToken.None);

        AssertEx.NotNull(view);
        AssertEx.True(view!.Recommendations[0].IsInstalled, "a bare name matches the node's :latest tag, case-insensitively.");
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenInstallListThrows_FallsBackToStoredFlag()
    {
        // Ollama unreachable: the install-state enrichment must not fail the cached read — keep the stored flag.
        var harness = Harness.Create(throwOnList: true);
        var snapshotId = harness.SeedLatestRecommendationSnapshot(useCase: "coding");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput(1, "qwen2.5-coder:7b", "qwen2.5-coder:7b", 82.5, null, null, null, null, null, null, 16384, true, "qwen2.5-coder:7b", null));

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.NotNull(view);
        AssertEx.True(view!.Recommendations[0].IsInstalled, "the stored flag is preserved when the node list cannot be read.");
    }

    private sealed class Harness
    {
        public required ModelFitQueryService Service { get; init; }
        public required InMemoryModelFitSnapshotStore SnapshotStore { get; init; }
        public required SeedableRecommendationStore RecommendationStore { get; init; }

        public static Harness Create(IEnumerable<string>? installedModelNames = null, bool throwOnList = false)
        {
            var snapshotStore = new InMemoryModelFitSnapshotStore();
            var recommendationStore = new SeedableRecommendationStore();
            var ollamaModelService = new FakeOllamaModelService(installedModelNames, throwOnList);

            var service = new ModelFitQueryService(snapshotStore,
                recommendationStore,
                ollamaModelService,
                NullLogger<ModelFitQueryService>.Instance);

            return new Harness
            {
                Service = service,
                SnapshotStore = snapshotStore,
                RecommendationStore = recommendationStore
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
    }

    /// <summary>
    ///     A thin recommendation store that supports direct seeding of rows for a snapshot (the query service only reads).
    ///     Wraps the shared <see cref="InMemoryModelFitRecommendationStore" /> replace/read contract.
    /// </summary>
    private sealed class SeedableRecommendationStore : IModelFitRecommendationStore
    {
        private readonly InMemoryModelFitRecommendationStore _inner = new();

        public Task<int> ReplaceForSnapshotAsync(Guid snapshotId, IReadOnlyList<ModelFitRecommendationInput> recommendations, CancellationToken cancellationToken = default)
        {
            return _inner.ReplaceForSnapshotAsync(snapshotId, recommendations, cancellationToken);
        }

        public Task<IReadOnlyList<ModelFitRecommendationRecord>> ListForSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default)
        {
            return _inner.ListForSnapshotAsync(snapshotId, cancellationToken);
        }

        public void Seed(Guid snapshotId, params ModelFitRecommendationInput[] rows)
        {
            _inner.ReplaceForSnapshotAsync(snapshotId, rows).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    ///     Minimal <see cref="IOllamaModelService" /> fake for the install-state join: returns a configured set of
    ///     installed model tags (or throws, to exercise the best-effort fallback). Only <c>ListLocalModelsAsync</c> is
    ///     used by the query service; the rest are unsupported.
    /// </summary>
    private sealed class FakeOllamaModelService(IEnumerable<string>? installedModelNames, bool throwOnList) : IOllamaModelService
    {
        private readonly IReadOnlyList<string> _installed = installedModelNames?.ToList() ?? [];

        public Task<IEnumerable<Model>> ListLocalModelsAsync(CancellationToken ct = default)
        {
            if (throwOnList)
            {
                throw new InvalidOperationException("Ollama unreachable (test).");
            }

            return Task.FromResult(_installed.Select(name => new Model
            {
                Name = name
            }));
        }

        public Task<ShowModelResponse> ShowModelAsync(string modelName, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<OllamaModelDetails> ShowModelDetailsAsync(string modelName, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<PullModelResponse> PullModelAsync(string modelName, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteModelAsync(string modelName, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RunningModelSnapshot>> ListRunningModelsAsync(CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task UnloadModelAsync(string modelName, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }
    }
}
