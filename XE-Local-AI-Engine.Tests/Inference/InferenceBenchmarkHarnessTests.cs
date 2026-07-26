namespace XE_Local_AI_Engine.Tests.Inference;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class InferenceBenchmarkHarnessTests
{
    private const long Gb = 1024L * 1024 * 1024;
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
            InferenceBenchmarkSpec.Golden("cuda", ctxSize: 256),
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
            InferenceBenchmarkSpec.Golden("cuda", ctxSize: 256),
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
    public async Task RunAsync_ProcessBudgetMateriallyAboveGlobalFree_RejectsAsExternalPressure()
    {
        using var handler = new RoleBenchmarkHandler();
        var harness = BuildHarness(modelCallsTool: false,
            handler,
            globalFreeVram: 4 * Gb,
            processBudgetVram: 8 * Gb);

        var metrics = await harness.RunAsync(ProfilingContext(ModelRole.Embedding),
            InferenceBenchmarkSpec.Golden("cuda", ctxSize: 2048),
            CancellationToken.None);

        AssertEx.False(metrics.Success);
        AssertEx.True(metrics.ExternalPressureDetected);
        AssertEx.Equal<long?>(4 * Gb, metrics.GlobalFreeVramLoadBytes);
        AssertEx.Equal<long?>(8 * Gb, metrics.ProcessBudgetVramLoadBytes);
        AssertEx.NotNullOrEmpty(metrics.DiagnosticsJson);
        AssertEx.Equal(0, handler.PostPaths.Count);
    }

    private static LlamaServerProfilingContext ProfilingContext(ModelRole role)
    {
        return new LlamaServerProfilingContext(new LlamaServerEndpoint("bartowski/Model-GGUF:Q4_K_M",
                role,
                new Uri("http://127.0.0.1:18100/v1")),
            []);
    }

    private static InferenceBenchmarkHarness BuildHarness(bool modelCallsTool,
        HttpMessageHandler? handler = null,
        long? globalFreeVram = 8 * Gb,
        long? processBudgetVram = 8 * Gb,
        IInferenceChatClientFactory? chatFactory = null)
    {
        chatFactory ??= Substitute.For<IInferenceChatClientFactory>();
        chatFactory.CreateChatClient(Arg.Any<Uri>(), Arg.Any<string>())
                   .Returns(_ => new FakeBenchmarkChatClient(modelCallsTool));

        handler ??= SharedEmptyMetricsHandler;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>())
                         .Returns(_ => new HttpClient(handler, disposeHandler: false));

        var hardwareProfiler = Substitute.For<IHardwareProfiler>();
        hardwareProfiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(NvidiaProfile(globalFreeVram)));

        var processBudgetProbe = Substitute.For<IProcessVramBudgetProbe>();
        processBudgetProbe.TryGetProcessBudgetBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult(processBudgetVram));

        return new InferenceBenchmarkHarness(chatFactory,
            httpClientFactory,
            hardwareProfiler,
            processBudgetProbe,
            NullLogger<InferenceBenchmarkHarness>.Instance);
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

    private sealed class EmptyMetricsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(JsonOrText(HttpStatusCode.OK, string.Empty, "text/plain"));
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
