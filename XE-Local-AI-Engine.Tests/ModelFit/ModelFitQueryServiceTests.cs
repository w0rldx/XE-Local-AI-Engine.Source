namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.ModelFit.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelFitQueryService" /> tests: the cache reader returns the assembled view when a latest successful
///     snapshot exists and null on a cache-miss. Its constructor takes neither the refresh service nor the advisor,
///     the structural proof that a read cannot reach them.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ModelFitQueryServiceTests
{
    private const string ApprovedImageId = "llmfit-recommender-0-9-30";
    private const string ProviderName = "llama.cpp";
    private const string CoderModel = "org/Qwen2.5-Coder-7B-GGUF:Q5_K_M";
    private const string OtherModel = "org/DeepSeek-Coder-1.3B-GGUF:Q4_K_M";

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
        var snapshotId = harness.SeedLatestRecommendationSnapshot("coding");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput
            {
                Rank = 1,
                ModelName = CoderModel,
                ProviderModelName = CoderModel,
                Score = 82.5,
                FitLevel = "Good",
                RunMode = "GPU",
                Quantization = "Q5_K_M",
                EstimatedTokensPerSecond = 48.2,
                RequiredRamMb = 6144d,
                RequiredVramMb = null,
                ContextTokens = 16384,
                IsInstalled = true,
                PullModelName = CoderModel,
                DiagnosticsJson = null
            },
            new ModelFitRecommendationInput
            {
                Rank = 2,
                ModelName = OtherModel,
                ProviderModelName = OtherModel,
                Score = 61.0,
                FitLevel = "Marginal",
                RunMode = "CPU",
                Quantization = "Q4_K_M",
                EstimatedTokensPerSecond = 12.0,
                RequiredRamMb = 1536d,
                RequiredVramMb = null,
                ContextTokens = 16384,
                IsInstalled = false,
                PullModelName = OtherModel,
                DiagnosticsJson = null
            });

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.NotNull(view);
        AssertEx.Equal(snapshotId, view!.SnapshotId);
        AssertEx.Equal(ModelFitRunStatus.Succeeded, view.Status);
        AssertEx.Equal(ApprovedImageId, view.ApprovedImageId);
        AssertEx.Equal("coding", view.UseCase ?? string.Empty);
        AssertEx.Equal(ProviderName, view.ProviderName);
        AssertEx.Equal(expected: 2, view.Recommendations.Count);
        AssertEx.Equal(expected: 1, view.Recommendations[0].Record.Rank);
        AssertEx.Equal(CoderModel, view.Recommendations[0].Record.ModelName);
        AssertEx.Equal(expected: 2, view.Recommendations[1].Record.Rank);
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenUseCaseDiffers_ReturnsNull()
    {
        var harness = Harness.Create();
        var snapshotId = harness.SeedLatestRecommendationSnapshot("coding");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput
            {
                Rank = 1,
                ModelName = CoderModel,
                ProviderModelName = null,
                Score = 82.5,
                FitLevel = null,
                RunMode = null,
                Quantization = null,
                EstimatedTokensPerSecond = null,
                RequiredRamMb = null,
                RequiredVramMb = null,
                ContextTokens = null,
                IsInstalled = false,
                PullModelName = null,
                DiagnosticsJson = null
            });

        // The latest snapshot is for "coding"; a "reasoning" query is a different key → cache-miss.
        var view = await harness.Service.GetLatestRecommendationsAsync("reasoning", ProviderName, CancellationToken.None);

        AssertEx.Null(view, "the latest-successful key includes use-case, so a different use-case is a cache-miss.");
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenPullModelOnNode_MarksInstalledFromNodeNotLlmfit()
    {
        // The node has the GGUF though the snapshot stored installed:false (offline recommend); a different row stored
        // installed:true is NOT on the node. Install state must follow the node, not llmfit's offline flag.
        var harness = Harness.Create([CoderModel]);
        var snapshotId = harness.SeedLatestRecommendationSnapshot("coding");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput
            {
                Rank = 1,
                ModelName = CoderModel,
                ProviderModelName = CoderModel,
                Score = 82.5,
                FitLevel = null,
                RunMode = null,
                Quantization = null,
                EstimatedTokensPerSecond = null,
                RequiredRamMb = null,
                RequiredVramMb = null,
                ContextTokens = 16384,
                IsInstalled = false,
                PullModelName = CoderModel,
                DiagnosticsJson = null
            },
            new ModelFitRecommendationInput
            {
                Rank = 2,
                ModelName = OtherModel,
                ProviderModelName = null,
                Score = 61.0,
                FitLevel = null,
                RunMode = null,
                Quantization = null,
                EstimatedTokensPerSecond = null,
                RequiredRamMb = null,
                RequiredVramMb = null,
                ContextTokens = 16384,
                IsInstalled = true,
                PullModelName = OtherModel,
                DiagnosticsJson = null
            });

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.NotNull(view);
        AssertEx.True(view!.Recommendations[0].Record.IsInstalled, "a row whose GGUF is installed on the node is Installed despite llmfit's stored false.");
        AssertEx.False(view.Recommendations[1].Record.IsInstalled, "a row whose GGUF is absent on the node is not Installed despite llmfit's stored true.");
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenPullModelNameNull_IsNotInstalled()
    {
        // A row with no pull name cannot be matched to the install list → not installed (unknown).
        var harness = Harness.Create([CoderModel]);
        var snapshotId = harness.SeedLatestRecommendationSnapshot("coding");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput
            {
                Rank = 1,
                ModelName = "Some/HF-Only-Model",
                ProviderModelName = null,
                Score = 50.0,
                FitLevel = null,
                RunMode = null,
                Quantization = null,
                EstimatedTokensPerSecond = null,
                RequiredRamMb = null,
                RequiredVramMb = null,
                ContextTokens = null,
                IsInstalled = true,
                PullModelName = null,
                DiagnosticsJson = null
            });

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.NotNull(view);
        AssertEx.False(view!.Recommendations[0].Record.IsInstalled);
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenNameDiffersOnlyByCase_MatchesTheInstalledGguf()
    {
        // The advisor and the GGUF store format the same {repo}:{quant} name, but a repo's casing is not guaranteed.
        var harness = Harness.Create(["ORG/Mistral-GGUF:q4_k_m"]);
        var snapshotId = harness.SeedLatestRecommendationSnapshot("general");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput
            {
                Rank = 1,
                ModelName = "org/Mistral-GGUF:Q4_K_M",
                ProviderModelName = "org/Mistral-GGUF:Q4_K_M",
                Score = 70.0,
                FitLevel = null,
                RunMode = null,
                Quantization = null,
                EstimatedTokensPerSecond = null,
                RequiredRamMb = null,
                RequiredVramMb = null,
                ContextTokens = null,
                IsInstalled = false,
                PullModelName = "org/Mistral-GGUF:Q4_K_M",
                DiagnosticsJson = null
            });

        var view = await harness.Service.GetLatestRecommendationsAsync("general", ProviderName, CancellationToken.None);

        AssertEx.NotNull(view);
        AssertEx.True(view!.Recommendations[0].Record.IsInstalled, "the install-state match is case-insensitive.");
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenInstallListThrows_FallsBackToStoredFlag()
    {
        // Listing failed: the install-state enrichment must not fail the cached read — keep the stored flag.
        var harness = Harness.Create(throwOnList: true);
        var snapshotId = harness.SeedLatestRecommendationSnapshot("coding");
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput
            {
                Rank = 1,
                ModelName = CoderModel,
                ProviderModelName = CoderModel,
                Score = 82.5,
                FitLevel = null,
                RunMode = null,
                Quantization = null,
                EstimatedTokensPerSecond = null,
                RequiredRamMb = null,
                RequiredVramMb = null,
                ContextTokens = 16384,
                IsInstalled = true,
                PullModelName = CoderModel,
                DiagnosticsJson = null
            });

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.NotNull(view);
        AssertEx.True(view!.Recommendations[0].Record.IsInstalled, "the stored flag is preserved when the node list cannot be read.");
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenDiagnosticsJsonHasSignals_MapperExtractsBothFromBlob()
    {
        // Verifies the end-to-end blob path: release_date and is_trusted_publisher written by the parser into
        // DiagnosticsJson are extracted by ModelFitMapper.ToResponse() and surface on the response DTO.
        var harness = Harness.Create();
        var snapshotId = harness.SeedLatestRecommendationSnapshot("coding");
        const string diagnosticsJson = """{"release_date":"2026-01-15","is_trusted_publisher":false,"is_gated":true}""";
        harness.RecommendationStore.Seed(snapshotId,
            new ModelFitRecommendationInput
            {
                Rank = 1,
                ModelName = "qwen3-coder",
                ProviderModelName = "qwen3-coder",
                Score = 80.0,
                FitLevel = "Good",
                RunMode = "GPU",
                Quantization = "Q4_K_M",
                EstimatedTokensPerSecond = 30.0,
                RequiredRamMb = 4096d,
                RequiredVramMb = null,
                ContextTokens = 8192,
                IsInstalled = false,
                PullModelName = "qwen3-coder",
                DiagnosticsJson = diagnosticsJson
            });

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.NotNull(view);
        var response = view!.ToResponse();
        AssertEx.Equal("2026-01-15", response.Recommendations[0].ReleaseDate);
        AssertEx.False(response.Recommendations[0].IsTrustedPublisher);
        AssertEx.True(response.Recommendations[0].IsGated);
    }

    [Test]
    public async Task GetLatestRecommendationsAsync_WhenDiagnosticsListSkippedEntries_SurfacesThem()
    {
        var harness = Harness.Create();
        harness.SeedLatestRecommendationSnapshot("coding", """{"gpu_accel":true,"skipped_catalog_entries":["Gemma 4 12B","Qwen3 8B"]}""");

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.Equal("Gemma 4 12B|Qwen3 8B", string.Join('|', AssertEx.NotNull(view).SkippedCatalogEntries));
    }

    [Test]
    [Arguments(null)]
    [Arguments("{}")]
    [Arguments("""{"gpu_accel":true}""")]
    [Arguments("{not json")]
    [Arguments("[1,2]")]
    [Arguments("""{"skipped_catalog_entries":"Qwen3 8B"}""")]
    public async Task GetLatestRecommendationsAsync_WhenDiagnosticsLackOrMangleTheMember_SkipsNone(string? diagnosticsJson)
    {
        var harness = Harness.Create();
        harness.SeedLatestRecommendationSnapshot("coding", diagnosticsJson);

        var view = await harness.Service.GetLatestRecommendationsAsync("coding", ProviderName, CancellationToken.None);

        AssertEx.Empty(AssertEx.NotNull(view).SkippedCatalogEntries);
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
            var provider = Substitute.For<ILocalModelProvider>();
            provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
            if (throwOnList)
            {
                provider.ListModelsAsync(Arg.Any<CancellationToken>())
                        .Returns<Task<IReadOnlyList<LocalModelDescriptor>>>(_ => throw new InvalidOperationException("GGUF store unreadable (test)."));
            }
            else
            {
                provider.ListModelsAsync(Arg.Any<CancellationToken>())
                        .Returns([.. (installedModelNames ?? []).Select(InstalledGguf)]);
            }

            var service = new ModelFitQueryService(snapshotStore,
                recommendationStore,
                SingleProviderResolverFactory.Create(provider),
                NullLogger<ModelFitQueryService>.Instance);

            return new Harness
            {
                Service = service,
                SnapshotStore = snapshotStore,
                RecommendationStore = recommendationStore
            };
        }

        private static LocalModelDescriptor InstalledGguf(string modelName)
        {
            return new LocalModelDescriptor
            {
                ModelName = modelName,
                ProviderName = LlamaServerProviderConstants.ProviderName,
                IsAvailable = true,
                SizeBytes = 1024,
                ModifiedAt = null,
                MaxContextTokens = null
            };
        }

        public Guid SeedLatestRecommendationSnapshot(string? useCase, string? diagnosticsJson = "{}")
        {
            // Open then mark Succeeded so the in-memory store sets is_latest_successful via its real transition path.
            var summary = SnapshotStore
                          .CreateRunningAsync(new ModelFitSnapshotInput
                          {
                              ApprovedImageId = ApprovedImageId,
                              Operation = ModelFitOperation.Recommend,
                              UseCase = useCase,
                              ProviderName = ProviderName,
                              ModelName = null,
                              Status = ModelFitRunStatus.Running,
                              StartedAtUtc = 1L
                          })
                          .GetAwaiter().GetResult();
            SnapshotStore.MarkTerminalAsync(summary.Id, ModelFitRunStatus.Succeeded, exitCode: 0, durationMs: 100, "{}", stderrExcerpt: null, diagnosticsJson, completedAtUtc: 2L)
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
}
