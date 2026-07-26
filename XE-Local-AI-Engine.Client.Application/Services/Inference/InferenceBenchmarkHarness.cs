namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="IInferenceBenchmarkHarness" />. Drives the fixed golden transcript through an
///     <see cref="IChatClient" /> seam (cold → warm-cache follow-up → mock tool round → long-context injection), measures
///     wall-clock TTFT and tool-loop, scrapes the llama-server <c>/metrics</c> Prometheus surface before/after to derive
///     PP/TG tokens-per-second and the cold-vs-warm cache-hit ratio, and samples host free-VRAM at load and after the
///     loop. The live chat loop is operator-verified (it needs a real GPU process); the orchestration is unit-tested via
///     a fake chat client, and <see cref="TryParsePromMetric" /> is a pure, separately-tested helper.
/// </summary>
public sealed class InferenceBenchmarkHarness : IInferenceBenchmarkHarness
{
    // llama-server Prometheus counter names (verified against the llama.cpp server /metrics surface).
    private const string PromptTokensMetric = "llamacpp:prompt_tokens_total";
    private const string PredictedTokensMetric = "llamacpp:tokens_predicted_total";
    private const string PromptSecondsMetric = "llamacpp:prompt_seconds_total";
    private const string PredictedSecondsMetric = "llamacpp:tokens_predicted_seconds_total";

    private readonly IInferenceChatClientFactory _chatClientFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<InferenceBenchmarkHarness> _logger;
    private readonly IProcessVramBudgetProbe _vramProbe;

    public InferenceBenchmarkHarness(IInferenceChatClientFactory chatClientFactory,
        IHttpClientFactory httpClientFactory,
        IProcessVramBudgetProbe vramProbe,
        ILogger<InferenceBenchmarkHarness> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClientFactory);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(vramProbe);
        ArgumentNullException.ThrowIfNull(logger);

        _chatClientFactory = chatClientFactory;
        _httpClientFactory = httpClientFactory;
        _vramProbe = vramProbe;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<InferenceBenchmarkMetrics> RunAsync(LlamaServerProfilingContext context, InferenceBenchmarkSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(spec);

        var endpoint = context.Endpoint;
        var metricsUri = new Uri(endpoint.BaseAddress, "/metrics");

        try
        {
            using var chatClient = _chatClientFactory.CreateChatClient(endpoint.BaseAddress, endpoint.ModelName);

            var totalStopwatch = Stopwatch.StartNew();
            var vramLoad = await _vramProbe.TryGetProcessBudgetBytesAsync(spec.Backend, ct).ConfigureAwait(false);
            var baseline = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);

            var chatOptions = BuildOptions(endpoint.ModelName, spec, tools: null);

            // Stage 1 — cold prompt (empty cache). TTFT is the wall-clock to the first streamed token.
            var coldMessages = new List<ChatMessage>
            {
                new(ChatRole.System, spec.SystemPersona),
                new(ChatRole.User, spec.ColdUserTurn)
            };
            var (ttftMs, coldText) = await StreamStageAsync(chatClient, coldMessages, chatOptions, ct).ConfigureAwait(false);
            var afterCold = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);

