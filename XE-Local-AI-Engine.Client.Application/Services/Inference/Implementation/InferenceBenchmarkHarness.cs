namespace XE_Local_AI_Engine.Client.Services.Inference.Implementation;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Role-aware inference benchmark harness: chat retains the fixed golden transcript, while embedding and reranker
///     use their valid llama-server endpoints with warm-up, repeated measurements, output correctness checks and
///     median/p95 latency.
/// </summary>
/// <remarks>
///     Every run records global-free VRAM separately from llama.cpp's process-local budget and rejects material
///     divergence both before the profiling server starts and when it grows during measurement, so WDDM contention
///     cannot produce silently paged performance numbers.
/// </remarks>
public sealed class InferenceBenchmarkHarness : IInferenceBenchmarkHarness
{
    private const string IncrementalPressureFailureReason =
        "Benchmark invalid: VRAM divergence grew materially during measurement. Close other GPU workloads and retry.";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IInferenceChatClientFactory _chatClientFactory;
    private readonly ILogger<InferenceBenchmarkHarness> _logger;
    private readonly ILlamaServerNativeClient _nativeClient;
    private readonly InferenceBenchmarkResourceSampler _resourceSampler;

    public InferenceBenchmarkHarness(IInferenceChatClientFactory chatClientFactory,
        ILlamaServerNativeClient nativeClient,
        IHardwareProfiler hardwareProfiler,
        IProcessVramBudgetProbe processVramBudgetProbe,
        ILogger<InferenceBenchmarkHarness> logger)
    {
        ArgumentNullException.ThrowIfNull(chatClientFactory);
        ArgumentNullException.ThrowIfNull(nativeClient);
        ArgumentNullException.ThrowIfNull(hardwareProfiler);
        ArgumentNullException.ThrowIfNull(processVramBudgetProbe);
        ArgumentNullException.ThrowIfNull(logger);

        _chatClientFactory = chatClientFactory;
        _nativeClient = nativeClient;
        _resourceSampler = new InferenceBenchmarkResourceSampler(hardwareProfiler, processVramBudgetProbe);
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
            var load = await _resourceSampler.CaptureAsync(spec, context.ProcessId, ct);
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
                resources.Add(await _resourceSampler.CaptureAsync(spec, context.ProcessId, innerCt));
            }

            var metrics = role switch
            {
                ModelRole.Chat => await RunChatAsync(context.Endpoint, spec, CapturePassResourcesAsync, ct),
                ModelRole.Embedding => await RunEmbeddingAsync(context.Endpoint, spec, CapturePassResourcesAsync, ct),
                ModelRole.Reranker => await RunRerankerAsync(context.Endpoint, spec, CapturePassResourcesAsync, ct),
                _ => InferenceBenchmarkMetrics.Failed($"Benchmark role '{role}' is unsupported.")
            };

            resources.Add(await _resourceSampler.CaptureAsync(spec, context.ProcessId, ct));
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

    private async Task<InferenceBenchmarkMetrics> RunChatAsync(LlamaServerEndpoint endpoint,
        InferenceBenchmarkSpec spec,
        Func<CancellationToken, Task> captureResources,
        CancellationToken ct)
    {
        for (var warmup = 0; warmup < Math.Max(0, spec.WarmupRuns); warmup++)
        {
            _ = await RunChatPassAsync(endpoint, spec, ct);
            await captureResources(ct);
        }

        var measuredRuns = Math.Max(1, spec.MeasuredRuns);
        var passes = new List<ChatPassMetrics>(measuredRuns);
        for (var run = 0; run < measuredRuns; run++)
        {
            passes.Add(await RunChatPassAsync(endpoint, spec, ct));
            await captureResources(ct);
        }

        return new InferenceBenchmarkMetrics
        {
            Success = true,
            FailureReason = null,
            TokensPerSecond = MedianNullable(passes.Select(static pass => pass.TokensPerSecond)),
            PpTokensPerSecond = MedianNullable(passes.Select(static pass => pass.PpTokensPerSecond)),
            TtftMs = MedianNullable(passes.Select(static pass => (double?)pass.TtftMs)),
            TotalLatencyMs = Percentile(passes.Select(static pass => pass.TotalLatencyMs).ToArray(), 0.50d),
            CacheHitRate = MedianNullable(passes.Select(static pass => pass.CacheHitRate)),
            ToolLoopMs = MedianNullable(passes.Select(static pass => pass.ToolLoopMs)),
            VramLoadBytes = null,
            VramAfterBytes = null,
            Runs = measuredRuns,
            RawJson = passes[^1].RawMetrics,
            Role = ModelRole.Chat.ToString(),
            P50LatencyMs = Percentile(passes.Select(static pass => pass.TotalLatencyMs).ToArray(), 0.50d),
            P95LatencyMs = Percentile(passes.Select(static pass => pass.TotalLatencyMs).ToArray(), 0.95d),
            RequestsProcessingAtLastScrape = passes[^1].RequestsProcessingAtLastScrape,
            RequestsDeferredAtLastScrape = passes[^1].RequestsDeferredAtLastScrape,
            ContextTokensHighWatermark = MaxNullableDouble(passes.Select(static pass => pass.ContextTokensHighWatermark)),
            AverageBusySlotsPerDecode = MedianNullable(passes.Select(static pass => pass.AverageBusySlotsPerDecode)),
            WarmPromptTimings = passes.Select(static pass => pass.WarmPromptTimings).ToArray()
        };
    }

