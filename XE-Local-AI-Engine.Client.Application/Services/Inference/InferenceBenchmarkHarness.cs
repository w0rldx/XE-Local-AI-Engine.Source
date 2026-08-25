namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Role-aware inference benchmark harness. Chat retains the fixed golden transcript; embedding and reranker use their
///     valid llama-server endpoints with warm-up + repeated measurements, output correctness checks, and median/p95
///     latency. Every run records global-free VRAM separately from llama.cpp's process-local budget and rejects material
///     divergence both before the profiling server starts and when it grows during measurement, so WDDM contention cannot
///     produce silently paged performance numbers.
/// </summary>
public sealed class InferenceBenchmarkHarness : IInferenceBenchmarkHarness
{
    private const string PromptTokensMetric = "llamacpp:prompt_tokens_total";
    private const string PredictedTokensMetric = "llamacpp:tokens_predicted_total";
    private const string PromptSecondsMetric = "llamacpp:prompt_seconds_total";
    private const string PredictedSecondsMetric = "llamacpp:tokens_predicted_seconds_total";
    private const string RequestsProcessingMetric = "llamacpp:requests_processing";
    private const string RequestsDeferredMetric = "llamacpp:requests_deferred";
    private const string ContextTokensHighWatermarkMetric = "llamacpp:n_tokens_max";
    private const string BusySlotsPerDecodeMetric = "llamacpp:n_busy_slots_per_decode";
    private const string SpeculativeDraftTokensMetric = "llamacpp:spec_decode_num_draft_tokens_total";
    private const string SpeculativeAcceptedTokensMetric = "llamacpp:spec_decode_num_accepted_tokens_total";
    private const string SpeculativeDraftsMetric = "llamacpp:spec_decode_num_drafts_total";

    private const string IncrementalPressureFailureReason =
        "Benchmark invalid: VRAM divergence grew materially during measurement. Close other GPU workloads and retry.";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IInferenceChatClientFactory _chatClientFactory;
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<InferenceBenchmarkHarness> _logger;
    private readonly IProcessVramBudgetProbe _processVramBudgetProbe;

    public InferenceBenchmarkHarness(IInferenceChatClientFactory chatClientFactory,
        IHttpClientFactory httpClientFactory,
        IHardwareProfiler hardwareProfiler,
        IProcessVramBudgetProbe processVramBudgetProbe,
        ILogger<InferenceBenchmarkHarness> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClientFactory);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(hardwareProfiler);
        ArgumentNullException.ThrowIfNull(processVramBudgetProbe);
        ArgumentNullException.ThrowIfNull(logger);

