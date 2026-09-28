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
                    Degrades = new Dictionary<string, int> { ["timeout"] = 1 },
                    BudgetCancelled = 3,
                    BacklogDrainMilliseconds = 10400
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

        var markdown = RetrievalEvalLiveReportWriter.RenderMarkdown(report);
        AssertEx.True(markdown.Contains("| bge20 | INVALID: forced rerank degraded |", StringComparison.Ordinal), markdown);
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
    public async Task FailedLazySpawn_DegradesTheRealRerankerClient_AndVoidsTheProdSpawnRow()
    {
        await using var supervisor = new LiveEndpointSupervisor();
        supervisor.RegisterLazy("bge", ModelRole.Reranker, static _ => Task.FromException<LiveLlamaServer>(new LiveInfraException("port taken")), CancellationToken.None);
        using var http = new HttpClient();

        // The mechanism that made the row look valid: the product client swallows the failed spawn as a degrade.
        var scores = await new LlamaServerRerankerClient(supervisor, http, new DegradeCapturingLogger()).RerankAsync("bge", "q", ["a", "b"], CancellationToken.None);
        AssertEx.Null(scores);

        var (server, failure) = await supervisor.AwaitLazyAsync("bge", ModelRole.Reranker);
        AssertEx.Null(server);
        var row = RetrievalEvalLiveTests.WithSpawnOutcome(Row("PROD-spawn"), server, failure);
        AssertEx.False(row.Valid);
        AssertEx.Equal("reranker spawn failed: port taken", row.InvalidReason);
    }

    [Test]
    public async Task NeverStartedLazySpawn_LeavesTheProdSpawnRowValid()
    {
        await using var supervisor = new LiveEndpointSupervisor();
        supervisor.RegisterLazy("bge", ModelRole.Reranker, static _ => throw new InvalidOperationException("must not spawn"), CancellationToken.None);

        var (server, failure) = await supervisor.AwaitLazyAsync("bge", ModelRole.Reranker);

        AssertEx.Null(server);
        AssertEx.Null(failure);
        AssertEx.True(RetrievalEvalLiveTests.WithSpawnOutcome(Row("PROD-spawn"), server, failure).Valid);
    }

    [Test]
    [Arguments(2048L, 1792)]
    [Arguments(8192L, 2048)]
    [Arguments(null, 2048)]
    public void PooledContextTokens_CapsAtTrainContextMinusMargin_AlignedDown(long? trainContext, int expected) =>
        AssertEx.Equal(expected, LiveLlamaServer.PooledContextTokens(ModelRole.Reranker, trainContext));

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
