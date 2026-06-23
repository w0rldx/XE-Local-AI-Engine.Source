namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.ModelFit.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelFitRefreshService" /> (the local model advisor) tests: the refresh path profiles
///     hardware, discovers candidate GGUF files (via a faked <see cref="IHuggingFaceGgufDiscovery" />), estimates each
///     file's fit, drops the non-fitting / insufficient-metadata files, ranks the survivors, persists normalized rows
///     and replaces the latest snapshot; the default quant is <c>Q4_K_M</c> with override honored; download/start
///     delegate to the GGUF model store (<see cref="IGgufModelStore" />) then the llama-server supervisor
///     (<see cref="ILlamaServerProcessSupervisor" />) in order.
/// </summary>
public sealed class ModelFitRefreshServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Test]
    public async Task Advisor_Recommend_PicksFittingGgufFileAndQuant()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();

        // Two repos: one tiny (fits) and one 70B (does not fit a 12 GB VRAM budget).
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([
                     Summary("org/tiny-GGUF"),
                     Summary("org/huge-GGUF")
                 ]));
        discovery.InspectRepoAsync("org/tiny-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/tiny-GGUF", File("Q4_K_M", paramCount: 1_000_000_000L))));
        discovery.InspectRepoAsync("org/huge-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/huge-GGUF", File("Q4_K_M", paramCount: 70_000_000_000L))));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(12 * Gb));

        var result = await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        AssertEx.Equal(expected: 1, result.RecommendationCount);

        var snapshotId = snapshotStore.Snapshots.Values.Single().Id;
        var rows = recommendationStore.RowsFor(snapshotId);
        AssertEx.ContainsSingle(rows, row => row.ModelName == "org/tiny-GGUF:Q4_K_M");
        // The 70B repo was dropped (it exceeds the 12 GB budget).
        AssertEx.False(rows.Any(row => row.ModelName.StartsWith("org/huge", StringComparison.Ordinal)), "the non-fitting 70B model must be dropped.");
        // VRAM required is filled from the GPU-mode estimate (was always null in the Docker path).
        AssertEx.True(rows[0].RequiredVramMb is not null, "GPU-mode fit must fill RequiredVramMb.");
    }

    [Test]
    public async Task Advisor_Recommend_DefaultsQ4KM_RespectsOverride()
    {
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([Summary("org/multi-GGUF")]));
        discovery.InspectRepoAsync("org/multi-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/multi-GGUF",
                     File("Q4_K_M", paramCount: 1_000_000_000L),
                     File("Q8_0", paramCount: 1_000_000_000L))));

        // Default → Q4_K_M selected.
        var snapshotStoreDefault = new InMemoryModelFitSnapshotStore();
        var recommendationStoreDefault = new InMemoryModelFitRecommendationStore();
        var advisorDefault = BuildAdvisor(snapshotStoreDefault, recommendationStoreDefault, discovery, GpuProfile(64 * Gb));
        await advisorDefault.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);
        var defaultRows = recommendationStoreDefault.RowsFor(snapshotStoreDefault.Snapshots.Values.Single().Id);
        AssertEx.Equal("Q4_K_M", defaultRows.Single().Quantization);

        // Override → Q8_0 selected.
        var snapshotStoreOverride = new InMemoryModelFitSnapshotStore();
        var recommendationStoreOverride = new InMemoryModelFitRecommendationStore();
        var advisorOverride = BuildAdvisor(snapshotStoreOverride, recommendationStoreOverride, discovery, GpuProfile(64 * Gb));
        await advisorOverride.RefreshAsync(Request("Q8_0"), reportProgress: null, CancellationToken.None);
        var overrideRows = recommendationStoreOverride.RowsFor(snapshotStoreOverride.Snapshots.Values.Single().Id);
        AssertEx.Equal("Q8_0", overrideRows.Single().Quantization);
    }

    [Test]
    public async Task Advisor_Recommend_DropsInsufficientMetadataFile()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([Summary("org/nometa-GGUF")]));
        // No param count AND no file size → no weights term → insufficient metadata, dropped.
        discovery.InspectRepoAsync("org/nometa-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/nometa-GGUF",
                     new GgufRepoFile("model.gguf", "Q4_K_M", SizeBytes: 0, Sha256: null, "main",
                         Architecture: null, QuantType: null, ParamCount: null, BlockCount: null, AttentionHeadCount: null,
                         AttentionHeadCountKV: null, EmbeddingLength: null, ContextLength: null))));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(64 * Gb));

        var result = await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        AssertEx.Equal(expected: 0, result.RecommendationCount);
    }

    [Test]
    public async Task Advisor_Recommend_SkipsFailingRepoButKeepsOthers()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();

        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([
                     Summary("org/good-GGUF"),
                     Summary("org/bad-GGUF")
                 ]));
        // The good repo inspects fine; the bad repo throws a network failure mid-inspect — it must be skipped, not fail the run.
        discovery.InspectRepoAsync("org/good-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/good-GGUF", File("Q4_K_M", paramCount: 1_000_000_000L))));
        discovery.InspectRepoAsync("org/bad-GGUF", Arg.Any<CancellationToken>())
                 .Returns<Task<GgufRepoDetail>>(_ => throw new HttpRequestException("simulated HF failure"));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(64 * Gb));

        var result = await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        // One bad repo must NOT fail the run — it succeeds with the good candidate only.
        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        AssertEx.Equal(expected: 1, result.RecommendationCount);

        var rows = recommendationStore.RowsFor(snapshotStore.Snapshots.Values.Single().Id);
        AssertEx.ContainsSingle(rows, row => row.ModelName == "org/good-GGUF:Q4_K_M");
        AssertEx.False(rows.Any(row => row.ModelName.StartsWith("org/bad", StringComparison.Ordinal)),
            "the failing repo must be skipped, not surfaced.");
    }

    [Test]
    public async Task Advisor_Recommend_RankingIsDeterministicRegardlessOfCompletionOrder()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();

        // Three repos with IDENTICAL fit (same headroom) so the deterministic tie-break is purely repo-id ordinal.
        // The "first" repo by id (org/a) is made to complete LAST via a delay, proving completion order does not change ranking.
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([
                     Summary("org/c-GGUF"),
                     Summary("org/a-GGUF"),
                     Summary("org/b-GGUF")
                 ]));
        discovery.InspectRepoAsync("org/a-GGUF", Arg.Any<CancellationToken>())
                 .Returns(async call =>
                 {
                     await Task.Delay(millisecondsDelay: 120, call.Arg<CancellationToken>());
                     return Detail("org/a-GGUF", File("Q4_K_M", paramCount: 1_000_000_000L));
                 });
        discovery.InspectRepoAsync("org/b-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/b-GGUF", File("Q4_K_M", paramCount: 1_000_000_000L))));
        discovery.InspectRepoAsync("org/c-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/c-GGUF", File("Q4_K_M", paramCount: 1_000_000_000L))));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(64 * Gb));

        var result = await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        AssertEx.Equal(expected: 3, result.RecommendationCount);

        var rows = recommendationStore.RowsFor(snapshotStore.Snapshots.Values.Single().Id);
        // Equal headroom → ordered by repo id ordinal: a, b, c — independent of which inspect finished first.
        AssertEx.Equal("org/a-GGUF:Q4_K_M", rows[0].ModelName);
        AssertEx.Equal("org/b-GGUF:Q4_K_M", rows[1].ModelName);
        AssertEx.Equal("org/c-GGUF:Q4_K_M", rows[2].ModelName);
    }

    [Test]
    public async Task Advisor_Recommend_OuterCancellationCancelsTheRun()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        using var cts = new CancellationTokenSource();

        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([Summary("org/slow-GGUF")]));
        // The repo inspect honors the (linked) token; cancelling the outer token while it waits must cancel the whole run.
        discovery.InspectRepoAsync("org/slow-GGUF", Arg.Any<CancellationToken>())
                 .Returns(async call =>
                 {
                     // ReSharper disable once AccessToDisposedClosure
                     await cts.CancelAsync();
                     await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                     return Detail("org/slow-GGUF", File("Q4_K_M", paramCount: 1_000_000_000L));
                 });

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(64 * Gb));

        await AssertEx.ThrowsAsync<OperationCanceledException>(() =>
            advisor.RefreshAsync(Request(), reportProgress: null, cts.Token));

        // The snapshot must be recorded Cancelled (not Failed/Succeeded) by the outer OperationCanceledException catch.
        var snapshot = snapshotStore.Snapshots.Values.Single();
        AssertEx.Equal(ModelFitRunStatus.Cancelled, snapshot.Status);
    }

    [Test]
    public async Task Advisor_DownloadThenStart_CallsStoreThenSupervisor()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        var store = Substitute.For<IGgufModelStore>();
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();

        var handle = new GgufModelHandle("org/tiny-GGUF:Q4_K_M", "/models/tiny.gguf", "Q4_K_M", 1 * Gb, Sha256: null, "main", GgufRole.Chat);
        store.EnsureModelAsync(Arg.Any<GgufModelRequest>(), Arg.Any<IProgress<PullProgress>?>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(handle));
        supervisor.EnsureRunningAsync("org/tiny-GGUF:Q4_K_M", ModelRole.Chat, Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(new LlamaServerEndpoint("org/tiny-GGUF:Q4_K_M", ModelRole.Chat, new Uri("http://127.0.0.1:8081/v1"))));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(64 * Gb), store, supervisor);

        var request = new GgufModelRequest
        {
            RepoId = "org/tiny-GGUF",
            Quant = "Q4_K_M"
        };
        var downloaded = await advisor.DownloadAsync(request, progress: null, CancellationToken.None);
        var endpoint = await advisor.StartAsync(downloaded.ModelName, ModelRole.Chat, CancellationToken.None);

        await store.Received(1).EnsureModelAsync(Arg.Is<GgufModelRequest>(r => r.RepoId == "org/tiny-GGUF" && r.Quant == "Q4_K_M"),
            Arg.Any<IProgress<PullProgress>?>(), Arg.Any<CancellationToken>());
        await supervisor.Received(1).EnsureRunningAsync("org/tiny-GGUF:Q4_K_M", ModelRole.Chat, Arg.Any<CancellationToken>());
        AssertEx.NotNull(endpoint);
    }

    [Test]
    public async Task Advisor_Refresh_Benchmark_FailsBeforeSnapshot()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var advisor = BuildAdvisor(snapshotStore, new InMemoryModelFitRecommendationStore(),
            Substitute.For<IHuggingFaceGgufDiscovery>(), GpuProfile(64 * Gb));

        var result = await advisor.RefreshAsync(new ModelFitRefreshRequest(ModelFitOperation.Benchmark, "coding", Limit: 5),
            reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Failed, result.Status);
        AssertEx.Null(result.SnapshotId);
        AssertEx.Empty(snapshotStore.Snapshots.Values);
    }

    private static ModelFitRefreshRequest Request(string? quantOverride = null)
    {
        return new ModelFitRefreshRequest(ModelFitOperation.Recommend, "coding", Limit: 5, quantOverride);
    }

    private static ModelFitRefreshService BuildAdvisor(InMemoryModelFitSnapshotStore snapshotStore,
        InMemoryModelFitRecommendationStore recommendationStore,
        IHuggingFaceGgufDiscovery discovery,
        HardwareProfile profile,
        IGgufModelStore? store = null,
        ILlamaServerProcessSupervisor? supervisor = null)
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(profile));

        var registry = Substitute.For<IGgufModelRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<GgufModelRegistryEntry>>([]));

        var securityOptions = Options.Create(new SecurityOptions
        {
            AllowedModelNamePattern = "^[a-zA-Z0-9._:/-]+$"
        });

        return new ModelFitRefreshService(profiler,
            discovery,
            new MemoryFitEstimator(),
            store ?? Substitute.For<IGgufModelStore>(),
            registry,
            supervisor ?? Substitute.For<ILlamaServerProcessSupervisor>(),
            new ModelFitRequestValidator(new ModelNameValidator(securityOptions)),
            snapshotStore,
            recommendationStore,
            TimeProvider.System,
            NullLogger<ModelFitRefreshService>.Instance);
    }

    private static GgufRepoSummary Summary(string repoId)
    {
        return new GgufRepoSummary(repoId, IsGated: false, Downloads: 1000, Likes: 10, DateTimeOffset.UnixEpoch, "mit", HasUsableGguf: true);
    }

    private static GgufRepoDetail Detail(string repoId, params GgufRepoFile[] files)
    {
        return new GgufRepoDetail(repoId, IsGated: false, "mit", files);
    }

    private static GgufRepoFile File(string quant, long paramCount)
    {
        // A small, fits-anywhere geometry (4 layers, 2 kv-heads, embedding 16 over 4 heads) so only param-count drives weights.
        return new GgufRepoFile($"model.{quant}.gguf",
            quant,
            1 * Gb,
            Sha256: null,
            "main",
            "llama",
            quant,
            paramCount,
            BlockCount: 4,
            AttentionHeadCount: 4,
            AttentionHeadCountKV: 2,
            EmbeddingLength: 16,
            ContextLength: 8192);
    }

    private static HardwareProfile GpuProfile(long vramBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = vramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }
}
