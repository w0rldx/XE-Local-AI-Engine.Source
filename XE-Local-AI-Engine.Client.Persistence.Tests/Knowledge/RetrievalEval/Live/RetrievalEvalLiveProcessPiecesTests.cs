namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     The model-free pieces of the live eval that reach its launcher, endpoint supervisor or report writer, which bind
///     loopback ports or start processes: launch mirror, alias guard, context cap, lazy spawn, report shape.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class RetrievalEvalLiveProcessPiecesTests
{
    [Test]
    public void Report_SerializesEveryRequiredBlock_AndRendersTheConfigTable()
    {
        var evaluation = new QueryEvaluation
        {
            QueryId = "q1",
            RelevantRetrieved = true,
            FirstRelevantRank = 2,
            ReciprocalRank = 0.5,
            CitationCoverage = 1,
            RetrievedRelevantCount = 1,
            RelevantDocumentCount = 1,
            PrecisionAtK = 0.2,
            NdcgAtK = 0.63,
            ElapsedMilliseconds = 12
        };
        var noAnswer = new QueryEvaluation
        {
            QueryId = "q2",
            RelevantRetrieved = false,
            FirstRelevantRank = 0,
            ReciprocalRank = 0,
            CitationCoverage = 0,
            ExpectsNoAnswer = true,
            NoAnswerCorrect = false,
            ElapsedMilliseconds = 30
        };
        var summary = LiveMetricSummary.From([evaluation, noAnswer]);
        AssertEx.Equal(0.5, summary.MeanReciprocalRank);
        AssertEx.Equal(1, summary.AnswerableCount);
        AssertEx.Equal(1, summary.NoAnswerCount);

        var report = new RetrievalEvalLiveReport
        {
            Completed = false,
            AbortReason = "HttpRequestException: boom",
            EmbeddingVectors = "resolved 'nomic' (NOT confident: no vector policy applies); dim 768 identity x",
            Environment = new LiveEnvironment
            {
                CapturedAtUtc = "2026-09-27 00:00:00Z",
                Gpu = "gpu",
                Cpu = "cpu",
                RamTotal = "1 kB",
                LlamaServerVersion = "version: 1",
                LlamaServerPath = "/bin/llama-server",
                GpuLayers = 99,
                Models =
                [
                    new LiveModelFile
                    {
                        Role = "embedding",
                        FileName = "e.gguf",
                        SizeBytes = 1,
                        Sha256 = "ab"
                    }
                ],
                CorpusDirectory = "/corpus",
                CorpusFileCount = 2,
                CorpusContentSha256 = "cafe",
                GitDirty = true,
                GitHead = "abc",
                K = 5
            },
            Configs =
            [
                new LiveConfigResult
                {
                    Id = "bge20",
                    Description = "d",
                    Valid = false,
                    InvalidReason = "forced rerank degraded",
                    Overall = summary,
                    ByCategory = new Dictionary<string, LiveMetricSummary> { ["en-prose"] = summary, ["chunk-boundary"] = summary, ["code-path"] = summary },
                    ByLanguage = new Dictionary<string, LiveMetricSummary> { ["en"] = summary },
                    EnglishOnly = LiveMetricSummary.From([noAnswer]),
                    BoundaryBothHalves = 1,
                    BoundaryPairCount = 2,
                    EndToEnd = LiveLatency.From([12d, 30d]),
                    RerankStage = LiveLatency.From([]),
                    Degrades = new Dictionary<string, int> { ["timeout"] = 1, ["cold"] = 4, ["busy"] = 2 },
                    BudgetCancelled = 3,
                    BacklogDrainMilliseconds = 10400,
                    PerQuery =
                    [
                        Score("a1", answerable: true, 5d, 1d, "Rerank", top1Relevant: true),
                        Score("a2", answerable: true, 3d, 2.5d, "Rerank", top1Relevant: true),
                        Score("a3", answerable: true, 0.5d, 0.4d, "Rerank", top1Relevant: false),
                        Score("n1", answerable: false, 1d, 0.5d, "Rerank", top1Relevant: false),
                        Score("f1", answerable: true, 0.04d, null, "Fusion", top1Relevant: true),
                        Score("e1", answerable: false, null, null, null, top1Relevant: false)
                    ]
                }
            ],
            Sanity = [new LiveSanityResult { RerankerId = "bge", Passed = true, PairScores = ["pair"] }],
            NegativeControls = [new LiveControlResult { Name = "shuffled scores below F0", Passed = true, Detail = "detail" }],
            Contention = new LiveContentionResult { SkippedReason = "no headroom" }
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report));
        foreach (var block in (string[])["Environment", "Configs", "Sanity", "NegativeControls", "Contention"])
        {
            AssertEx.True(json.RootElement.TryGetProperty(block, out _), $"report JSON lacks '{block}'.");
        }

        var config = json.RootElement.GetProperty("Configs")[0];
        AssertEx.False(config.GetProperty("Valid").GetBoolean());
        AssertEx.Equal(30d, config.GetProperty("EndToEnd").GetProperty("Max").GetDouble());
        AssertEx.Equal(3, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        AssertEx.Equal(2, config.GetProperty("BusyFallbacks").GetInt32());
        var perQuery = config.GetProperty("PerQuery");
        AssertEx.Equal(6, perQuery.GetArrayLength());
        AssertEx.Equal("Rerank", perQuery[0].GetProperty("Top1ScoreKind").GetString());
        AssertEx.Equal(4d, perQuery[0].GetProperty("Margin").GetDouble());
        AssertEx.True(perQuery[0].GetProperty("Top1Relevant").GetBoolean());

        var markdown = RetrievalEvalLiveReportWriter.RenderMarkdown(report);
        AssertEx.True(markdown.Contains("| bge20 | INVALID: forced rerank degraded |", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("| 4/2 | 3 |", StringComparison.Ordinal), "cold/busy column: " + markdown);
        AssertEx.True(markdown.Contains("PROD-cold = every query before any warm-up", StringComparison.Ordinal), markdown);

        // Rerank: answerable top-1 {5, 3, 0.5}, margins {4, 0.5, 0.1}; the fusion group never mixes into it.
        AssertEx.True(markdown.Contains("| bge20 | Rerank | answerable | 3 | 0.5000/0.5000/3.0000/5.0000/5.0000 | 0.1000/0.1000/0.5000/4.0000/4.0000 |", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("| bge20 | Rerank | no-answer | 1 | 1.0000/1.0000/1.0000/1.0000/1.0000 |", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("| bge20 | Fusion | answerable | 1 | 0.0400/0.0400/0.0400/0.0400/0.0400 | - |", StringComparison.Ordinal), markdown);

        // Rerank threshold: no-answer {1} vs relevant top-1 {5, 3} (a3 excluded) separates at t = 3; Fusion has no no-answer query.
        AssertEx.True(markdown.Contains("| bge20 | Rerank | 3.0000 | 1.000 | 1.000 (n=1) | 1.000 (n=2) |", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("| bge20 | Fusion | n/a (needs no-answer", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("Skipped: no headroom", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("- Embedding: resolved 'nomic' (NOT confident", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("**ABORTED:** HttpRequestException: boom", StringComparison.Ordinal) && markdown.Contains("(dirty)", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("| n/a |", StringComparison.Ordinal), "a no-answer-free group must print n/a, not 0.000: " + markdown);
        AssertEx.True(markdown.Contains("en-prose", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("both halves 1/2", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("- bge20: backlog drain 10400.0 ms (3 budget-cancelled)", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("| code-path * |", StringComparison.Ordinal) && markdown.Contains("not a quality signal", StringComparison.Ordinal), markdown);
        AssertEx.True(markdown.Contains("## EN-only slice", StringComparison.Ordinal) && markdown.Contains("| bge20 | 1 | n/a |", StringComparison.Ordinal), markdown);
    }

    [Test]
    [Arguments(ModelRole.Embedding, 99)]
    [Arguments(ModelRole.Reranker, 99)]
    [Arguments(ModelRole.Embedding, 0)]
    [Arguments(ModelRole.Reranker, 0)]
    public void BuildArguments_ParseBackToTheProductProjection_ExceptTheExplicitGpuLayers(ModelRole role, int gpuLayers)
    {
        var variant = gpuLayers > 0 ? GpuVariant.Cuda : GpuVariant.Cpu;
        var policy = new LlamaServerLaunchPolicyOptions();
        var plan = new LlamaServerLaunchPlan(1792,
            UseKvCacheQuantization: variant != GpuVariant.Cpu && policy.EnableGpuKvCacheQuantization,
            policy.KvCacheType,
            variant == GpuVariant.Cpu ? LiveLlamaServer.ProductProjection(role, 0, 1792).Threads : null,
            variant == GpuVariant.Cpu ? LiveLlamaServer.ProductProjection(role, 0, 1792).ThreadsBatch : null);
        var product = LlamaServerLaunchProjection.From(variant, ResolvedLaunchArguments.Explore(), plan, role);
        var arguments = LiveLlamaServer.BuildArguments(role, "/m.gguf", 1234, "xe-eval-test-1234", gpuLayers, contextTokens: 1792);

        var parsed = AssertEx.NotNull(LlamaServerLaunchProjection.TryFromArguments(arguments), string.Join(' ', arguments));

        // Deliberate differences outside the projection: explicit -ngl, --device none on the CPU pass, and the guard's
        // --alias (TryFromArguments ignores flags it does not allow-list).
        AssertEx.Equal(gpuLayers, parsed.GpuLayers);
        AssertEx.True(arguments.Take(4).SequenceEqual(["-m", "/m.gguf", "--alias", "xe-eval-test-1234"]), string.Join(' ', arguments));
        AssertEx.Equal(product, parsed with
        {
            GpuLayers = null
        }, string.Join(' ', arguments));
        AssertEx.Equal(gpuLayers == 0, arguments.Contains("none"), "the CPU pass must hide the GPU with --device none, and only the CPU pass.");
    }

    [Test]
    [Arguments("""{"object":"list","data":[{"id":"xe-eval-a-1"}]}""", true)]
    [Arguments("""{"object":"list","data":[{"id":"nomic-embed-text-v1.5.f16.gguf"}]}""", false)]
    [Arguments("""{"object":"list","data":[{"id":"xe-eval-a-1"},{"id":"other"}]}""", false)]
    [Arguments("""{"object":"list","data":[]}""", false)]
    [Arguments("not json", false)]
    public void ListsOnlyAlias_AcceptsExactlyThisLaunchesAlias(string body, bool expected) =>
        AssertEx.Equal(expected, LiveLlamaServer.ListsOnlyAlias(body, "xe-eval-a-1"));

    [Test]
    public void AbstainThreshold_BreaksTiesToTheLowestThreshold_AndIsNullForAnEmptySide()
    {
        // No-answer {1, 3} vs relevant {2, 4}: t = 2 and t = 4 both reach 0.75, and the lowest t wins.
        var tie = AssertEx.NotNull(LiveAbstainThreshold.Find([1d, 3d], [2d, 4d]));
        AssertEx.Equal(2d, tie.Threshold);
        AssertEx.Equal(0.75d, tie.BalancedAccuracy);
        AssertEx.Equal(0.5d, tie.NoAnswerRecall);
        AssertEx.Equal(1d, tie.AnswerableRetention);

        AssertEx.Null(LiveAbstainThreshold.Find([], [1d]));
        AssertEx.Null(LiveAbstainThreshold.Find([1d], []));
    }

    [Test]
    public async Task LazyKey_ReportsNotRunning_WhileItsSpawnIsPendingOrFailed()
    {
        await using var supervisor = new LiveEndpointSupervisor();
        var spawn = new TaskCompletionSource<LiveLlamaServer>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.RegisterLazy("bge", ModelRole.Reranker, _ => spawn.Task, CancellationToken.None);

        AssertEx.Null(supervisor.TryAcquireInferenceLease("bge", ModelRole.Reranker).Lease);
        AssertEx.False(supervisor.LazyStarted("bge", ModelRole.Reranker));

        var warm = supervisor.WarmLazyAsync("bge", ModelRole.Reranker);
        AssertEx.True(supervisor.LazyStarted("bge", ModelRole.Reranker));
        AssertEx.Null(supervisor.TryAcquireInferenceLease("bge", ModelRole.Reranker).Lease);

        spawn.SetException(new LiveInfraException("port taken"));
        var (server, failure) = await warm;
        AssertEx.Null(server);
        AssertEx.Equal("port taken", failure);
        AssertEx.Null(supervisor.TryAcquireInferenceLease("bge", ModelRole.Reranker).Lease);
    }

    [Test]
    public async Task RealRerankerClient_OnAColdLazyKey_FallsBackAsColdWithoutStartingTheSpawn()
    {
        await using var supervisor = new LiveEndpointSupervisor();
        supervisor.RegisterLazy("bge", ModelRole.Reranker, static _ => throw new InvalidOperationException("a search must not spawn"), CancellationToken.None);
        using var http = new HttpClient();
        var degrades = new DegradeCapturingLogger();

        var scores = await new LlamaServerRerankerClient(supervisor, http, degrades).RerankAsync("bge", "q", ["a", "b"], CancellationToken.None);

        AssertEx.Null(scores);
        AssertEx.False(supervisor.LazyStarted("bge", ModelRole.Reranker), "a search started the reranker spawn.");
        AssertEx.Equal("cold", degrades.Degrades.Single().Reason);
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public void ColdRow_IsInvalid_OnlyWhenASearchStartedTheSpawn(bool spawnedBySearch, bool expectedValid)
    {
        var row = RetrievalEvalLiveTests.WithColdOutcome(Row("PROD-cold"), spawnedBySearch, warmed: null);

        AssertEx.Equal(expectedValid, row.Valid);
        AssertEx.Equal(spawnedBySearch ? "a search spawned the cold reranker (spawn charged to a search)" : null, row.InvalidReason);
    }

    [Test]
    public async Task Leases_AreCountedPerKey_AndReleaseOnlyWhenTheLastSettles()
    {
        await using var supervisor = new LiveEndpointSupervisor();
        supervisor.Register("bge", ModelRole.Reranker, new Uri("http://127.0.0.1:9/v1"));
        AssertEx.True(supervisor.WaitForLeasesReleasedAsync("bge", ModelRole.Reranker, CancellationToken.None).IsCompletedSuccessfully);

        var first = supervisor.TryAcquireInferenceLease("bge", ModelRole.Reranker);
        var second = supervisor.TryAcquireInferenceLease("bge", ModelRole.Reranker);
        AssertEx.Equal(new Uri("http://127.0.0.1:9/v1"), AssertEx.NotNull(first.Endpoint).BaseAddress);
        var released = supervisor.WaitForLeasesReleasedAsync("bge", ModelRole.Reranker, CancellationToken.None);

        AssertEx.NotNull(first.Lease).Dispose();
        first.Lease!.Dispose();
        AssertEx.False(released.IsCompleted, "a double dispose must not release the other outstanding lease.");
        AssertEx.NotNull(second.Lease).Dispose();
        await released;
    }

    [Test]
    [Arguments(2048L, 1792)]
    [Arguments(8192L, 2048)]
    [Arguments(null, 2048)]
    public void PooledContextTokens_CapsAtTrainContextMinusMargin_AlignedDown(long? trainContext, int expected) =>
        AssertEx.Equal(expected, LiveLlamaServer.PooledContextTokens(ModelRole.Reranker, trainContext));

    private static LiveQueryScore Score(string id, bool answerable, double? top1, double? top2, string? kind, bool top1Relevant) =>
        new()
        {
            QueryId = id,
            Category = "en-prose",
            Language = "en",
            Answerable = answerable,
            Top1Score = top1,
            Top2Score = top2,
            Top1ScoreKind = kind,
            Top1Relevant = top1Relevant,
            FirstRelevantRank = top1Relevant ? 1 : 0
        };

    private static LiveConfigResult Row(string id)
    {
        var empty = LiveMetricSummary.From([]);
        return new LiveConfigResult
        {
            Id = id,
            Description = "d",
            Valid = true,
            Overall = empty,
            ByCategory = new Dictionary<string, LiveMetricSummary>(),
            ByLanguage = new Dictionary<string, LiveMetricSummary>(),
            EnglishOnly = empty,
            EndToEnd = LiveLatency.From([]),
            RerankStage = LiveLatency.From([])
        };
    }
}