    private async Task<ChatPassMetrics> RunChatPassAsync(LlamaServerEndpoint endpoint,
        InferenceBenchmarkSpec spec,
        CancellationToken ct)
    {
        // Every stopwatch below starts after this wrap, so the metadata-only span hop (Activity start/stop plus MEAI's
        // span-end response materialization, microseconds against an LLM-scale round) is inside the measured region on both paths.
        using var chatClient = _chatClientFactory.CreateChatClient(endpoint.BaseAddress, endpoint.ModelName).WithProviderTelemetry();

        var totalStopwatch = Stopwatch.StartNew();
        var baseline = await ScrapeMetricsAsync(endpoint.BaseAddress, ct);
        var chatOptions = BuildOptions(endpoint.ModelName, spec, tools: null);

        var coldMessages = new List<ChatMessage>
        {
            new(ChatRole.System, spec.SystemPersona),
            new(ChatRole.User, spec.ColdUserTurn)
        };
        var coldStage = await StreamStageAsync(chatClient, coldMessages, chatOptions, ct);

        var warmMessages = new List<ChatMessage>(coldMessages)
        {
            new(ChatRole.Assistant, coldStage.Text),
            new(ChatRole.User, spec.WarmFollowUpTurn)
        };
        var warmStage = await StreamStageAsync(chatClient, warmMessages, chatOptions, ct);

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
        using (var toolInnerClient = _chatClientFactory.CreateChatClient(endpoint.BaseAddress, endpoint.ModelName).WithProviderTelemetry())
            using (var toolInvokingClient = toolInnerClient.AsBuilder().UseFunctionInvocation().Build())
            {
                var toolStopwatch = Stopwatch.StartNew();
                _ = await toolInvokingClient.GetResponseAsync(toolMessages, toolOptions, ct);
                toolStopwatch.Stop();
                toolLoopMs = Volatile.Read(ref toolInvocations) > 0 ? toolStopwatch.Elapsed.TotalMilliseconds : null;
            }

        var longMessages = new List<ChatMessage>
        {
            new(ChatRole.System, spec.SystemPersona),
            new(ChatRole.User, spec.LongContextUserTurn)
        };
        _ = await chatClient.GetResponseAsync(longMessages, chatOptions, ct);
        var afterAll = await ScrapeMetricsAsync(endpoint.BaseAddress, ct);
        totalStopwatch.Stop();

        return new ChatPassMetrics
        {
            TokensPerSecond = DeriveRate(baseline, afterAll, LlamaServerMetrics.TokensPredictedTotal, LlamaServerMetrics.TokensPredictedSecondsTotal),
            PpTokensPerSecond = DeriveRate(baseline, afterAll, LlamaServerMetrics.PromptTokensTotal, LlamaServerMetrics.PromptSecondsTotal),
            TtftMs = coldStage.TtftMs,
            TotalLatencyMs = totalStopwatch.Elapsed.TotalMilliseconds,
            CacheHitRate = DeriveCacheHitRate(warmStage.Timings),
            ToolLoopMs = toolLoopMs,
            RawMetrics = afterAll,
            RequestsProcessingAtLastScrape = LlamaServerMetrics.TryParse(afterAll, LlamaServerMetrics.RequestsProcessing),
            RequestsDeferredAtLastScrape = LlamaServerMetrics.TryParse(afterAll, LlamaServerMetrics.RequestsDeferred),
            ContextTokensHighWatermark = LlamaServerMetrics.TryParse(afterAll, LlamaServerMetrics.ContextTokensHighWatermark),
            AverageBusySlotsPerDecode = LlamaServerMetrics.TryParse(afterAll, LlamaServerMetrics.BusySlotsPerDecode),
            WarmPromptTimings = warmStage.Timings
        };
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

        for (var warmup = 0; warmup < Math.Max(0, spec.WarmupRuns); warmup++)
        {
            _ = await _nativeClient.PostEmbeddingsAsync(endpoint.BaseAddress, endpoint.ModelName, inputs, ct);
            await captureResources(ct);
        }

        var baselineMetrics = await ScrapeMetricsAsync(endpoint.BaseAddress, ct);
        var latencies = new List<double>(measuredRuns);
        IReadOnlyList<IReadOnlyList<double>>? baselineVectors = null;
        var allFinite = true;
        var deterministic = true;
        int? dimensions = null;

        for (var run = 0; run < measuredRuns; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            var vectors = await _nativeClient.PostEmbeddingsAsync(endpoint.BaseAddress, endpoint.ModelName, inputs, ct);
            stopwatch.Stop();
            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            await captureResources(ct);

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

        var afterMetrics = await ScrapeMetricsAsync(endpoint.BaseAddress, ct);
        var totalSeconds = latencies.Sum() / 1000d;
        var success = allFinite && deterministic;

        return new InferenceBenchmarkMetrics
        {
            Success = success,
            FailureReason = success ? null : "Embedding benchmark output failed finite-value or deterministic-equivalence checks.",
            TokensPerSecond = null,
            PpTokensPerSecond = DeriveRate(baselineMetrics, afterMetrics, LlamaServerMetrics.PromptTokensTotal, LlamaServerMetrics.PromptSecondsTotal),
            TtftMs = null,
            TotalLatencyMs = latencies.Sum(),
            CacheHitRate = null,
            ToolLoopMs = null,
            VramLoadBytes = null,
            VramAfterBytes = null,
            Runs = measuredRuns,
            RawJson = afterMetrics,
            Role = ModelRole.Embedding.ToString(),
            ItemsPerSecond = Throughput(inputs.Count * measuredRuns, totalSeconds),
            InputTokensPerSecond = CounterThroughput(baselineMetrics, afterMetrics, LlamaServerMetrics.PromptTokensTotal, totalSeconds),
            P50LatencyMs = Percentile(latencies, 0.50d),
            P95LatencyMs = Percentile(latencies, 0.95d),
            BatchSize = inputs.Count,
            OutputDimension = dimensions,
            ValuesFinite = allFinite,
            DeterministicOutput = deterministic
        };
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

        for (var warmup = 0; warmup < Math.Max(0, spec.WarmupRuns); warmup++)
        {
            _ = await _nativeClient.PostRerankAsync(endpoint.BaseAddress, spec.RerankerQuery, documents, ct);
            await captureResources(ct);
        }

        var baselineMetrics = await ScrapeMetricsAsync(endpoint.BaseAddress, ct);
        var latencies = new List<double>(measuredRuns);
        IReadOnlyList<double>? baselineScores = null;
        IReadOnlyList<int>? baselineOrder = null;
        var allFinite = true;
        var deterministic = true;

        for (var run = 0; run < measuredRuns; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            var scores = await _nativeClient.PostRerankAsync(endpoint.BaseAddress, spec.RerankerQuery, documents, ct);
            stopwatch.Stop();
            latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            await captureResources(ct);

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

        var afterMetrics = await ScrapeMetricsAsync(endpoint.BaseAddress, ct);
        var totalSeconds = latencies.Sum() / 1000d;
        var success = allFinite && deterministic;

        return new InferenceBenchmarkMetrics
        {
            Success = success,
            FailureReason = success ? null : "Reranker benchmark output failed finite-score or deterministic-order checks.",
            TokensPerSecond = null,
            PpTokensPerSecond = DeriveRate(baselineMetrics, afterMetrics, LlamaServerMetrics.PromptTokensTotal, LlamaServerMetrics.PromptSecondsTotal),
            TtftMs = null,
            TotalLatencyMs = latencies.Sum(),
            CacheHitRate = null,
            ToolLoopMs = null,
            VramLoadBytes = null,
            VramAfterBytes = null,
            Runs = measuredRuns,
            RawJson = afterMetrics,
            Role = ModelRole.Reranker.ToString(),
            ItemsPerSecond = Throughput(documents.Count * measuredRuns, totalSeconds),
            InputTokensPerSecond = CounterThroughput(baselineMetrics, afterMetrics, LlamaServerMetrics.PromptTokensTotal, totalSeconds),
            P50LatencyMs = Percentile(latencies, 0.50d),
            P95LatencyMs = Percentile(latencies, 0.95d),
            BatchSize = documents.Count,
            OutputDimension = null,
            ValuesFinite = allFinite,
            DeterministicOutput = deterministic
        };
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

        var warmPromptTimings = metrics.WarmPromptTimings;
        var cacheEvidence = warmPromptTimings is null
            ? null
            : new
            {
                method = "warm-timings-v1",
                samples = warmPromptTimings.Select(static timings => timings is null
                    ? null
                    : new
                    {
                        cachedPromptTokens = timings.CachedPromptTokens,
                        evaluatedPromptTokens = timings.PromptTokens
                    }).ToArray(),
                validSamples = warmPromptTimings.Count(static timings => DeriveCacheHitRate(timings) is not null),
                totalSamples = warmPromptTimings.Count
            };

        var diagnostics = JsonSerializer.Serialize(new
            {
                role = role.ToString(),
                cacheEvidence,
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
                    metrics.AverageBusySlotsPerDecode
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
            if (arguments[index] is "-m" or "--model" or "-a" or "--alias" or "--host" or "--port")
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

    private static string BuildPreSpawnPressureFailureReason(VramObservation? observation)
    {
        if (observation is not { GlobalFreeBytes: { } globalFree, ProcessBudgetBytes: { } processBudget, PressureAboveBaselineBytes: { } pressureAboveBaseline })
        {
            return "Benchmark invalid: pre-spawn VRAM pressure exceeded the configured ambient allowance. Close other GPU workloads and retry.";
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"Benchmark invalid: pre-spawn VRAM pressure exceeded the configured ambient allowance (global free {Gib(globalFree)}, process budget {Gib(processBudget)}, pressure above baseline {Gib(pressureAboveBaseline)}). Close other GPU workloads and retry, or use the explicit pre-spawn pressure override.");
    }

    private static string Gib(long bytes) =>
        string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024 * 1024):0.0} GiB");

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
        LlamaServerGenerationTimings? timings = null;

        await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, ct))
        {
            firstTokenMs ??= stopwatch.Elapsed.TotalMilliseconds;
            builder.Append(update.Text);
            timings = LlamaServerGenerationTimings.TryRead(update.RawRepresentation) ?? timings;
        }

        return new StreamStageResult
        {
            TtftMs = firstTokenMs ?? stopwatch.Elapsed.TotalMilliseconds,
            Text = builder.ToString(),
            Timings = timings
        };
    }

