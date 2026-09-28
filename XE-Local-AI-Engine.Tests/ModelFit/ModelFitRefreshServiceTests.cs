namespace XE_Local_AI_Engine.Tests.ModelFit;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Validation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
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
[Category(TestCategories.Unit)]
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
    public async Task Advisor_Recommend_ScoresAgainstFreeVram_NotTotalVram()
    {
        // The advisor's score is estimated / fit-budget, and the fit budget is free VRAM when the probe measured it. A
        // 16 GiB card with 8 GiB already resident has half the room, so the same model must score twice as high — it
        // consumes twice the share of what is actually available. This score was derived from a second, inline copy of
        // the budget expression that still read TOTAL VRAM, so it disagreed with the fit verdicts beside it.
        var totalOnly = await ScoreForProfileAsync(GpuProfile(16 * Gb));
        var withFreeReading = await ScoreForProfileAsync(GpuProfile(16 * Gb, availableVramBytes: 8 * Gb));

        AssertEx.True(withFreeReading > totalOnly,
            "a card with half its VRAM already resident must score the same model higher, not identically.");
        AssertEx.True(Math.Abs(withFreeReading - (2 * totalOnly)) <= 0.2d,
            $"halving the budget must double the score (total-only {totalOnly}, free {withFreeReading}).");
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
                     new GgufRepoFile
                     {
                         FileName = "model.gguf",
                         Quant = "Q4_K_M",
                         SizeBytes = 0,
                         Sha256 = null,
                         Revision = "main",
                         Architecture = null,
                         QuantType = null,
                         ParamCount = null,
                         BlockCount = null,
                         AttentionHeadCount = null,
                         AttentionHeadCountKV = null,
                         EmbeddingLength = null,
                         ContextLength = null
                     })));

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
    public async Task Advisor_Recommend_RanksMostCapableThatFitsFirst()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();

        // Two repos that both fit a roomy 64 GB budget: a 1B and a 14B. The advisor must lead with the MORE capable
        // (14B) model — the old "largest leftover VRAM" order would have surfaced the 1B first and truncated the 14B.
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([
                     Summary("org/small-GGUF"),
                     Summary("org/big-GGUF")
                 ]));
        discovery.InspectRepoAsync("org/small-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/small-GGUF", File("Q4_K_M", paramCount: 1_000_000_000L))));
        discovery.InspectRepoAsync("org/big-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/big-GGUF", File("Q4_K_M", paramCount: 14_000_000_000L))));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(64 * Gb));

        var result = await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        var rows = recommendationStore.RowsFor(snapshotStore.Snapshots.Values.Single().Id);
        AssertEx.Equal("org/big-GGUF:Q4_K_M", rows[0].ModelName);
        AssertEx.Equal("org/small-GGUF:Q4_K_M", rows[1].ModelName);
    }

    [Test]
    public async Task Advisor_Recommend_MergesUseCaseSearchTerms_AndDedupes()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();

        // Use-case "coding" (the Request() default) maps to terms ["coder", "code"]. Each term's search returns a
        // distinct repo plus a SHARED one — proving the per-term results are merged AND de-duped by repo id.
        discovery.SearchAsync(Arg.Is<GgufSearchQuery>(q => q.SearchText == "coder"), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([Summary("org/coder-GGUF"), Summary("org/shared-GGUF")]));
        discovery.SearchAsync(Arg.Is<GgufSearchQuery>(q => q.SearchText == "code"), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([Summary("org/code-GGUF"), Summary("org/shared-GGUF")]));

        foreach (var repoId in new[]
                 {
                     "org/coder-GGUF",
                     "org/code-GGUF",
                     "org/shared-GGUF"
                 })
        {
            discovery.InspectRepoAsync(repoId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(Detail(repoId, File("Q4_K_M", paramCount: 1_000_000_000L))));
        }

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(64 * Gb));

        var result = await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        var rows = recommendationStore.RowsFor(snapshotStore.Snapshots.Values.Single().Id);
        var names = rows.Select(static row => row.ModelName).ToHashSet(StringComparer.Ordinal);
        AssertEx.True(names.Contains("org/coder-GGUF:Q4_K_M"), "the 'coder' term's repo must be present.");
        AssertEx.True(names.Contains("org/code-GGUF:Q4_K_M"), "the 'code' term's repo must be present.");
        AssertEx.True(names.Contains("org/shared-GGUF:Q4_K_M"), "the shared repo must be present.");
        // De-duped: the shared repo appears once across the two term searches, so exactly three rows.
        AssertEx.Equal(expected: 3, rows.Count);
    }

    [Test]
    public async Task Advisor_Recommend_TrustedPublisherWinsCapabilityTie()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();

        // Identical capability (same param count → same estimate) and identical downloads. The untrusted repo sorts
        // FIRST by repo-id ordinal ("aaa" < "unsloth"), so a pass proves the trusted-publisher nudge beats the id tie-break.
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([
                     Summary("aaa/model-GGUF"),
                     Summary("unsloth/model-GGUF")
                 ]));
        discovery.InspectRepoAsync("aaa/model-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("aaa/model-GGUF", File("Q4_K_M", paramCount: 3_000_000_000L))));
        discovery.InspectRepoAsync("unsloth/model-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("unsloth/model-GGUF", File("Q4_K_M", paramCount: 3_000_000_000L))));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(64 * Gb));

        await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        var rows = recommendationStore.RowsFor(snapshotStore.Snapshots.Values.Single().Id);
        AssertEx.Equal("unsloth/model-GGUF:Q4_K_M", rows[0].ModelName);
        AssertEx.Equal("aaa/model-GGUF:Q4_K_M", rows[1].ModelName);
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

        var handle = new GgufModelHandle
        {
            ModelName = "org/tiny-GGUF:Q4_K_M",
            LocalPath = "/models/tiny.gguf",
            Quant = "Q4_K_M",
            SizeBytes = 1 * Gb,
            Sha256 = null,
            SourceRevision = "main",
            Role = GgufRole.Chat
        };
        store.EnsureModelAsync(Arg.Any<GgufModelRequest>(), Arg.Any<IProgress<PullProgress>?>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult(handle));
        supervisor.EnsureRunningAsync("org/tiny-GGUF:Q4_K_M", ModelRole.Chat, Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(new LlamaServerEndpoint
                  {
                      ModelName = "org/tiny-GGUF:Q4_K_M",
                      Role = ModelRole.Chat,
                      BaseAddress = new Uri("http://127.0.0.1:8081/v1")
                  }));

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

        var result = await advisor.RefreshAsync(new ModelFitRefreshRequest
            {
                Operation = ModelFitOperation.Benchmark,
                UseCase = "coding",
                Limit = 5
            },
            reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Failed, result.Status);
        AssertEx.Null(result.SnapshotId);
        AssertEx.Empty(snapshotStore.Snapshots.Values);
    }

    [Test]
    public async Task Advisor_Recommend_StepsDownQuantLadder_WhenDefaultQuantDoesNotFit()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();

        // A 14B repo whose Q4_K_M does NOT fit an 8 GiB budget but whose Q3_K_M does. The advisor must keep the model at
        // the largest quant that fits (Q3_K_M) instead of dropping the whole repo — the keystone of the fix.
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([Summary("org/big-GGUF")]));
        discovery.InspectRepoAsync("org/big-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/big-GGUF",
                     File("Q4_K_M", paramCount: 14_000_000_000L),
                     File("Q3_K_M", paramCount: 14_000_000_000L))));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(8 * Gb));

        var result = await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        AssertEx.Equal(expected: 1, result.RecommendationCount);
        var rows = recommendationStore.RowsFor(snapshotStore.Snapshots.Values.Single().Id);
        AssertEx.Equal("org/big-GGUF:Q3_K_M", rows.Single().ModelName);
        AssertEx.Equal("Q3_K_M", rows.Single().Quantization);
    }

    [Test]
    public async Task Advisor_Recommend_DropsRepo_WhenOnlySubFloorQuantsFit()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();

        // A 14B repo where Q4_K_M does not fit 8 GiB and the only fitting file (Q2_K) is BELOW the Q3_K_M quality floor.
        // The model must be dropped rather than recommended at an unusably-degraded quant.
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([Summary("org/big-GGUF")]));
        discovery.InspectRepoAsync("org/big-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/big-GGUF",
                     File("Q4_K_M", paramCount: 14_000_000_000L),
                     File("Q2_K", paramCount: 14_000_000_000L))));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(8 * Gb));

        var result = await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        AssertEx.Equal(expected: 0, result.RecommendationCount);
    }

    [Test]
    public async Task Advisor_Recommend_NewerModelBoostsAboveOlderPeer_AtEqualCapability()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();

        // Two equal-capability (same param count → same tier), equal-download repos. The newer (later last-modified) one
        // must rank first even though its repo id ("zzz") loses the ordinal tie-break to "aaa" — proving the recency boost.
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([
                     Summary("aaa/old-GGUF", downloads: 5000, lastModified: DateTimeOffset.UnixEpoch),
                     Summary("zzz/new-GGUF", downloads: 5000, lastModified: DateTimeOffset.UnixEpoch.AddYears(5))
                 ]));
        discovery.InspectRepoAsync("aaa/old-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("aaa/old-GGUF", File("Q4_K_M", paramCount: 3_000_000_000L))));
        discovery.InspectRepoAsync("zzz/new-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("zzz/new-GGUF", File("Q4_K_M", paramCount: 3_000_000_000L))));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(64 * Gb));

        await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        var rows = recommendationStore.RowsFor(snapshotStore.Snapshots.Values.Single().Id);
        AssertEx.Equal("zzz/new-GGUF:Q4_K_M", rows[0].ModelName);
        AssertEx.Equal("aaa/old-GGUF:Q4_K_M", rows[1].ModelName);
    }

    [Test]
    public async Task Advisor_Recommend_EmitsReleaseDateAndTrustSignalsToSnapshotJson()
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();

        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([
                     Summary("unsloth/model-GGUF", lastModified: DateTimeOffset.UnixEpoch.AddYears(56))
                 ]));
        discovery.InspectRepoAsync("unsloth/model-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("unsloth/model-GGUF", File("Q4_K_M", paramCount: 1_000_000_000L))));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(64 * Gb));

        await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        // The advisor JSON the parser consumes must carry the recency + trust boosts per model.
        var rawJson = snapshotStore.Snapshots.Values.Single().RawJson;
        AssertEx.NotNull(rawJson);
        AssertEx.Contains(rawJson!, "release_date");
        AssertEx.Contains(rawJson!, "is_trusted_publisher");
    }

    [Test]
    public async Task Advisor_Recommend_KnowledgeCompanionReserve_ShrinksTheRecommendationBudgetOnly()
    {
        // A ~1.4 GiB model fits a 12 GiB card, but not the 1 GiB left once an 11 GiB companion reserve is taken off. The catalog lane sees the
        // same reduced profile, and the advisor's system block is byte-identical to the unreserved run: only recommendations shrink.
        var unreserved = await RefreshWithCompanionReserveAsync(companionReserveBytes: 0);
        var reserved = await RefreshWithCompanionReserveAsync(companionReserveBytes: 11 * Gb);

        AssertEx.Equal(expected: 1, unreserved.Rows);
        AssertEx.Equal(expected: 0, reserved.Rows);
        AssertEx.Equal(12 * Gb, unreserved.CatalogProfile.VramBytes);
        AssertEx.Equal(1 * Gb, reserved.CatalogProfile.VramBytes);
        AssertEx.Equal(1 * Gb, reserved.CatalogProfile.AvailableVramBytes);
        AssertEx.Equal(AssertEx.NotNull(unreserved.DiagnosticsJson), reserved.DiagnosticsJson);
    }

    [Test]
    public async Task Advisor_Recommend_SizesAgainstAForcedProfileRefresh_AndHandsThatProfileToTheCompanionReserve()
    {
        // The reserve skips resident companions only against measured free VRAM, so a cached figure predating a companion's spawn would count it nowhere.
        var profile = GpuProfile(12 * Gb, availableVramBytes: 10 * Gb);
        var runtimeAudit = Substitute.For<IRuntimeDeviceAudit>();
        runtimeAudit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(profile));
        var companionReserve = Substitute.For<IKnowledgeCompanionReserve>();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([]));

        var advisor = BuildAdvisor(new InMemoryModelFitSnapshotStore(),
            new InMemoryModelFitRecommendationStore(),
            discovery,
            profile,
            companionReserve: companionReserve,
            runtimeAudit: runtimeAudit);
        await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        await runtimeAudit.Received(1).GetEffectiveProfileAsync(forceRefreshProfile: true, Arg.Any<CancellationToken>());
        await runtimeAudit.DidNotReceive().GetEffectiveProfileAsync(forceRefreshProfile: false, Arg.Any<CancellationToken>());
        await companionReserve.Received(1).ResolveGpuBytesAsync(profile, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Advisor_Recommend_KnowledgeCompanionReserveBeyondTheCard_StaysInGpuMode()
    {
        var reserved = await RefreshWithCompanionReserveAsync(companionReserveBytes: 64 * Gb);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, reserved.Status);
        AssertEx.Equal(1L, reserved.CatalogProfile.VramBytes);
        AssertEx.Equal(1L, reserved.CatalogProfile.AvailableVramBytes);
        AssertEx.True(reserved.CatalogProfile is { GpuAccelAvailable: true, VramKnown: true },
            "The clamp keeps a one-byte GPU budget; flipping to CPU mode would recommend RAM-sized models instead.");
        AssertEx.Equal(expected: 0, reserved.Rows);
    }

    private static async Task<CompanionReserveRun> RefreshWithCompanionReserveAsync(long companionReserveBytes)
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([Summary("org/tiny-GGUF")]));
        discovery.InspectRepoAsync("org/tiny-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/tiny-GGUF", File("Q4_K_M", paramCount: 1_000_000_000L))));
        var companionReserve = Substitute.For<IKnowledgeCompanionReserve>();
        companionReserve.ResolveGpuBytesAsync(Arg.Any<HardwareProfile>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(companionReserveBytes));

        HardwareProfile? catalogProfile = null;
        var empty = new EmptyCatalogRecommendationService();
        var catalog = Substitute.For<ICatalogRecommendationService>();
        catalog.BuildRecommendationsAsync(Arg.Any<string?>(),
                   Arg.Any<string>(),
                   Arg.Any<int>(),
                   Arg.Any<HardwareProfile>(),
                   Arg.Any<IReadOnlySet<string>>(),
                   Arg.Any<CancellationToken>())
               .Returns(call =>
               {
                   catalogProfile = call.ArgAt<HardwareProfile>(3);
                   return empty.BuildRecommendationsAsync(call.ArgAt<string?>(0),
                       call.ArgAt<string>(1),
                       call.ArgAt<int>(2),
                       catalogProfile,
                       call.ArgAt<IReadOnlySet<string>>(4),
                       call.ArgAt<CancellationToken>(5));
               });

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, GpuProfile(12 * Gb, availableVramBytes: 12 * Gb), catalog: catalog, companionReserve: companionReserve);
        var result = await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        var snapshot = snapshotStore.Snapshots.Values.Single();
        return new CompanionReserveRun
        {
            Status = result.Status,
            Rows = recommendationStore.RowsFor(snapshot.Id).Count,
            CatalogProfile = AssertEx.NotNull(catalogProfile),
            DiagnosticsJson = snapshot.DiagnosticsJson
        };
    }

    private sealed class CompanionReserveRun
    {
        public required ModelFitRunStatus Status { get; init; }

        public required int Rows { get; init; }

        public required HardwareProfile CatalogProfile { get; init; }

        public required string? DiagnosticsJson { get; init; }
    }

    // Runs one recommend refresh for a single fitting repo and returns the persisted row's score.
    [Test]
    public async Task Advisor_Recommend_DropsExploreRowTheCatalogLaneAlreadyRecommends()
    {
        // Live shape: the catalog lane recommended unsloth/Qwen3-Coder-30B-A3B at rank 2 and the explore lane found the same repo again at rank 9.
        const string sharedRepo = "unsloth/Qwen3-Coder-30B-A3B-Instruct-GGUF";
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([
                     Summary(sharedRepo),
                     Summary("org/other-GGUF")
                 ]));
        discovery.InspectRepoAsync(sharedRepo, Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail(sharedRepo, File("Q4_K_M", paramCount: 1_000_000_000L))));
        discovery.InspectRepoAsync("org/other-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/other-GGUF", File("Q4_K_M", paramCount: 1_000_000_000L))));
        var profile = GpuProfile(12 * Gb);
        var catalog = await CatalogRecommending(sharedRepo, profile);

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, profile, catalog: catalog);

        var result = await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        var rows = recommendationStore.RowsFor(snapshotStore.Snapshots.Values.Single().Id);
        AssertEx.ContainsSingle(rows, row => row.ModelName == GgufModelName.Format(sharedRepo, "Q4_K_M"));
        AssertEx.ContainsSingle(rows, row => row.ModelName == "org/other-GGUF:Q4_K_M");
        AssertEx.True(rows.Select(row => row.Rank).SequenceEqual(Enumerable.Range(1, rows.Count)), "ranks must stay contiguous after the duplicate is dropped.");
    }

    private static async Task<ICatalogRecommendationService> CatalogRecommending(string repoId, HardwareProfile profile)
    {
        var file = File("Q4_K_M", paramCount: 1_000_000_000L);
        var selected = GgufFileSelector.SelectBestFit(new MemoryFitEstimator(), [file], "Q4_K_M", ctxTarget: 8192, profile)!;
        var empty = await new EmptyCatalogRecommendationService()
            .BuildRecommendationsAsync(useCase: null, "Q4_K_M", ctxTarget: 8192, profile, new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);
        var candidate = new CatalogRecommendationCandidate
        {
            Entry = new ModelCatalogEntry("qwen3-coder-30b-a3b",
                "qwen3",
                "Qwen3 Coder 30B A3B",
                "unsloth",
                repoId,
                "apache-2.0",
                "flagship",
                ["coding"],
                TotalParamsB: 30,
                ActiveParamsB: 3,
                Moe: true,
                ContextLength: 8192,
                MinLlamaCppTag: "b10201",
                ReleaseDate: "2026-01-01",
                Notes: null),
            File = selected.File,
            Estimate = selected.Estimate,
            ModelName = GgufModelName.Format(repoId, "Q4_K_M"),
            IsInstalled = false
        };
        var catalog = Substitute.For<ICatalogRecommendationService>();
        catalog.BuildRecommendationsAsync(Arg.Any<string?>(),
                   Arg.Any<string>(),
                   Arg.Any<int>(),
                   Arg.Any<HardwareProfile>(),
                   Arg.Any<IReadOnlySet<string>>(),
                   Arg.Any<CancellationToken>())
               .Returns(Task.FromResult(new CatalogRecommendationResult
               {
                   Recommended = [candidate],
                   CanRun = [],
                   CatalogSnapshot = empty.CatalogSnapshot
               }));
        return catalog;
    }

    private static async Task<double> ScoreForProfileAsync(HardwareProfile profile)
    {
        var snapshotStore = new InMemoryModelFitSnapshotStore();
        var recommendationStore = new InMemoryModelFitRecommendationStore();
        var discovery = Substitute.For<IHuggingFaceGgufDiscovery>();
        discovery.SearchAsync(Arg.Any<GgufSearchQuery>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult<IReadOnlyList<GgufRepoSummary>>([Summary("org/tiny-GGUF")]));
        discovery.InspectRepoAsync("org/tiny-GGUF", Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(Detail("org/tiny-GGUF", File("Q4_K_M", paramCount: 1_000_000_000L))));

        var advisor = BuildAdvisor(snapshotStore, recommendationStore, discovery, profile);
        var result = await advisor.RefreshAsync(Request(), reportProgress: null, CancellationToken.None);

        AssertEx.Equal(ModelFitRunStatus.Succeeded, result.Status);
        var rows = recommendationStore.RowsFor(snapshotStore.Snapshots.Values.Single().Id);
        AssertEx.Equal(expected: 1, rows.Count, "the model must still fit — a dropped row would make the score vacuous.");
        return rows[0].Score;
    }

    private static ModelFitRefreshRequest Request(string? quantOverride = null)
    {
        return new ModelFitRefreshRequest
        {
            Operation = ModelFitOperation.Recommend,
            UseCase = "coding",
            Limit = 5,
            QuantOverride = quantOverride
        };
    }

    private static ModelFitRefreshService BuildAdvisor(InMemoryModelFitSnapshotStore snapshotStore,
        InMemoryModelFitRecommendationStore recommendationStore,
        IHuggingFaceGgufDiscovery discovery,
        HardwareProfile profile,
        IGgufModelStore? store = null,
        ILlamaServerProcessSupervisor? supervisor = null,
        ICatalogRecommendationService? catalog = null,
        IKnowledgeCompanionReserve? companionReserve = null,
        IRuntimeDeviceAudit? runtimeAudit = null)
    {
        if (runtimeAudit is null)
        {
            runtimeAudit = Substitute.For<IRuntimeDeviceAudit>();
            runtimeAudit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(profile));
        }

        var registry = Substitute.For<IGgufModelRegistry>();
        registry.ListAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<GgufModelRegistryEntry>>([]));

        var securityOptions = Options.Create(new SecurityOptions
        {
            AllowedModelNamePattern = "^[a-zA-Z0-9._:/-]+$"
        });

        return new ModelFitRefreshService(runtimeAudit,
            discovery,
            new MemoryFitEstimator(),
            store ?? Substitute.For<IGgufModelStore>(),
            registry,
            supervisor ?? Substitute.For<ILlamaServerProcessSupervisor>(),
            new ModelFitRequestValidator(new ModelNameValidator(securityOptions)),
            snapshotStore,
            recommendationStore,
            catalog ?? new EmptyCatalogRecommendationService(),
            companionReserve ?? Substitute.For<IKnowledgeCompanionReserve>(),
            TimeProvider.System,
            NullLogger<ModelFitRefreshService>.Instance);
    }

    private static GgufRepoSummary Summary(string repoId, long downloads = 1000, DateTimeOffset? lastModified = null)
    {
        return new GgufRepoSummary
        {
            RepoId = repoId,
            IsGated = false,
            Downloads = downloads,
            Likes = 10,
            LastModified = lastModified ?? DateTimeOffset.UnixEpoch,
            License = "mit",
            HasUsableGguf = true,
            IsTrustedPublisher = GgufPublisherTrust.IsTrustedPublisher(repoId)
        };
    }

    private static GgufRepoDetail Detail(string repoId, params GgufRepoFile[] files)
    {
        return new GgufRepoDetail
        {
            RepoId = repoId,
            IsGated = false,
            License = "mit",
            Files = files
        };
    }

    private static GgufRepoFile File(string quant, long paramCount)
    {
        // A small, fits-anywhere geometry (4 layers, 2 kv-heads, embedding 16 over 4 heads) so only param-count drives weights.
        return new GgufRepoFile
        {
            FileName = $"model.{quant}.gguf",
            Quant = quant,
            SizeBytes = 1 * Gb,
            Sha256 = null,
            Revision = "main",
            Architecture = "llama",
            QuantType = quant,
            ParamCount = paramCount,
            BlockCount = 4,
            AttentionHeadCount = 4,
            AttentionHeadCountKV = 2,
            EmbeddingLength = 16,
            ContextLength = 8192
        };
    }

    private static HardwareProfile GpuProfile(long vramBytes, long? availableVramBytes = null)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = vramBytes,
            AvailableVramBytes = availableVramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }
}