            // Stage 2 — warm-cache follow-up: replays the cold context so the shared prefix is served from cache.
            var warmMessages = new List<ChatMessage>(coldMessages)
            {
                new(ChatRole.Assistant, coldText),
                new(ChatRole.User, spec.WarmFollowUpTurn)
            };
            _ = await StreamStageAsync(chatClient, warmMessages, chatOptions, ct).ConfigureAwait(false);
            var afterWarm = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);

            // Stage 3 — one deterministic tool round. The client MUST be decorated with function invocation
            // (UseFunctionInvocation): without it the offered tool delegate is never called and no second model turn
            // happens, so the elapsed time would measure a single plain completion, not a tool loop. A dedicated inner
            // client is wrapped so disposing the FICC wrapper (which cascades to its own inner) never tears down the
            // shared streaming client the other stages use. The measurement is valid ONLY when the model actually
            // invoked the tool AND the second model turn completed; a small local model that refuses the tool yields no
            // tool loop, so ToolLoopMs is recorded as null (a failed measurement) rather than a bogus single-turn time.
            var toolInvocations = 0;
            var toolFunction = AIFunctionFactory.Create(() =>
                {
                    _ = Interlocked.Increment(ref toolInvocations);
                    return spec.Tool.DeterministicResult;
                },
                spec.Tool.Name,
                spec.Tool.Description);
            var toolOptions = BuildOptions(endpoint.ModelName, spec, tools: [toolFunction]);
            var toolMessages = new List<ChatMessage>
            {
                new(ChatRole.System, spec.SystemPersona),
                new(ChatRole.User, spec.ToolUserTurn)
            };

            double? toolLoopMs;
            using (var toolInnerClient = _chatClientFactory.CreateChatClient(endpoint.BaseAddress, endpoint.ModelName))
                using (var toolInvokingClient = toolInnerClient.AsBuilder().UseFunctionInvocation().Build())
                {
                    var toolStopwatch = Stopwatch.StartNew();
                    _ = await toolInvokingClient.GetResponseAsync(toolMessages, toolOptions, ct).ConfigureAwait(false);
                    toolStopwatch.Stop();

                    if (Volatile.Read(ref toolInvocations) > 0)
                    {
                        toolLoopMs = toolStopwatch.Elapsed.TotalMilliseconds;
                    }
                    else
                    {
                        toolLoopMs = null;
                        _logger.LogDebug("Benchmark tool-loop stage: the model did not invoke the offered tool; recording ToolLoopMs as null.");
                    }
                }

            // Stage 4 — long-context injection sized near the profile's context window.
            var longMessages = new List<ChatMessage>
            {
                new(ChatRole.System, spec.SystemPersona),
                new(ChatRole.User, spec.LongContextUserTurn)
            };
            _ = await chatClient.GetResponseAsync(longMessages, chatOptions, ct).ConfigureAwait(false);
            var afterAll = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);

            var vramAfter = await _vramProbe.TryGetProcessBudgetBytesAsync(spec.Backend, ct).ConfigureAwait(false);
            totalStopwatch.Stop();

            var ppTokensPerSecond = DeriveRate(baseline, afterAll, PromptTokensMetric, PromptSecondsMetric);
            var tgTokensPerSecond = DeriveRate(baseline, afterAll, PredictedTokensMetric, PredictedSecondsMetric);
            var cacheHitRate = DeriveCacheHitRate(baseline, afterCold, afterWarm);

            return new InferenceBenchmarkMetrics(Success: true,
                FailureReason: null,
                TokensPerSecond: tgTokensPerSecond,
                PpTokensPerSecond: ppTokensPerSecond,
                TtftMs: ttftMs,
                TotalLatencyMs: totalStopwatch.Elapsed.TotalMilliseconds,
                CacheHitRate: cacheHitRate,
                ToolLoopMs: toolLoopMs,
                VramLoadBytes: vramLoad,
                VramAfterBytes: vramAfter,
                Runs: 1,
                RawJson: afterAll);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Inference benchmark harness failed during the golden transcript.");
            return InferenceBenchmarkMetrics.Failed($"Benchmark harness error: {exception.GetType().Name}.");
        }
    }

    /// <summary>
    ///     Extracts the first sample of the Prometheus metric named <paramref name="name" /> from a <c>/metrics</c> text
    ///     scrape, or <see langword="null" /> when the metric is absent/unparseable. Pure and culture-invariant so it is
    ///     unit-testable without a live server. Tolerates label sets and trailing timestamps; comment lines are skipped.
    /// </summary>
    public static double? TryParsePromMetric(string? text, string name)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#' || !span.StartsWith(name, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = span[name.Length..];
            if (rest.IsEmpty)
            {
                continue;
            }

            if (rest[0] == '{')
            {
                var close = rest.IndexOf('}');
                if (close < 0)
                {
                    continue;
                }

                rest = rest[(close + 1)..];
            }
            else if (rest[0] is not ' ' and not '\t')
            {
                // A longer metric name shares this prefix (e.g. "_seconds_total" vs "_total") — not our metric.
                continue;
            }

            rest = rest.Trim();
            var separator = rest.IndexOfAny(' ', '\t');
            var valueSpan = separator >= 0 ? rest[..separator] : rest;
            if (double.TryParse(valueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static ChatOptions BuildOptions(string modelId, InferenceBenchmarkSpec spec, IList<AITool>? tools)
    {
        return new ChatOptions
        {
            ModelId = modelId,
            Temperature = spec.Temperature,
            Seed = spec.Seed,
            Tools = tools
        };
    }

    private static async Task<(double TtftMs, string Text)> StreamStageAsync(IChatClient chatClient,
        IReadOnlyList<ChatMessage> messages,
        ChatOptions options,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        double? firstTokenMs = null;
        var builder = new StringBuilder();

        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
        {
            firstTokenMs ??= stopwatch.Elapsed.TotalMilliseconds;
            builder.Append(update.Text);
        }

        return (firstTokenMs ?? stopwatch.Elapsed.TotalMilliseconds, builder.ToString());
    }

    private async Task<string?> ScrapeMetricsAsync(Uri metricsUri, CancellationToken ct)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            return await client.GetStringAsync(metricsUri, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogDebug(exception, "llama-server /metrics scrape failed; throughput metrics will be unavailable.");
            return null;
        }
        catch (TaskCanceledException exception) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "llama-server /metrics scrape timed out; throughput metrics will be unavailable.");
            return null;
        }
    }

    // PP/TG tokens-per-second = Δtokens / Δseconds across the run, from the cumulative counters.
    private static double? DeriveRate(string? before, string? after, string tokensMetric, string secondsMetric)
    {
        var tokens = Delta(before, after, tokensMetric);
        var seconds = Delta(before, after, secondsMetric);
        if (tokens is null || seconds is null || seconds <= 0)
        {
            return null;
        }

        return tokens / seconds;
    }

    // Cache-hit = the prompt-token reuse the warm follow-up enjoyed over the cold prompt (fewer prompt tokens processed
    // because the shared prefix was served from the KV cache).
    private static double? DeriveCacheHitRate(string? baseline, string? afterCold, string? afterWarm)
    {
        var coldPromptDelta = Delta(baseline, afterCold, PromptTokensMetric);
        var warmPromptDelta = Delta(afterCold, afterWarm, PromptTokensMetric);
        if (coldPromptDelta is null || warmPromptDelta is null || coldPromptDelta <= 0)
        {
            return null;
        }

        return Math.Clamp(1.0 - (warmPromptDelta.Value / coldPromptDelta.Value), min: 0.0, max: 1.0);
    }

    private static double? Delta(string? before, string? after, string metric)
    {
        var afterValue = TryParsePromMetric(after, metric);
        if (afterValue is null)
        {
            return null;
        }

        var beforeValue = TryParsePromMetric(before, metric) ?? 0d;
        var delta = afterValue.Value - beforeValue;
        // A counter reset (process restart) makes the delta negative — fall back to the absolute after-value.
        return delta >= 0 ? delta : afterValue;
    }
}