    private async Task<string?> ScrapeMetricsAsync(Uri baseAddress, CancellationToken ct)
    {
        try
        {
            return await _nativeClient.GetMetricsTextAsync(baseAddress, ct);
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

    /// <summary>Aggregate pp/tg tokens per second from two Prometheus scrapes.</summary>
    /// <remarks>
    ///     Cache evidence uses per-request timings, while throughput continues to span the whole benchmark pass
    ///     through cumulative counters. Only the arithmetic is shared with the benchmark executor's path through
    ///     <see cref="TokenThroughput" />; these counters publish seconds rather than milliseconds.
    /// </remarks>
    private static double? DeriveRate(string? before, string? after, string tokensMetric, string secondsMetric) =>
        TokenThroughput.FromSeconds(Delta(before, after, tokensMetric), Delta(before, after, secondsMetric));

    private static double? DeriveCacheHitRate(LlamaServerGenerationTimings? timings)
    {
        // Defence in depth if the timing reader's nonnegative validation changes.
        if (timings?.CachedPromptTokens is not { } cached
            || timings.PromptTokens is not { } evaluated
            || cached < 0
            || evaluated < 0)
        {
            return null;
        }

        var total = (double)cached + evaluated;
        return total > 0d ? cached / total : null;
    }

    private static double? Delta(string? before, string? after, string metric)
    {
        var afterValue = LlamaServerMetrics.TryParse(after, metric);
        if (afterValue is null)
        {
            return null;
        }

        var beforeValue = LlamaServerMetrics.TryParse(before, metric) ?? 0d;
        var delta = afterValue.Value - beforeValue;
        return delta >= 0 ? delta : afterValue;
    }

    private sealed record ChatPassMetrics
    {
        public required double? TokensPerSecond { get; init; }

        public required double? PpTokensPerSecond { get; init; }

        public required double TtftMs { get; init; }

        public required double TotalLatencyMs { get; init; }

        public required double? CacheHitRate { get; init; }

        public required double? ToolLoopMs { get; init; }

        public required string? RawMetrics { get; init; }

        public required double? RequestsProcessingAtLastScrape { get; init; }

        public required double? RequestsDeferredAtLastScrape { get; init; }

        public required double? ContextTokensHighWatermark { get; init; }

        public required double? AverageBusySlotsPerDecode { get; init; }

        public required LlamaServerGenerationTimings? WarmPromptTimings { get; init; }
    }

    // One streamed stage of the benchmark: the time to the first update, full text, and latest timing snapshot.
    private sealed record StreamStageResult
    {
        public required double TtftMs { get; init; }

        public required string Text { get; init; }

        public required LlamaServerGenerationTimings? Timings { get; init; }
    }
}