        _chatClientFactory = chatClientFactory;
        _httpClientFactory = httpClientFactory;
        _hardwareProfiler = hardwareProfiler;
        _processVramBudgetProbe = processVramBudgetProbe;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<InferenceBenchmarkMetrics> RunAsync(LlamaServerProfilingContext context,
        InferenceBenchmarkSpec spec,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(spec);

        var role = context.Endpoint.Role;

        try
        {
            var resources = new ResourceEvidenceCollector(context.PreSpawnVram,
                spec.PreSpawnVramAmbientBaselineBytes,
                spec.PreSpawnVramPressureAbsoluteThresholdBytes,
                spec.PreSpawnVramPressureRatioThreshold,
                spec.RejectPreSpawnVramPressure,
                spec.IncrementalVramDivergenceAbsoluteThresholdBytes,
                spec.IncrementalVramDivergenceRatioThreshold);
            var load = await CaptureResourcesAsync(spec, context.ProcessId, ct).ConfigureAwait(false);
            resources.Add(load);

            if (resources.PreSpawnVram?.ExternalPressureDetected == true)
            {
                var preSpawn = resources.PreSpawnVram;
                _logger.LogWarning(
                    "Rejected {Role} inference benchmark before workload because VRAM pressure above the WDDM ambient baseline exceeded the configured thresholds. GlobalFreeBytes={GlobalFreeBytes}, ProcessBudgetBytes={ProcessBudgetBytes}, RawExcessBytes={RawExcessBytes}, PressureAboveBaselineBytes={PressureAboveBaselineBytes}, PressureAboveBaselineRatio={PressureAboveBaselineRatio}.",
                    role,
                    preSpawn?.GlobalFreeBytes,
                    preSpawn?.ProcessBudgetBytes,
                    preSpawn?.ProcessBudgetExcessBytes,
                    preSpawn?.PressureAboveBaselineBytes,
                    preSpawn?.PressureAboveBaselineRatio);
                return ApplyResourceEvidence(InferenceBenchmarkMetrics.Failed(BuildPreSpawnPressureFailureReason(preSpawn)), context, resources);
            }

            async Task CapturePassResourcesAsync(CancellationToken innerCt)
            {
                resources.Add(await CaptureResourcesAsync(spec, context.ProcessId, innerCt).ConfigureAwait(false));
            }

            var metrics = role switch
            {
                ModelRole.Chat => await RunChatAsync(context.Endpoint, spec, CapturePassResourcesAsync, ct).ConfigureAwait(false),
                ModelRole.Embedding => await RunEmbeddingAsync(context.Endpoint, spec, CapturePassResourcesAsync, ct).ConfigureAwait(false),
                ModelRole.Reranker => await RunRerankerAsync(context.Endpoint, spec, CapturePassResourcesAsync, ct).ConfigureAwait(false),
                _ => InferenceBenchmarkMetrics.Failed($"Benchmark role '{role}' is unsupported.")
            };

            resources.Add(await CaptureResourcesAsync(spec, context.ProcessId, ct).ConfigureAwait(false));
            return ApplyResourceEvidence(metrics, context, resources);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Inference benchmark harness failed for role {Role}.", role);
            return InferenceBenchmarkMetrics.Failed($"Benchmark harness error: {exception.GetType().Name}.") with
            {
                Role = role.ToString()
            };
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

    private async Task<InferenceBenchmarkMetrics> RunChatAsync(LlamaServerEndpoint endpoint,
        InferenceBenchmarkSpec spec,
        Func<CancellationToken, Task> captureResources,
        CancellationToken ct)
    {
        for (var warmup = 0; warmup < Math.Max(0, spec.WarmupRuns); warmup++)
        {
            _ = await RunChatPassAsync(endpoint, spec, ct).ConfigureAwait(false);
            await captureResources(ct).ConfigureAwait(false);
        }

        var measuredRuns = Math.Max(1, spec.MeasuredRuns);
        var passes = new List<ChatPassMetrics>(measuredRuns);
        for (var run = 0; run < measuredRuns; run++)
        {
            passes.Add(await RunChatPassAsync(endpoint, spec, ct).ConfigureAwait(false));
            await captureResources(ct).ConfigureAwait(false);
        }

        return new InferenceBenchmarkMetrics(Success: true,
            FailureReason: null,
            TokensPerSecond: MedianNullable(passes.Select(static pass => pass.TokensPerSecond)),
            PpTokensPerSecond: MedianNullable(passes.Select(static pass => pass.PpTokensPerSecond)),
            TtftMs: MedianNullable(passes.Select(static pass => (double?)pass.TtftMs)),
            TotalLatencyMs: Percentile(passes.Select(static pass => pass.TotalLatencyMs).ToArray(), 0.50d),
            CacheHitRate: MedianNullable(passes.Select(static pass => pass.CacheHitRate)),
            ToolLoopMs: MedianNullable(passes.Select(static pass => pass.ToolLoopMs)),
            VramLoadBytes: null,
            VramAfterBytes: null,
            Runs: measuredRuns,
            RawJson: passes[^1].RawMetrics,
            Role: ModelRole.Chat.ToString(),
            P50LatencyMs: Percentile(passes.Select(static pass => pass.TotalLatencyMs).ToArray(), 0.50d),
            P95LatencyMs: Percentile(passes.Select(static pass => pass.TotalLatencyMs).ToArray(), 0.95d),
            RequestsProcessingAtLastScrape: passes[^1].RequestsProcessingAtLastScrape,
            RequestsDeferredAtLastScrape: passes[^1].RequestsDeferredAtLastScrape,
            ContextTokensHighWatermark: MaxNullableDouble(passes.Select(static pass => pass.ContextTokensHighWatermark)),
            AverageBusySlotsPerDecode: MedianNullable(passes.Select(static pass => pass.AverageBusySlotsPerDecode)),
            SpeculativeDraftTokens: MedianNullable(passes.Select(static pass => pass.SpeculativeDraftTokens)),
            SpeculativeAcceptedTokens: MedianNullable(passes.Select(static pass => pass.SpeculativeAcceptedTokens)),
            SpeculativeVerificationSteps: MedianNullable(passes.Select(static pass => pass.SpeculativeVerificationSteps)),
            SpeculativeAcceptanceRate: MedianNullable(passes.Select(static pass => pass.SpeculativeAcceptanceRate)));
    }

    private async Task<ChatPassMetrics> RunChatPassAsync(LlamaServerEndpoint endpoint,
        InferenceBenchmarkSpec spec,
        CancellationToken ct)
    {
        var metricsUri = new Uri(endpoint.BaseAddress, "/metrics");
        using var chatClient = _chatClientFactory.CreateChatClient(endpoint.BaseAddress, endpoint.ModelName);

        var totalStopwatch = Stopwatch.StartNew();
        var baseline = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);
        var chatOptions = BuildOptions(endpoint.ModelName, spec, tools: null);

        var coldMessages = new List<ChatMessage>
        {
            new(ChatRole.System, spec.SystemPersona),
            new(ChatRole.User, spec.ColdUserTurn)
        };
        var (ttftMs, coldText) = await StreamStageAsync(chatClient, coldMessages, chatOptions, ct).ConfigureAwait(false);
        var afterCold = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);

        var warmMessages = new List<ChatMessage>(coldMessages)
        {
            new(ChatRole.Assistant, coldText),
            new(ChatRole.User, spec.WarmFollowUpTurn)
        };
        _ = await StreamStageAsync(chatClient, warmMessages, chatOptions, ct).ConfigureAwait(false);
        var afterWarm = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);

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
                toolLoopMs = Volatile.Read(ref toolInvocations) > 0 ? toolStopwatch.Elapsed.TotalMilliseconds : null;
            }

        var longMessages = new List<ChatMessage>
        {
            new(ChatRole.System, spec.SystemPersona),
            new(ChatRole.User, spec.LongContextUserTurn)
        };
        _ = await chatClient.GetResponseAsync(longMessages, chatOptions, ct).ConfigureAwait(false);
        var afterAll = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);
        totalStopwatch.Stop();

        var speculativeDraftTokens = Delta(baseline, afterAll, SpeculativeDraftTokensMetric);
        var speculativeAcceptedTokens = Delta(baseline, afterAll, SpeculativeAcceptedTokensMetric);
        double? speculativeAcceptanceRate = speculativeDraftTokens is > 0 && speculativeAcceptedTokens is not null
            ? Math.Clamp(speculativeAcceptedTokens.Value / speculativeDraftTokens.Value, min: 0d, max: 1d)
            : null;

        return new ChatPassMetrics(DeriveRate(baseline, afterAll, PredictedTokensMetric, PredictedSecondsMetric),
            DeriveRate(baseline, afterAll, PromptTokensMetric, PromptSecondsMetric),
            ttftMs,
            totalStopwatch.Elapsed.TotalMilliseconds,
            DeriveCacheHitRate(baseline, afterCold, afterWarm),
            toolLoopMs,
            afterAll,
            TryParsePromMetric(afterAll, RequestsProcessingMetric),
            TryParsePromMetric(afterAll, RequestsDeferredMetric),
            TryParsePromMetric(afterAll, ContextTokensHighWatermarkMetric),
            TryParsePromMetric(afterAll, BusySlotsPerDecodeMetric),
            speculativeDraftTokens,
            speculativeAcceptedTokens,
            Delta(baseline, afterAll, SpeculativeDraftsMetric),
            speculativeAcceptanceRate);
    }

    private async Task<InferenceBenchmarkMetrics> RunEmbeddingAsync(LlamaServerEndpoint endpoint,
        InferenceBenchmarkSpec spec,
        Func<CancellationToken, Task> captureResources,
        CancellationToken ct)
    {
        var inputs = spec.EmbeddingInputs;
        if (inputs.Count == 0)
        {
            return InferenceBenchmarkMetrics.Failed("Embedding benchmark corpus is empty.") with
            {
                Role = ModelRole.Embedding.ToString()
            };
        }

        var measuredRuns = Math.Max(1, spec.MeasuredRuns);
        var metricsUri = new Uri(endpoint.BaseAddress, "/metrics");
        var endpointUri = BuildRoleUri(endpoint.BaseAddress, "embeddings");
        using var client = _httpClientFactory.CreateClient();

        for (var warmup = 0; warmup < Math.Max(0, spec.WarmupRuns); warmup++)
        {
            _ = await PostEmbeddingAsync(client, endpointUri, endpoint.ModelName, inputs, ct).ConfigureAwait(false);
            await captureResources(ct).ConfigureAwait(false);
        }

        var baselineMetrics = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);
        var latencies = new List<double>(measuredRuns);
        IReadOnlyList<IReadOnlyList<double>>? baselineVectors = null;
        var allFinite = true;
        var deterministic = true;
        int? dimensions = null;

        for (var run = 0; run < measuredRuns; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            var vectors = await PostEmbeddingAsync(client, endpointUri, endpoint.ModelName, inputs, ct).ConfigureAwait(false);
            stopwatch.Stop();
            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            await captureResources(ct).ConfigureAwait(false);

            if (vectors.Count != inputs.Count || vectors.Count == 0)
            {
                return InferenceBenchmarkMetrics.Failed("Embedding benchmark returned an invalid vector count.") with
                {
                    Role = ModelRole.Embedding.ToString()
                };
            }

            dimensions ??= vectors[0].Count;
            if (dimensions <= 0 || vectors.Any(vector => vector.Count != dimensions.Value))
            {
                return InferenceBenchmarkMetrics.Failed("Embedding benchmark returned inconsistent vector dimensions.") with
                {
                    Role = ModelRole.Embedding.ToString()
                };
            }

            allFinite &= vectors.SelectMany(static vector => vector).All(double.IsFinite);
            if (baselineVectors is null)
            {
                baselineVectors = vectors;
            }
            else
            {
                deterministic &= VectorsEqual(baselineVectors, vectors, spec.DeterminismTolerance);
            }
        }

        var afterMetrics = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);
        var totalSeconds = latencies.Sum() / 1000d;
        var success = allFinite && deterministic;

        return new InferenceBenchmarkMetrics(Success: success,
            FailureReason: success ? null : "Embedding benchmark output failed finite-value or deterministic-equivalence checks.",
            TokensPerSecond: null,
            PpTokensPerSecond: DeriveRate(baselineMetrics, afterMetrics, PromptTokensMetric, PromptSecondsMetric),
            TtftMs: null,
            TotalLatencyMs: latencies.Sum(),
            CacheHitRate: null,
            ToolLoopMs: null,
            VramLoadBytes: null,
            VramAfterBytes: null,
            Runs: measuredRuns,
            RawJson: afterMetrics,
            Role: ModelRole.Embedding.ToString(),
            ItemsPerSecond: Throughput(inputs.Count * measuredRuns, totalSeconds),
            InputTokensPerSecond: CounterThroughput(baselineMetrics, afterMetrics, PromptTokensMetric, totalSeconds),
            P50LatencyMs: Percentile(latencies, 0.50d),
            P95LatencyMs: Percentile(latencies, 0.95d),
            BatchSize: inputs.Count,
            OutputDimension: dimensions,
            ValuesFinite: allFinite,
            DeterministicOutput: deterministic);
    }

    private async Task<InferenceBenchmarkMetrics> RunRerankerAsync(LlamaServerEndpoint endpoint,
        InferenceBenchmarkSpec spec,
        Func<CancellationToken, Task> captureResources,
        CancellationToken ct)
    {
        var documents = spec.RerankerDocuments;
        if (documents.Count == 0)
        {
            return InferenceBenchmarkMetrics.Failed("Reranker benchmark corpus is empty.") with
            {
                Role = ModelRole.Reranker.ToString()
            };
        }

        var measuredRuns = Math.Max(1, spec.MeasuredRuns);
        var metricsUri = new Uri(endpoint.BaseAddress, "/metrics");
        var endpointUri = BuildRoleUri(endpoint.BaseAddress, "rerank");
        using var client = _httpClientFactory.CreateClient();

        for (var warmup = 0; warmup < Math.Max(0, spec.WarmupRuns); warmup++)
        {
            _ = await PostRerankAsync(client, endpointUri, spec.RerankerQuery, documents, ct).ConfigureAwait(false);
            await captureResources(ct).ConfigureAwait(false);
        }

        var baselineMetrics = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);
        var latencies = new List<double>(measuredRuns);
        IReadOnlyList<double>? baselineScores = null;
        IReadOnlyList<int>? baselineOrder = null;
        var allFinite = true;
        var deterministic = true;

        for (var run = 0; run < measuredRuns; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            var scores = await PostRerankAsync(client, endpointUri, spec.RerankerQuery, documents, ct).ConfigureAwait(false);
            stopwatch.Stop();
            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            await captureResources(ct).ConfigureAwait(false);

            if (scores.Count != documents.Count)
            {
                return InferenceBenchmarkMetrics.Failed("Reranker benchmark returned an invalid score count.") with
                {
                    Role = ModelRole.Reranker.ToString()
                };
            }

            allFinite &= scores.All(double.IsFinite);
            var order = scores.Select((score, index) => (score, index))
                              .OrderByDescending(item => item.score)
                              .ThenBy(item => item.index)
                              .Select(item => item.index)
                              .ToArray();
            if (baselineScores is null)
            {
                baselineScores = scores;
                baselineOrder = order;
            }
            else
            {
                deterministic &= ScoresEqual(baselineScores, scores, spec.DeterminismTolerance)
                                 && baselineOrder!.SequenceEqual(order);
            }
        }

        var afterMetrics = await ScrapeMetricsAsync(metricsUri, ct).ConfigureAwait(false);
        var totalSeconds = latencies.Sum() / 1000d;
        var success = allFinite && deterministic;

        return new InferenceBenchmarkMetrics(Success: success,
            FailureReason: success ? null : "Reranker benchmark output failed finite-score or deterministic-order checks.",
            TokensPerSecond: null,
            PpTokensPerSecond: DeriveRate(baselineMetrics, afterMetrics, PromptTokensMetric, PromptSecondsMetric),
            TtftMs: null,
            TotalLatencyMs: latencies.Sum(),
            CacheHitRate: null,
            ToolLoopMs: null,
            VramLoadBytes: null,
            VramAfterBytes: null,
            Runs: measuredRuns,
            RawJson: afterMetrics,
            Role: ModelRole.Reranker.ToString(),
            ItemsPerSecond: Throughput(documents.Count * measuredRuns, totalSeconds),
            InputTokensPerSecond: CounterThroughput(baselineMetrics, afterMetrics, PromptTokensMetric, totalSeconds),
            P50LatencyMs: Percentile(latencies, 0.50d),
            P95LatencyMs: Percentile(latencies, 0.95d),
            BatchSize: documents.Count,
            OutputDimension: null,
            ValuesFinite: allFinite,
            DeterministicOutput: deterministic);
    }

    private async Task<ResourceObservation> CaptureResourcesAsync(InferenceBenchmarkSpec spec,
        int? processId,
        CancellationToken ct)
    {
        var hardware = await _hardwareProfiler.GetProfileAsync(forceRefresh: true, ct).ConfigureAwait(false);
        var processBudget = await _processVramBudgetProbe.TryGetProcessBudgetBytesAsync(spec.Backend, ct).ConfigureAwait(false);
        var globalFree = string.Equals(spec.Backend, InferenceBackends.Cpu, StringComparison.OrdinalIgnoreCase)
            ? null
            : hardware.AvailableVramBytes;

        return new ResourceObservation(VramObservation.Create(globalFree,
                processBudget),
            TryGetWorkingSetBytes(processId));
    }

    private static InferenceBenchmarkMetrics ApplyResourceEvidence(InferenceBenchmarkMetrics metrics,
        LlamaServerProfilingContext context,
        ResourceEvidenceCollector resources)
    {
        var role = context.Endpoint.Role;
        var load = resources.First.Vram;
        var after = resources.Last.Vram;
        var externalPressure = resources.ExternalPressureDetected;
        var success = metrics.Success && !externalPressure;
        var failureReason = metrics.FailureReason;
        if (externalPressure)
        {
            failureReason = resources.PreSpawnVram?.ExternalPressureDetected == true
                ? metrics.FailureReason ?? BuildPreSpawnPressureFailureReason(resources.PreSpawnVram)
                : IncrementalPressureFailureReason;
        }

        var diagnostics = JsonSerializer.Serialize(new
            {
                role = role.ToString(),
                workload = new
                {
                    metrics.ItemsPerSecond,
                    metrics.InputTokensPerSecond,
                    metrics.P50LatencyMs,
                    metrics.P95LatencyMs,
                    metrics.BatchSize,
                    metrics.OutputDimension,
                    metrics.ValuesFinite,
                    metrics.DeterministicOutput
                },
                server = new
                {
                    metrics.RequestsProcessingAtLastScrape,
                    metrics.RequestsDeferredAtLastScrape,
                    metrics.ContextTokensHighWatermark,
                    metrics.AverageBusySlotsPerDecode,
                    metrics.SpeculativeDraftTokens,
                    metrics.SpeculativeAcceptedTokens,
                    metrics.SpeculativeVerificationSteps,
                    metrics.SpeculativeAcceptanceRate
                },
                vram = new
                {
                    preSpawn = resources.PreSpawnVram,
                    load,
                    after,
                    minimumGlobalFreeBytes = resources.MinimumGlobalFreeBytes,
                    minimumProcessBudgetBytes = resources.MinimumProcessBudgetBytes,
                    externalPressure
                },
                process = new
                {
                    peakWorkingSetBytes = resources.PeakWorkingSetBytes,
                    samples = resources.Samples.Count
                },
                runtime = new
                {
                    version = context.LoadObservation?.RuntimeVersion,
                    sha256 = context.LoadObservation?.RuntimeSha256,
                    launchArgumentsSha256 = HashSemanticLaunchArguments(context.SuccessfulLaunchArguments),
                    readinessDurationMs = context.LoadObservation?.ReadinessDurationMs,
                    outcome = context.LoadObservation?.Outcome.ToString(),
                    placement = context.LoadObservation?.Placement.ToString(),
                    attemptKind = context.LoadObservation?.AttemptKind.ToString(),
                    speculationClass = context.LoadObservation?.SpeculativeModeClass.ToString()
                }
            },
            SerializerOptions);

        return metrics with
        {
            Success = success,
            FailureReason = success ? null : failureReason,
            Role = role.ToString(),
            VramLoadBytes = load.GlobalFreeBytes ?? load.ProcessBudgetBytes,
            VramAfterBytes = after.GlobalFreeBytes ?? after.ProcessBudgetBytes,
            GlobalFreeVramLoadBytes = load.GlobalFreeBytes,
            GlobalFreeVramAfterBytes = after.GlobalFreeBytes,
            ProcessBudgetVramLoadBytes = load.ProcessBudgetBytes,
            ProcessBudgetVramAfterBytes = after.ProcessBudgetBytes,
            MinimumGlobalFreeVramBytes = resources.MinimumGlobalFreeBytes,
            MinimumProcessBudgetVramBytes = resources.MinimumProcessBudgetBytes,
            PeakProcessRamBytes = resources.PeakWorkingSetBytes,
            ExternalPressureDetected = externalPressure,
            DiagnosticsJson = diagnostics
        };
    }

    internal static string? HashSemanticLaunchArguments(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return null;
        }

        var semanticArguments = new List<string>(arguments.Count);
        var index = 0;
        while (index < arguments.Count)
        {
            if (arguments[index] is "-m" or "--model" or "--host" or "--port")
            {
                index += 2;
                continue;
            }

            semanticArguments.Add(arguments[index]);
            index++;
        }

        var serialized = JsonSerializer.Serialize(semanticArguments, SerializerOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
    }

    private static long? TryGetWorkingSetBytes(int? processId)
    {
        if (processId is not { } pid || pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Refresh();
            return process.HasExited ? null : process.WorkingSet64;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string BuildPreSpawnPressureFailureReason(VramObservation? observation)
    {
        if (observation is not { GlobalFreeBytes: { } globalFree, ProcessBudgetBytes: { } processBudget, PressureAboveBaselineBytes: { } pressureAboveBaseline })
        {
            return "Benchmark invalid: pre-spawn VRAM pressure exceeded the configured ambient allowance. Close other GPU workloads and retry.";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"Benchmark invalid: pre-spawn VRAM pressure exceeded the configured ambient allowance (global free {globalFree} bytes, process budget {processBudget} bytes, pressure above baseline {pressureAboveBaseline} bytes). Close other GPU workloads and retry, or use the explicit pre-spawn pressure override.");
    }

    private static async Task<IReadOnlyList<IReadOnlyList<double>>> PostEmbeddingAsync(HttpClient client,
        Uri endpoint,
        string modelName,
        IReadOnlyList<string> inputs,
        CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(endpoint, new EmbeddingRequest(modelName, inputs), SerializerOptions, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(SerializerOptions, ct).ConfigureAwait(false);
        if (payload?.Data is null || payload.Data.Count != inputs.Count)
        {
            throw new InvalidDataException("Embedding response did not contain one vector per input.");
        }

        var ordered = payload.Data.OrderBy(item => item.Index).ToArray();
        if (ordered.Select(item => item.Index).Where((index, position) => index != position).Any())
        {
            throw new InvalidDataException("Embedding response indices were incomplete or duplicated.");
        }

        return ordered.Select(item => item.Embedding).ToArray();
    }

    private static async Task<IReadOnlyList<double>> PostRerankAsync(HttpClient client,
        Uri endpoint,
        string query,
        IReadOnlyList<string> documents,
        CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(endpoint, new RerankRequest(query, documents), SerializerOptions, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<RerankResponse>(SerializerOptions, ct).ConfigureAwait(false);
        if (payload?.Results is null || payload.Results.Count != documents.Count)
        {
            throw new InvalidDataException("Reranker response did not contain one score per document.");
        }

        var scores = new double[documents.Count];
        var assigned = new bool[documents.Count];
        foreach (var result in payload.Results)
        {
            if (result.Index < 0 || result.Index >= documents.Count || assigned[result.Index])
            {
                throw new InvalidDataException("Reranker response indices were incomplete or duplicated.");
            }

            scores[result.Index] = result.RelevanceScore;
            assigned[result.Index] = true;
        }

        return scores;
    }

    private static Uri BuildRoleUri(Uri baseAddress, string route)
    {
        return new Uri($"{baseAddress.AbsoluteUri.TrimEnd('/')}/{route}");
    }

    private static bool VectorsEqual(IReadOnlyList<IReadOnlyList<double>> expected,
        IReadOnlyList<IReadOnlyList<double>> actual,
        double tolerance)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        for (var vectorIndex = 0; vectorIndex < expected.Count; vectorIndex++)
        {
            if (!ScoresEqual(expected[vectorIndex], actual[vectorIndex], tolerance))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ScoresEqual(IReadOnlyList<double> expected, IReadOnlyList<double> actual, double tolerance)
    {
        return expected.Count == actual.Count
               && expected.Select((value, index) => Math.Abs(value - actual[index]) <= tolerance).All(static equal => equal);
    }

    private static double? Throughput(int itemCount, double seconds)
    {
        return seconds > 0d ? itemCount / seconds : null;
    }

    private static double? CounterThroughput(string? before, string? after, string metric, double seconds)
    {
        var count = Delta(before, after, metric);
        return count is not null && seconds > 0d ? count / seconds : null;
    }

    private static double? Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var ordered = values.OrderBy(static value => value).ToArray();
        var index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static double? MedianNullable(IEnumerable<double?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return Percentile(present, 0.50d);
    }

    private static double? MaxNullableDouble(IEnumerable<double?> values)
    {
        var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return present.Length == 0 ? null : present.Max();
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

    private static async Task<StreamStageResult> StreamStageAsync(IChatClient chatClient,
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

        return new StreamStageResult(firstTokenMs ?? stopwatch.Elapsed.TotalMilliseconds, builder.ToString());
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

    /// <summary>
    ///     pp/tg tokens per second from two Prometheus scrapes. The harness never sees llama-server's per-request
    ///     <c>timings</c> object — it drives the chat client and reads the server's <c>/metrics</c> counters around the
    ///     call — so only the ARITHMETIC is shared with the benchmark executor's path, through
    ///     <see cref="TokenThroughput" />; there is no common input to share above it. The counters publish seconds,
    ///     not milliseconds, which is the one thing that differs between the two derivations.
    /// </summary>
    private static double? DeriveRate(string? before, string? after, string tokensMetric, string secondsMetric) =>
        TokenThroughput.FromSeconds(Delta(before, after, tokensMetric), Delta(before, after, secondsMetric));

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
        return delta >= 0 ? delta : afterValue;
    }

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")]
        string Model,
        [property: JsonPropertyName("input")]
        IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")]
        IReadOnlyList<EmbeddingResult>? Data);

    private sealed record EmbeddingResult(
        [property: JsonPropertyName("index")]
        int Index,
        [property: JsonPropertyName("embedding")]
        IReadOnlyList<double> Embedding);

    private sealed record RerankRequest(
        [property: JsonPropertyName("query")]
        string Query,
        [property: JsonPropertyName("documents")]
        IReadOnlyList<string> Documents);

    private sealed record RerankResponse(
        [property: JsonPropertyName("results")]
        IReadOnlyList<RerankResult>? Results);

    private sealed record RerankResult(
        [property: JsonPropertyName("index")]
        int Index,
        [property: JsonPropertyName("relevance_score")]
        double RelevanceScore);

    private sealed record VramObservation(
        long? GlobalFreeBytes,
        long? ProcessBudgetBytes,
        long? ProcessBudgetExcessBytes,
        double? ProcessBudgetExcessRatio,
        long? PressureAboveBaselineBytes,
        double? PressureAboveBaselineRatio,
        bool ExternalPressureDetected)
    {
        public static VramObservation Create(long? globalFreeBytes,
            long? processBudgetBytes)
        {
            if (globalFreeBytes is not { } global || processBudgetBytes is not { } process || process <= global)
            {
                return new VramObservation(globalFreeBytes,
                    processBudgetBytes,
                    ProcessBudgetExcessBytes: null,
                    ProcessBudgetExcessRatio: null,
                    PressureAboveBaselineBytes: null,
                    PressureAboveBaselineRatio: null,
                    ExternalPressureDetected: false);
            }

            var excess = process - global;
            var ratio = process > 0 ? (double)excess / process : 0d;
            return new VramObservation(globalFreeBytes,
                processBudgetBytes,
                excess,
                ratio,
                PressureAboveBaselineBytes: null,
                PressureAboveBaselineRatio: null,
                ExternalPressureDetected: false);
        }
    }

    private sealed record ResourceObservation(VramObservation Vram, long? WorkingSetBytes);

    private sealed class ResourceEvidenceCollector
    {
        private readonly long _incrementalAbsoluteThresholdBytes;
        private readonly double _incrementalRatioThreshold;
        private readonly long _preSpawnAmbientBaselineBytes;
        private readonly long _preSpawnPressureAbsoluteThresholdBytes;
        private readonly double _preSpawnPressureRatioThreshold;
        private readonly bool _rejectPreSpawnVramPressure;
        private readonly List<ResourceObservation> _samples = [];

        public ResourceEvidenceCollector(LlamaServerProfilingVramSnapshot? preSpawnVram,
            long preSpawnAmbientBaselineBytes,
            long preSpawnPressureAbsoluteThresholdBytes,
            double preSpawnPressureRatioThreshold,
            bool rejectPreSpawnVramPressure,
            long incrementalAbsoluteThresholdBytes,
            double incrementalRatioThreshold)
        {
            _preSpawnAmbientBaselineBytes = Math.Max(0, preSpawnAmbientBaselineBytes);
            _preSpawnPressureAbsoluteThresholdBytes = Math.Max(0, preSpawnPressureAbsoluteThresholdBytes);
            _preSpawnPressureRatioThreshold = Math.Max(0d, preSpawnPressureRatioThreshold);
            _rejectPreSpawnVramPressure = rejectPreSpawnVramPressure;
            _incrementalAbsoluteThresholdBytes = Math.Max(0, incrementalAbsoluteThresholdBytes);
            _incrementalRatioThreshold = Math.Max(0d, incrementalRatioThreshold);
            PreSpawnVram = preSpawnVram is null
                ? null
                : ClassifyPreSpawnPressure(VramObservation.Create(preSpawnVram.GlobalFreeBytes, preSpawnVram.ProcessBudgetBytes));
        }

        public IReadOnlyList<ResourceObservation> Samples => _samples;

        public VramObservation? PreSpawnVram { get; }

        public bool ExternalPressureDetected =>
            PreSpawnVram?.ExternalPressureDetected == true
            || _samples.Any(static sample => sample.Vram.ExternalPressureDetected);

        public ResourceObservation First => _samples[0];

        public ResourceObservation Last => _samples[^1];

        public long? PeakWorkingSetBytes => MaxNullable(_samples.Select(static sample => sample.WorkingSetBytes));

        public long? MinimumGlobalFreeBytes => MinNullable(_samples.Select(static sample => sample.Vram.GlobalFreeBytes));

        public long? MinimumProcessBudgetBytes => MinNullable(_samples.Select(static sample => sample.Vram.ProcessBudgetBytes));

        public void Add(ResourceObservation sample)
        {
            // Pre-existing pressure is decided from the pre-spawn sample. Once the profiling server is resident, its own
            // VRAM becomes a stable gap between the global-free and per-process-budget readers. Only growth beyond that
            // post-load gap indicates pressure introduced during measurement.
            if (_samples.Count > 0
                && _samples[0].Vram.GlobalFreeBytes is not null
                && _samples[0].Vram.ProcessBudgetBytes is not null
                && sample.Vram.ProcessBudgetExcessBytes is { } currentExcess
                && sample.Vram.ProcessBudgetBytes is > 0)
            {
                var baselineExcess = _samples[0].Vram.ProcessBudgetExcessBytes ?? 0L;
                var additionalExcess = Math.Max(0L, currentExcess - baselineExcess);
                var additionalRatio = (double)additionalExcess / sample.Vram.ProcessBudgetBytes.Value;
                var material = additionalExcess >= _incrementalAbsoluteThresholdBytes
                               && additionalRatio >= _incrementalRatioThreshold;
                sample = sample with
                {
                    Vram = sample.Vram with
                    {
                        PressureAboveBaselineBytes = additionalExcess,
                        PressureAboveBaselineRatio = additionalRatio,
                        ExternalPressureDetected = material
                    }
                };
            }

            _samples.Add(sample);
        }

        private VramObservation ClassifyPreSpawnPressure(VramObservation observation)
        {
            if (observation.ProcessBudgetExcessBytes is not { } rawExcess
                || observation.ProcessBudgetBytes is not > 0)
            {
                return observation;
            }

            var pressureAboveBaseline = Math.Max(0L, rawExcess - _preSpawnAmbientBaselineBytes);
            var pressureAboveBaselineRatio = (double)pressureAboveBaseline / observation.ProcessBudgetBytes.Value;
            var material = _rejectPreSpawnVramPressure
                           && pressureAboveBaseline >= _preSpawnPressureAbsoluteThresholdBytes
                           && pressureAboveBaselineRatio >= _preSpawnPressureRatioThreshold;
            return observation with
            {
                PressureAboveBaselineBytes = pressureAboveBaseline,
                PressureAboveBaselineRatio = pressureAboveBaselineRatio,
                ExternalPressureDetected = material
            };
        }

        private static long? MaxNullable(IEnumerable<long?> values)
        {
            var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
            return present.Length == 0 ? null : present.Max();
        }

        private static long? MinNullable(IEnumerable<long?> values)
        {
            var present = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
            return present.Length == 0 ? null : present.Min();
        }
    }

    private sealed record ChatPassMetrics(
        double? TokensPerSecond,
        double? PpTokensPerSecond,
        double TtftMs,
        double TotalLatencyMs,
        double? CacheHitRate,
        double? ToolLoopMs,
        string? RawMetrics,
        double? RequestsProcessingAtLastScrape,
        double? RequestsDeferredAtLastScrape,
        double? ContextTokensHighWatermark,
        double? AverageBusySlotsPerDecode,
        double? SpeculativeDraftTokens,
        double? SpeculativeAcceptedTokens,
        double? SpeculativeVerificationSteps,
        double? SpeculativeAcceptanceRate);

    // One streamed stage of the benchmark: the time to the first update, and the full text the stage produced.
    private sealed record StreamStageResult(double TtftMs, string Text);
}
