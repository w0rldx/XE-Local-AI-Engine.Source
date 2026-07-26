namespace XE_Local_AI_Engine.Tests.Inference;

using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="InferenceBenchmarkHarness.TryParsePromMetric" /> tests: the pure Prometheus-text parser pulls a named
///     counter out of a <c>/metrics</c> scrape (including a label set) and returns <see langword="null" /> when the
///     metric is absent — so throughput derivation is unit-testable without a live llama-server.
/// </summary>
public sealed class InferenceBenchmarkHarnessTests
{
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
        // A bare counter line.
        AssertEx.Equal<double?>(1234d, InferenceBenchmarkHarness.TryParsePromMetric(MetricsScrape, "llamacpp:prompt_tokens_total"));

        // A counter carrying a label set is still extracted (the value follows the closing brace).
        AssertEx.Equal<double?>(567.5d, InferenceBenchmarkHarness.TryParsePromMetric(MetricsScrape, "llamacpp:tokens_predicted_total"));
    }

    [Test]
    public void PromMetricParser_ReturnsNull_WhenAbsent()
    {
        AssertEx.Null(InferenceBenchmarkHarness.TryParsePromMetric(MetricsScrape, "llamacpp:tokens_predicted_seconds_total"));
        AssertEx.Null(InferenceBenchmarkHarness.TryParsePromMetric(text: null, "llamacpp:prompt_tokens_total"));
    }

    // ──────────────────────────────────────────────────────────────────────
    // Tool-loop stage — the client is decorated with function invocation, so the tool delegate runs and a second model
    // turn completes. The measurement is real only when the model actually invoked the tool.
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task RunAsync_ToolStage_RecordsToolLoop_WhenModelInvokesTool()
    {
        var harness = BuildHarness(modelCallsTool: true);

        var metrics = await harness.RunAsync(ProfilingContext(), InferenceBenchmarkSpec.Golden("cuda", ctxSize: 256), CancellationToken.None);

        AssertEx.True(metrics.Success, metrics.FailureReason);
        // FICC drove the tool delegate and the second model turn completed, so the tool-loop measurement is valid.
        AssertEx.True(metrics.ToolLoopMs.HasValue, "ToolLoopMs must be recorded when the model invoked the tool");
        AssertEx.True(metrics.ToolLoopMs >= 0);
    }

    [Test]
    public async Task RunAsync_ToolStage_RecordsNull_WhenModelNeverInvokesTool()
    {
        var harness = BuildHarness(modelCallsTool: false);

        var metrics = await harness.RunAsync(ProfilingContext(), InferenceBenchmarkSpec.Golden("cuda", ctxSize: 256), CancellationToken.None);

        AssertEx.True(metrics.Success, metrics.FailureReason);
        // The model returned text without calling the tool: a single plain completion is NOT a tool loop, so the harness
        // records null rather than a bogus single-turn timing.
        AssertEx.Null(metrics.ToolLoopMs);
    }

    private static LlamaServerProfilingContext ProfilingContext()
    {
        return new LlamaServerProfilingContext(new LlamaServerEndpoint("bartowski/Model-GGUF:Q4_K_M", ModelRole.Chat, new Uri("http://127.0.0.1:18100/v1")),
            []);
    }

    private static InferenceBenchmarkHarness BuildHarness(bool modelCallsTool)
    {
        var chatClientFactory = Substitute.For<IInferenceChatClientFactory>();
        chatClientFactory.CreateChatClient(Arg.Any<Uri>(), Arg.Any<string>())
                         .Returns(_ => new FakeBenchmarkChatClient(modelCallsTool));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new EmptyMetricsHandler()));

        var vramProbe = Substitute.For<IProcessVramBudgetProbe>();

        return new InferenceBenchmarkHarness(chatClientFactory, httpClientFactory, vramProbe, NullLogger<InferenceBenchmarkHarness>.Instance);
    }

    // A fake chat client that streams a fixed reply for the throughput stages and, for the tool stage, either scripts a
    // tool-call → text round (first turn calls the offered tool, second turn answers) or answers directly without ever
    // calling the tool. A fresh instance is handed out per factory call, so disposing the tool stage's inner client
    // never tears down the streaming client the other stages use.
    private sealed class FakeBenchmarkChatClient(bool callsTool) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            // Only the tool stage offers tools; every other GetResponseAsync stage (e.g. the long-context injection)
            // runs tool-less and gets a plain completion.
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

    // Returns an empty 200 for every /metrics scrape so throughput derivation sees no counters (rates fall to null)
    // without any network — the tool-loop assertions do not depend on throughput.
    private sealed class EmptyMetricsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            });
        }
    }
}
