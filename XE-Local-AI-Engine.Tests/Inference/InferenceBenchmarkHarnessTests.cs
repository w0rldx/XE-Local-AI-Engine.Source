namespace XE_Local_AI_Engine.Tests.Inference;

using System.ClientModel.Primitives;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;
using StreamingChatCompletionUpdate = OpenAI.Chat.StreamingChatCompletionUpdate;

public sealed class InferenceBenchmarkHarnessTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const long Mib = 1024L * 1024;
    private static readonly EmptyMetricsHandler SharedEmptyMetricsHandler = new();

    private const string MetricsScrape = """
                                         # HELP llamacpp:prompt_tokens_total Number of prompt tokens processed.
                                         # TYPE llamacpp:prompt_tokens_total counter
                                         llamacpp:prompt_tokens_total 1234
                                         # TYPE llamacpp:tokens_predicted_total counter
                                         llamacpp:tokens_predicted_total{slot="0"} 567.5
                                         """;

    [Test]
    public void PromMetricParser_ExtractsGauge()
    {
        AssertEx.Equal<double?>(1234d, InferenceBenchmarkHarness.TryParsePromMetric(MetricsScrape, "llamacpp:prompt_tokens_total"));
        AssertEx.Equal<double?>(567.5d, InferenceBenchmarkHarness.TryParsePromMetric(MetricsScrape, "llamacpp:tokens_predicted_total"));
    }

    [Test]
    public void SemanticLaunchHash_IgnoresEphemeralReachabilityButChangesWithInferencePolicy()
    {
        var first = InferenceBenchmarkHarness.HashSemanticLaunchArguments([
            "-m", "/private/a.gguf", "--host", "127.0.0.1", "--port", "18001", "-c", "4096", "-ctk", "q8_0"
        ]);
        var samePolicy = InferenceBenchmarkHarness.HashSemanticLaunchArguments([
            "--model", "C:\\private\\b.gguf", "--host", "localhost", "--port", "29111", "-c", "4096", "-ctk", "q8_0"
        ]);
        var changedPolicy = InferenceBenchmarkHarness.HashSemanticLaunchArguments([
            "-m", "/private/a.gguf", "--host", "127.0.0.1", "--port", "18001", "-c", "8192", "-ctk", "q8_0"
        ]);

        var firstHash = AssertEx.NotNull(first);
        AssertEx.Equal(firstHash, samePolicy);
        AssertEx.NotEqual(firstHash, changedPolicy);
    }

    [Test]
    public void PromMetricParser_ReturnsNull_WhenAbsent()
    {
        AssertEx.Null(InferenceBenchmarkHarness.TryParsePromMetric(MetricsScrape, "llamacpp:tokens_predicted_seconds_total"));
        AssertEx.Null(InferenceBenchmarkHarness.TryParsePromMetric(text: null, "llamacpp:prompt_tokens_total"));
    }

    [Test]
    public async Task RunAsync_ToolStage_RecordsToolLoop_WhenModelInvokesTool()
    {
        var harness = BuildHarness(modelCallsTool: true);

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Chat),
            InferenceBenchmarkSpec.Golden("cuda", ctxSize: 256) with
            {
                WarmupRuns = 0,
                MeasuredRuns = 1
            },
            CancellationToken.None);

        AssertEx.True(metrics.Success, metrics.FailureReason);
        AssertEx.True(metrics.ToolLoopMs.HasValue, "ToolLoopMs must be recorded when the model invoked the tool");
        AssertEx.True(metrics.ToolLoopMs >= 0);
        AssertEx.Equal(ModelRole.Chat.ToString(), metrics.Role);
    }

    [Test]
    public async Task RunAsync_ToolStage_RecordsNull_WhenModelNeverInvokesTool()
    {
        var harness = BuildHarness(modelCallsTool: false);

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Chat),
            InferenceBenchmarkSpec.Golden("cuda", ctxSize: 256) with
            {
                WarmupRuns = 0,
                MeasuredRuns = 1
            },
            CancellationToken.None);

        AssertEx.True(metrics.Success, metrics.FailureReason);
        AssertEx.Null(metrics.ToolLoopMs);
    }

    [Test]
    public async Task RunAsync_EmbeddingRole_UsesEmbeddingsEndpoint_AndReportsCorrectnessStatistics()
    {
        using var handler = new RoleBenchmarkHandler();
        var chatFactory = Substitute.For<IInferenceChatClientFactory>();
        var harness = BuildHarness(modelCallsTool: false, handler, chatFactory: chatFactory);
        var spec = InferenceBenchmarkSpec.Golden("cuda", ctxSize: 2048) with
        {
            WarmupRuns = 1,
            MeasuredRuns = 3
        };

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Embedding), spec, CancellationToken.None);

        AssertEx.True(metrics.Success, metrics.FailureReason);
        AssertEx.Equal(ModelRole.Embedding.ToString(), metrics.Role);
        AssertEx.Equal(3, metrics.Runs);
        AssertEx.Equal<int?>(spec.EmbeddingInputs.Count, metrics.BatchSize);
        AssertEx.Equal<int?>(3, metrics.OutputDimension);
        AssertEx.True(metrics.ValuesFinite == true);
        AssertEx.True(metrics.DeterministicOutput == true);
        AssertEx.True(metrics.ItemsPerSecond > 0d);
        AssertEx.True(metrics.P50LatencyMs >= 0d);
        AssertEx.True(metrics.P95LatencyMs >= metrics.P50LatencyMs);
        AssertEx.Equal(4, handler.PostPaths.Count(path => path.EndsWith("/v1/embeddings", StringComparison.Ordinal)));
        AssertEx.False(handler.PostPaths.Any(path => path.Contains("chat/completions", StringComparison.Ordinal)));
        chatFactory.DidNotReceiveWithAnyArgs().CreateChatClient(default!, default!);
    }

    [Test]
    public async Task RunAsync_RerankerRole_UsesRerankEndpoint_AndReportsStableOrderStatistics()
    {
        using var handler = new RoleBenchmarkHandler();
        var chatFactory = Substitute.For<IInferenceChatClientFactory>();
        var harness = BuildHarness(modelCallsTool: false, handler, chatFactory: chatFactory);
        var spec = InferenceBenchmarkSpec.Golden("cuda", ctxSize: 2048) with
        {
            WarmupRuns = 1,
            MeasuredRuns = 3
        };

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Reranker), spec, CancellationToken.None);

        AssertEx.True(metrics.Success, metrics.FailureReason);
        AssertEx.Equal(ModelRole.Reranker.ToString(), metrics.Role);
        AssertEx.Equal(3, metrics.Runs);
        AssertEx.Equal<int?>(spec.RerankerDocuments.Count, metrics.BatchSize);
        AssertEx.Null(metrics.OutputDimension);
        AssertEx.True(metrics.ValuesFinite == true);
        AssertEx.True(metrics.DeterministicOutput == true);
        AssertEx.True(metrics.ItemsPerSecond > 0d);
        AssertEx.Equal(4, handler.PostPaths.Count(path => path.EndsWith("/v1/rerank", StringComparison.Ordinal)));
        AssertEx.False(handler.PostPaths.Any(path => path.Contains("chat/completions", StringComparison.Ordinal)));
        chatFactory.DidNotReceiveWithAnyArgs().CreateChatClient(default!, default!);
    }

    [Test]
    public async Task RunAsync_IdleWddmAmbientOffset_AllowsWorkload()
    {
        using var handler = new RoleBenchmarkHandler();
        const long globalFree = 28754 * Mib;
        const long processBudget = 30927 * Mib;
        var harness = BuildHarness(modelCallsTool: false,
            handler,
            globalFreeVram: globalFree,
            processBudgetVram: processBudget);
        var spec = InferenceBenchmarkSpec.Golden("cuda", ctxSize: 2048) with
        {
            WarmupRuns = 0,
            MeasuredRuns = 1
        };

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Embedding,
                preSpawnVram: new LlamaServerProfilingVramSnapshot(globalFree, processBudget)),
            spec,
            CancellationToken.None);

        AssertEx.True(metrics.Success, metrics.FailureReason);
        AssertEx.False(metrics.ExternalPressureDetected);
        AssertEx.Equal<long?>(globalFree, metrics.GlobalFreeVramLoadBytes);
        AssertEx.Equal<long?>(processBudget, metrics.ProcessBudgetVramLoadBytes);
        AssertEx.NotNullOrEmpty(metrics.DiagnosticsJson);
        AssertEx.Equal(1, handler.PostPaths.Count(path => path.EndsWith("/v1/embeddings", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunAsync_BallastPressure_RejectsBeforeWorkload()
    {
        using var handler = new RoleBenchmarkHandler();
        const long globalFree = 6550 * Mib;
        const long processBudget = 29283 * Mib;
        var harness = BuildHarness(modelCallsTool: false,
            handler,
            globalFreeVram: globalFree,
            processBudgetVram: processBudget);
        var spec = InferenceBenchmarkSpec.Golden("cuda", ctxSize: 2048) with
        {
            WarmupRuns = 0,
            MeasuredRuns = 1
        };

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Embedding,
                preSpawnVram: new LlamaServerProfilingVramSnapshot(globalFree, processBudget)),
            spec,
            CancellationToken.None);

        AssertEx.False(metrics.Success);
        AssertEx.True(metrics.ExternalPressureDetected);
        AssertEx.Contains(metrics.FailureReason!, $"global free {globalFree} bytes");
        AssertEx.Contains(metrics.FailureReason!, "Close other GPU workloads and retry");
        AssertEx.Equal(0, handler.PostPaths.Count);
    }

    [Test]
    public async Task RunAsync_BallastPressure_WithExplicitPreSpawnOverride_AllowsWorkload()
    {
        using var handler = new RoleBenchmarkHandler();
        const long globalFree = 6550 * Mib;
        const long processBudget = 29283 * Mib;
        var harness = BuildHarness(modelCallsTool: false,
            handler,
            globalFreeVram: globalFree,
            processBudgetVram: processBudget);
        var spec = InferenceBenchmarkSpec.Golden("cuda", ctxSize: 2048) with
        {
            WarmupRuns = 0,
            MeasuredRuns = 1,
            RejectPreSpawnVramPressure = false
        };

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Embedding,
                preSpawnVram: new LlamaServerProfilingVramSnapshot(globalFree, processBudget)),
            spec,
            CancellationToken.None);

        AssertEx.True(metrics.Success, metrics.FailureReason);
        AssertEx.False(metrics.ExternalPressureDetected);
        AssertEx.Equal(1, handler.PostPaths.Count(path => path.EndsWith("/v1/embeddings", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunAsync_PressureIntroducedDuringBenchmark_RejectsAsExternalPressure()
    {
        using var handler = new RoleBenchmarkHandler();
        var harness = BuildHarness(modelCallsTool: false,
            handler,
            globalFreeVram: 6 * Gb,
            processBudgetVram: 8 * Gb,
            globalFreeVramSamples: [6 * Gb, 4 * Gb, 4 * Gb]);
        var spec = InferenceBenchmarkSpec.Golden("cuda", ctxSize: 2048) with
        {
            WarmupRuns = 0,
            MeasuredRuns = 1
        };

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Embedding,
                preSpawnVram: new LlamaServerProfilingVramSnapshot(8 * Gb, 8 * Gb)),
            spec,
            CancellationToken.None);

        AssertEx.False(metrics.Success);
        AssertEx.True(metrics.ExternalPressureDetected);
        AssertEx.Equal<long?>(6 * Gb, metrics.GlobalFreeVramLoadBytes);
        AssertEx.Equal<long?>(4 * Gb, metrics.GlobalFreeVramAfterBytes);
        AssertEx.Equal<long?>(8 * Gb, metrics.ProcessBudgetVramLoadBytes);
        AssertEx.Equal<long?>(8 * Gb, metrics.ProcessBudgetVramAfterBytes);
        AssertEx.Equal(1, handler.PostPaths.Count(path => path.EndsWith("/v1/embeddings", StringComparison.Ordinal)));
    }

    [Test]
    public async Task RunAsync_ChatRole_HonorsWarmupsAndRepeatedRuns_WithMedianP95AndResourcePeaks()
    {
        var harness = BuildHarness(modelCallsTool: true);
        var spec = InferenceBenchmarkSpec.Golden("cuda", ctxSize: 256) with
        {
            WarmupRuns = 1,
            MeasuredRuns = 3
        };

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Chat, Environment.ProcessId),
            spec,
            CancellationToken.None);

        AssertEx.True(metrics.Success, metrics.FailureReason);
        AssertEx.Equal(3, metrics.Runs);
        AssertEx.True(metrics.P50LatencyMs >= 0d);
        AssertEx.True(metrics.P95LatencyMs >= metrics.P50LatencyMs);
        AssertEx.True(metrics.PeakProcessRamBytes > 0);
        AssertEx.Equal<long?>(8 * Gb, metrics.MinimumGlobalFreeVramBytes);
        AssertEx.Equal<long?>(8 * Gb, metrics.MinimumProcessBudgetVramBytes);
    }

    [Test]
    public async Task RunAsync_ChatCacheEvidence_UsesWarmTimingFractionWhenColdAndWarmEvaluatedCountsMatch()
    {
        const int coldEvaluated = 6;
        const int warmCached = 4;
        const int warmEvaluated = 6;
        using var handler = new BenchmarkMetricsHandler();
        using var chatClient = new TimedBenchmarkChatClient([
                [TimingUpdate(cached: 2, evaluated: 8, text: "warm"), TimingUpdate(warmCached, warmEvaluated, string.Empty)]
            ],
            TimingUpdate(cached: 0, evaluated: coldEvaluated, text: "cold"));
        var harness = BuildHarness(modelCallsTool: false,
            handler,
            chatFactory: ChatFactory(chatClient));

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Chat),
            InferenceBenchmarkSpec.Golden("cuda", ctxSize: 256) with
            {
                WarmupRuns = 0,
                MeasuredRuns = 1
            },
            CancellationToken.None);

        AssertEx.True(metrics.Success, metrics.FailureReason);
        AssertEx.True(coldEvaluated <= warmEvaluated);
        AssertEx.True(warmCached > 0);
        AssertEx.True(metrics.CacheHitRate > 0d);
        AssertEx.Equal<double?>(0.4d, metrics.CacheHitRate);
        var sample = AssertEx.NotNull(AssertEx.NotNull(metrics.WarmPromptTimings).Single());
        AssertEx.Equal<int?>(4, sample.CachedPromptTokens);
        AssertEx.Equal<int?>(6, sample.PromptTokens);
    }

    [Test]
    public async Task RunAsync_ChatCacheEvidence_PreservesNullAndZeroSamplesAndExcludesWarmup()
    {
        using var chatClient = new TimedBenchmarkChatClient([
            [TimingUpdate(cached: 99, evaluated: 1, text: string.Empty)],
            [TimingUpdate(cached: 0, evaluated: 10, text: string.Empty)],
            [PlainUpdate(string.Empty)],
            [RawUpdate("""
                       {"id":"chunk","object":"chat.completion.chunk","created":1,"model":"m",
                        "choices":[{"index":0,"finish_reason":"stop","delta":{}}],
                        "timings":{"cache_n":2,"prompt_n":"bad","predicted_n":1}}
                       """, string.Empty)],
            [TimingUpdate(cached: -1, evaluated: -1, text: string.Empty)],
            [TimingUpdate(cached: 0, evaluated: 0, text: string.Empty)],
            [TimingUpdate(cached: int.MaxValue, evaluated: int.MaxValue, text: string.Empty)]
        ]);
        var harness = BuildHarness(modelCallsTool: false, chatFactory: ChatFactory(chatClient));

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Chat),
            InferenceBenchmarkSpec.Golden("cuda", ctxSize: 256) with
            {
                WarmupRuns = 1,
                MeasuredRuns = 6
            },
            CancellationToken.None);

        AssertEx.True(metrics.Success, metrics.FailureReason);
        AssertEx.Equal(6, metrics.Runs);
        AssertEx.Equal<double?>(0d, metrics.CacheHitRate);
        var samples = AssertEx.NotNull(metrics.WarmPromptTimings);
        AssertEx.Equal(6, samples.Count);
        AssertEx.Equal<int?>(0, AssertEx.NotNull(samples[0]).CachedPromptTokens);
        AssertEx.Null(samples[1]);
        AssertEx.Null(AssertEx.NotNull(samples[2]).PromptTokens);
        AssertEx.Null(AssertEx.NotNull(samples[3]).PromptTokens);
        AssertEx.Equal<int?>(0, AssertEx.NotNull(samples[4]).PromptTokens);
        AssertEx.Equal<int?>(int.MaxValue, AssertEx.NotNull(samples[5]).PromptTokens);

        using var diagnostics = JsonDocument.Parse(AssertEx.NotNull(metrics.DiagnosticsJson));
        var evidence = diagnostics.RootElement.GetProperty("cacheEvidence");
        AssertEx.Equal("warm-timings-v1", evidence.GetProperty("method").GetString());
        AssertEx.Equal(2, evidence.GetProperty("validSamples").GetInt32());
        AssertEx.Equal(6, evidence.GetProperty("totalSamples").GetInt32());
        var diagnosticSamples = evidence.GetProperty("samples");
        AssertEx.Equal(6, diagnosticSamples.GetArrayLength());
        AssertEx.Equal(0, diagnosticSamples[0].GetProperty("cachedPromptTokens").GetInt32());
        AssertEx.Equal(10, diagnosticSamples[0].GetProperty("evaluatedPromptTokens").GetInt32());
        AssertEx.Equal(JsonValueKind.Null, diagnosticSamples[1].ValueKind);
    }

    [Test]
    public async Task RunAsync_PersistsSanitizedRuntimeLoadCorrelationInDiagnostics()
    {
        using var handler = new BenchmarkMetricsHandler();
        var harness = BuildHarness(modelCallsTool: true, handler: handler);
        var context = ProfilingContext(ModelRole.Chat) with
        {
            SuccessfulLaunchArguments = ["-m", "/private/models/model.gguf", "-c", "4096"],
            LoadObservation = new LlamaServerLoadObservation(ModelRole.Chat,
                GpuVariant.Cuda,
                RuntimeVersion: "b10375",
                RuntimeSha256: new string('A', 64),
                ReadinessDurationMs: 812.5,
                LlamaServerReadinessOutcome.Ready,
                LlamaServerPlacementOutcome.Partial,
                LlamaServerLoadAttemptKind.Primary,
                SpeculativeModeClass.MainModelHeads,
                ModelName: "model")
        };
        var spec = InferenceBenchmarkSpec.Golden("cuda", ctxSize: 256) with
        {
            WarmupRuns = 0,
            MeasuredRuns = 1
        };

        var metrics = await harness.RunAsync(context, spec, CancellationToken.None);

        using var diagnostics = JsonDocument.Parse(AssertEx.NotNull(metrics.DiagnosticsJson));
        var runtime = diagnostics.RootElement.GetProperty("runtime");
        AssertEx.Equal("b10375", runtime.GetProperty("version").GetString());
        AssertEx.Equal(new string('A', 64), runtime.GetProperty("sha256").GetString());
        AssertEx.Equal("Partial", runtime.GetProperty("placement").GetString());
        AssertEx.Equal("Primary", runtime.GetProperty("attemptKind").GetString());
        AssertEx.Equal("MainModelHeads", runtime.GetProperty("speculationClass").GetString());
        AssertEx.False(runtime.GetRawText().Contains("/private/models", StringComparison.Ordinal));
        AssertEx.Equal(expected: 64, runtime.GetProperty("launchArgumentsSha256").GetString()!.Length);
        var serialized = diagnostics.RootElement.GetRawText();
        AssertEx.False(serialized.Contains("speculativeDraftTokens", StringComparison.Ordinal));
        AssertEx.False(serialized.Contains("speculativeAcceptedTokens", StringComparison.Ordinal));
        AssertEx.False(serialized.Contains("speculativeVerificationSteps", StringComparison.Ordinal));
        AssertEx.False(serialized.Contains("speculativeAcceptanceRate", StringComparison.Ordinal));
        AssertEx.Equal(2, handler.ScrapeCount);
        AssertEx.Equal<double?>(102d, metrics.ContextTokensHighWatermark);
        AssertEx.Equal<double?>(1d, metrics.AverageBusySlotsPerDecode);
        AssertEx.Equal<double?>(0d, metrics.RequestsProcessingAtLastScrape);
        AssertEx.Equal<double?>(0d, metrics.RequestsDeferredAtLastScrape);
    }

    private static LlamaServerProfilingContext ProfilingContext(ModelRole role,
        int? processId = null,
        LlamaServerProfilingVramSnapshot? preSpawnVram = null)
    {
        return new LlamaServerProfilingContext(new LlamaServerEndpoint("bartowski/Model-GGUF:Q4_K_M",
                role,
                new Uri("http://127.0.0.1:18100/v1")),
            [],
            FitParamsOutput: [],
            processId)
        {
            PreSpawnVram = preSpawnVram
        };
    }

    private static InferenceBenchmarkHarness BuildHarness(bool modelCallsTool,
        HttpMessageHandler? handler = null,
        long? globalFreeVram = 8 * Gb,
        long? processBudgetVram = 8 * Gb,
        IInferenceChatClientFactory? chatFactory = null,
        IReadOnlyList<long?>? globalFreeVramSamples = null)
    {
        if (chatFactory is null)
        {
            chatFactory = Substitute.For<IInferenceChatClientFactory>();
            chatFactory.CreateChatClient(Arg.Any<Uri>(), Arg.Any<string>())
                       .Returns(_ => new FakeBenchmarkChatClient(modelCallsTool));
        }

        handler ??= SharedEmptyMetricsHandler;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
                         .Returns(_ => new HttpClient(handler, disposeHandler: false));

        var remainingGlobalFreeSamples = new Queue<long?>(globalFreeVramSamples ?? [globalFreeVram]);
        var lastGlobalFreeSample = globalFreeVram;
        var hardwareProfiler = Substitute.For<IHardwareProfiler>();
        hardwareProfiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                        .Returns(_ =>
                        {
                            if (remainingGlobalFreeSamples.Count > 0)
                            {
                                lastGlobalFreeSample = remainingGlobalFreeSamples.Dequeue();
                            }

                            return Task.FromResult(NvidiaProfile(lastGlobalFreeSample));
                        });

        var processBudgetProbe = Substitute.For<IProcessVramBudgetProbe>();
        processBudgetProbe.TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult(processBudgetVram));

        return new InferenceBenchmarkHarness(chatFactory,
            httpClientFactory,
            hardwareProfiler,
            processBudgetProbe,
            NullLogger<InferenceBenchmarkHarness>.Instance);
    }

    private static IInferenceChatClientFactory ChatFactory(IChatClient client)
    {
        var factory = Substitute.For<IInferenceChatClientFactory>();
        factory.CreateChatClient(Arg.Any<Uri>(), Arg.Any<string>()).Returns(client);
        return factory;
    }

    private static ChatResponseUpdate TimingUpdate(int cached, int evaluated, string text)
    {
        var json = string.Create(CultureInfo.InvariantCulture,
            $"{{\"id\":\"chunk\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\","
            + $"\"choices\":[{{\"index\":0,\"finish_reason\":\"stop\",\"delta\":{{}}}}],"
            + $"\"timings\":{{\"cache_n\":{cached},\"prompt_n\":{evaluated},\"predicted_n\":1}}}}");
        return RawUpdate(json, text);
    }

    private static ChatResponseUpdate PlainUpdate(string text) => new(ChatRole.Assistant, text);

    private static ChatResponseUpdate RawUpdate(string json, string text)
    {
        var chunk = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(json))
                    ?? throw new InvalidOperationException("The chunk fixture did not deserialize.");
        return new ChatResponseUpdate(ChatRole.Assistant, text) { RawRepresentation = chunk };
    }

    private static HardwareProfile NvidiaProfile(long? availableVramBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = 24 * Gb,
            AvailableVramBytes = availableVramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }

    private sealed class FakeBenchmarkChatClient(bool callsTool) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var toolHasRun = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any();
            var tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault();
            if (!callsTool || toolHasRun || tool is null)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            }

            var call = new FunctionCallContent("call-1", tool.Name, new Dictionary<string, object?>());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                call
            })));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "primary");
            yield return new ChatResponseUpdate(ChatRole.Assistant, " colors");
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class TimedBenchmarkChatClient(IReadOnlyList<IReadOnlyList<ChatResponseUpdate>> warmStreams,
        ChatResponseUpdate? coldUpdate = null) : IChatClient
    {
        private int _streamCall;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _streamCall);
            if (call % 2 == 1)
            {
                yield return coldUpdate ?? PlainUpdate("cold");
                yield break;
            }

            foreach (var update in warmStreams[(call / 2) - 1])
            {
                yield return update;
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class EmptyMetricsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(JsonOrText(HttpStatusCode.OK, string.Empty, "text/plain"));
        }
    }

    private sealed class BenchmarkMetricsHandler : HttpMessageHandler
    {
        private int _scrapes;

        public int ScrapeCount => Volatile.Read(ref _scrapes);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var scrape = Interlocked.Increment(ref _scrapes);
            var metrics = $"""
                           llamacpp:prompt_tokens_total {scrape * 100}
                           llamacpp:prompt_seconds_total {scrape.ToString(CultureInfo.InvariantCulture)}
                           llamacpp:tokens_predicted_total {scrape * 50}
                           llamacpp:tokens_predicted_seconds_total {scrape.ToString(CultureInfo.InvariantCulture)}
                           llamacpp:requests_processing 0
                           llamacpp:requests_deferred 0
                           llamacpp:n_tokens_max {100 + scrape}
                           llamacpp:n_busy_slots_per_decode 1
                           """;
            return Task.FromResult(JsonOrText(HttpStatusCode.OK, metrics, "text/plain"));
        }
    }

    private sealed class RoleBenchmarkHandler : HttpMessageHandler
    {
        private int _workloadCalls;

        public List<string> PostPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Get && path.EndsWith("/metrics", StringComparison.Ordinal))
            {
                var calls = Volatile.Read(ref _workloadCalls);
                var metrics = $"""
                               llamacpp:prompt_tokens_total {calls * 100}
                               llamacpp:prompt_seconds_total {(calls * 0.1d).ToString(CultureInfo.InvariantCulture)}
                               """;
                return Task.FromResult(JsonOrText(HttpStatusCode.OK, metrics, "text/plain"));
            }

            PostPaths.Add(path);
            _ = Interlocked.Increment(ref _workloadCalls);
            if (path.EndsWith("/v1/embeddings", StringComparison.Ordinal))
            {
                const string json = """
                                    {"data":[
                                      {"index":0,"embedding":[0.1,0.2,0.3]},
                                      {"index":1,"embedding":[0.2,0.3,0.4]},
                                      {"index":2,"embedding":[0.3,0.4,0.5]},
                                      {"index":3,"embedding":[0.4,0.5,0.6]},
                                      {"index":4,"embedding":[0.5,0.6,0.7]},
                                      {"index":5,"embedding":[0.6,0.7,0.8]},
                                      {"index":6,"embedding":[0.7,0.8,0.9]},
                                      {"index":7,"embedding":[0.8,0.9,1.0]}
                                    ]}
                                    """;
                return Task.FromResult(JsonOrText(HttpStatusCode.OK, json, "application/json"));
            }

            if (path.EndsWith("/v1/rerank", StringComparison.Ordinal))
            {
                const string json = """
                                    {"results":[
                                      {"index":2,"relevance_score":0.9},
                                      {"index":0,"relevance_score":0.8},
                                      {"index":3,"relevance_score":0.2},
                                      {"index":1,"relevance_score":0.1}
                                    ]}
                                    """;
                return Task.FromResult(JsonOrText(HttpStatusCode.OK, json, "application/json"));
            }

            return Task.FromResult(JsonOrText(HttpStatusCode.NotFound, string.Empty, "text/plain"));
        }
    }

    private static HttpResponseMessage JsonOrText(HttpStatusCode status, string content, string mediaType)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        };
    }
}
