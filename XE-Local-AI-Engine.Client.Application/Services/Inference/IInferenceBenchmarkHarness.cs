namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Text;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Drives a FIXED golden transcript against a transient profiling llama-server process and captures the
///     comparable throughput/latency/cache/VRAM metrics for one inference profile.
/// </summary>
/// <remarks>The transcript and sampling are fixed, so two profiles benchmarked on the same box are directly comparable.</remarks>
public interface IInferenceBenchmarkHarness
{
    /// <summary>Runs the golden transcript against the <paramref name="context" /> endpoint and returns its measured metrics.</summary>
    Task<InferenceBenchmarkMetrics> RunAsync(LlamaServerProfilingContext context, InferenceBenchmarkSpec spec, CancellationToken ct);
}

/// <summary>A deterministic mock tool the tool-call stage offers the model; the result is fixed so the round is reproducible.</summary>
public sealed record InferenceBenchmarkToolDefinition
{
    /// <summary>Tool function name.</summary>
    public required string Name { get; init; }

    /// <summary>Tool description the model sees.</summary>
    public required string Description { get; init; }

    /// <summary>The fixed result returned whenever the model invokes the tool.</summary>
    public required string DeterministicResult { get; init; }
}

/// <summary>
///     Operator-configurable VRAM-admission thresholds for inference benchmarks. Bound from
///     <c>InferenceBenchmark:VramAdmission</c>; request-level pressure bypass remains explicit and defaults off.
/// </summary>
public sealed class InferenceBenchmarkVramAdmissionOptions
{
    public const string SectionName = "InferenceBenchmark:VramAdmission";

    public long PreSpawnAmbientBaselineBytes { get; set; } = 1024L * 1024 * 1024;

    public long PreSpawnPressureAbsoluteThresholdBytes { get; set; } = 512L * 1024 * 1024;

    public double PreSpawnPressureRatioThreshold { get; set; } = 0.05d;

    public long IncrementalPressureAbsoluteThresholdBytes { get; set; } = 512L * 1024 * 1024;

    public double IncrementalPressureRatioThreshold { get; set; } = 0.05d;
}

/// <summary>
///     The fixed golden transcript + sampling for a benchmark run. Built per-profile by
///     <see cref="Golden" /> so the long-context stage is sized near the profile's context window.
/// </summary>
public sealed record InferenceBenchmarkSpec
{
    /// <summary>The lowercase backend token (<c>cuda</c>/<c>vulkan</c>/<c>cpu</c>) for the host-VRAM probe.</summary>
    public required string Backend { get; init; }

    /// <summary>The profile's context size; drives the long-context injection length.</summary>
    public required int CtxSize { get; init; }

    /// <summary>The fixed system persona for every stage.</summary>
    public required string SystemPersona { get; init; }

    /// <summary>The first user turn (cold cache).</summary>
    public required string ColdUserTurn { get; init; }

    /// <summary>The follow-up user turn that reuses the cold context (warm cache).</summary>
    public required string WarmFollowUpTurn { get; init; }

    /// <summary>The user turn that should trigger the mock tool.</summary>
    public required string ToolUserTurn { get; init; }

    /// <summary>The deterministic mock tool offered in the tool-call stage.</summary>
    public required InferenceBenchmarkToolDefinition Tool { get; init; }

    /// <summary>A long user turn sized near <see cref="CtxSize" /> to exercise long-context handling.</summary>
    public required string LongContextUserTurn { get; init; }

    /// <summary>Fixed RNG seed for reproducibility.</summary>
    public required int Seed { get; init; }

    /// <summary>Fixed sampling temperature (0 = greedy/deterministic).</summary>
    public required float Temperature { get; init; }

    /// <summary>Untimed role-specific warm-up passes before measurements begin.</summary>
    public int WarmupRuns { get; init; } = 1;

    /// <summary>Repeated measured passes used for median/p95 and stability checks.</summary>
    public int MeasuredRuns { get; init; } = 5;

    /// <summary>Fixed embedding batch used by the embedding-role benchmark.</summary>
    public IReadOnlyList<string> EmbeddingInputs { get; init; } =
    [
        "Local inference keeps private data on the operator's machine.",
        "Vector search maps semantically related passages near one another.",
        "A deterministic benchmark repeats the same corpus for every profile.",
        "GPU memory pressure can invalidate otherwise fast benchmark results.",
        "Embedding dimensions must stay stable across repeated requests.",
        "Finite vectors are required before a benchmark can justify a profile.",
        "Warm-up passes are excluded from the measured latency distribution.",
        "Median and p95 describe typical and tail request latency."
    ];

    /// <summary>Fixed reranker query used by the reranker-role benchmark.</summary>
    public string RerankerQuery { get; init; } = "Which passage explains local private inference?";

    /// <summary>Fixed reranker candidate batch used by the reranker-role benchmark.</summary>
    public IReadOnlyList<string> RerankerDocuments { get; init; } =
    [
        "Local inference processes prompts on the operator's own machine.",
        "A tropical storm forms over warm ocean water.",
        "Private retrieval can avoid sending documents to a cloud service.",
        "The benchmark records median and tail latency."
    ];

    /// <summary>Maximum absolute per-element drift accepted for repeated embedding vectors and reranker scores.</summary>
    public double DeterminismTolerance { get; init; } = 1e-5;

    /// <summary>
    ///     Expected idle process-budget/global-free offset on WDDM. The dev-box clean baseline is approximately 950 MiB;
    ///     rounding the allowance to 1 GiB keeps that platform offset separate from external-pressure growth.
    /// </summary>
    public long PreSpawnVramAmbientBaselineBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>
    ///     Minimum absolute divergence above <see cref="PreSpawnVramAmbientBaselineBytes" /> that can reject a benchmark.
    ///     The relative threshold below must also be exceeded.
    /// </summary>
    public long PreSpawnVramPressureAbsoluteThresholdBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Minimum pre-spawn divergence-above-baseline ratio that can reject a benchmark.</summary>
    public double PreSpawnVramPressureRatioThreshold { get; init; } = 0.05d;

    /// <summary>
    ///     Whether material pre-spawn pressure rejects before workload. Tests and diagnostic callers can disable this
    ///     explicitly; production profiling keeps the fail-closed default.
    /// </summary>
    public bool RejectPreSpawnVramPressure { get; init; } = true;

    /// <summary>
    ///     Minimum absolute growth beyond the post-load divergence baseline that can invalidate a benchmark. The relative
    ///     threshold below must also be exceeded, avoiding false pressure evidence from small driver-reporting jitter.
    /// </summary>
    public long IncrementalVramDivergenceAbsoluteThresholdBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Minimum post-load divergence-growth ratio that can invalidate a benchmark.</summary>
    public double IncrementalVramDivergenceRatioThreshold { get; init; } = 0.05d;

    /// <summary>Builds the canonical golden transcript for <paramref name="backend" />, with the long-context stage sized to ~75% of <paramref name="ctxSize" />.</summary>
    public static InferenceBenchmarkSpec Golden(string backend,
        int ctxSize,
        InferenceBenchmarkVramAdmissionOptions? vramAdmission = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);

        var safeCtx = ctxSize > 0 ? ctxSize : 4096;
        var admission = vramAdmission ?? new InferenceBenchmarkVramAdmissionOptions();

        return new InferenceBenchmarkSpec
        {
            Backend = backend,
            CtxSize = safeCtx,
            SystemPersona = "You are a concise benchmarking assistant. Answer briefly and deterministically.",
            ColdUserTurn = "List three primary colors, comma separated.",
            WarmFollowUpTurn = "Now list three secondary colors, comma separated.",
            ToolUserTurn = "What is the current bench status? Call the bench_status tool to find out.",
            Tool = new InferenceBenchmarkToolDefinition
            {
                Name = "bench_status",
                Description = "Returns a fixed benchmark status payload for the deterministic tool-call round.",
                DeterministicResult = "{\"status\":\"ok\",\"phase\":\"benchmark\"}"
            },
            LongContextUserTurn = BuildLongContextTurn(safeCtx),
            Seed = 0,
            Temperature = 0f,
            PreSpawnVramAmbientBaselineBytes = Math.Max(0, admission.PreSpawnAmbientBaselineBytes),
            PreSpawnVramPressureAbsoluteThresholdBytes = Math.Max(0, admission.PreSpawnPressureAbsoluteThresholdBytes),
            PreSpawnVramPressureRatioThreshold = Math.Max(0d, admission.PreSpawnPressureRatioThreshold),
            IncrementalVramDivergenceAbsoluteThresholdBytes = Math.Max(0, admission.IncrementalPressureAbsoluteThresholdBytes),
            IncrementalVramDivergenceRatioThreshold = Math.Max(0d, admission.IncrementalPressureRatioThreshold)
        };
    }

    // A repeated, deterministic filler sized to roughly 75% of the context window. ~4 characters per token is the usual
    // rough heuristic, so target-tokens * 4 characters approximates the intended fill without tokenizing here.
    private static string BuildLongContextTurn(int ctxSize)
    {
        const string sentence = "The quick brown fox jumps over the lazy dog. ";
        var targetTokens = Math.Max(64, ctxSize * 3 / 4);
        var targetChars = targetTokens * 4;

        var builder = new StringBuilder(targetChars + sentence.Length);
        builder.Append("Summarize the following passage in one sentence: ");
        while (builder.Length < targetChars)
        {
            builder.Append(sentence);
        }

        return builder.ToString();
    }
}

/// <summary>
///     The measured outcome of one role-specific benchmark run: chat keeps TG/PP/cache/tool metrics; embedding and
///     reranker add item throughput, latency distribution, batch shape and output-correctness evidence.
/// </summary>
/// <remarks>
///     Global-free VRAM and llama.cpp's process-local budget are recorded separately so WDDM pressure cannot
///     masquerade as a valid run. Any figure that could not be derived is <see langword="null" />.
/// </remarks>
public sealed record InferenceBenchmarkMetrics
{
    /// <summary>Whether the run completed; <see langword="false" /> blocks the freeze gate.</summary>
    public required bool Success { get; init; }

    /// <summary>Sanitized failure reason when <see cref="Success" /> is false.</summary>
    public required string? FailureReason { get; init; }

    /// <summary>Token-generation throughput (TG tok/s) from <c>/metrics</c>.</summary>
    public required double? TokensPerSecond { get; init; }

    /// <summary>Prompt-processing throughput (PP tok/s) from <c>/metrics</c>.</summary>
    public required double? PpTokensPerSecond { get; init; }

    /// <summary>Wall-clock time-to-first-token of the cold stage, in milliseconds.</summary>
    public required double? TtftMs { get; init; }

    /// <summary>Total wall-clock of the whole transcript, in milliseconds.</summary>
    public required double? TotalLatencyMs { get; init; }

    /// <summary>Warm-request reused prompt fraction, 0..1.</summary>
    public required double? CacheHitRate { get; init; }

    /// <summary>Wall-clock of the tool-call round, in milliseconds.</summary>
    public required double? ToolLoopMs { get; init; }

    /// <summary>Effective free VRAM observed at load (global-free when available, otherwise process budget).</summary>
    public required long? VramLoadBytes { get; init; }

    /// <summary>Effective free VRAM observed after the loop (global-free when available, otherwise process budget).</summary>
    public required long? VramAfterBytes { get; init; }

    /// <summary>Number of measured passes.</summary>
    public required int Runs { get; init; }

    /// <summary>Raw <c>/metrics</c> scrape for operator diagnostics.</summary>
    public required string? RawJson { get; init; }

    public string? Role { get; init; }

    public double? ItemsPerSecond { get; init; }

    public double? InputTokensPerSecond { get; init; }

    public double? P50LatencyMs { get; init; }

    public double? P95LatencyMs { get; init; }

    public int? BatchSize { get; init; }

    public int? OutputDimension { get; init; }

    public bool? ValuesFinite { get; init; }

    public bool? DeterministicOutput { get; init; }

    public long? GlobalFreeVramLoadBytes { get; init; }

    public long? GlobalFreeVramAfterBytes { get; init; }

    public long? ProcessBudgetVramLoadBytes { get; init; }

    public long? ProcessBudgetVramAfterBytes { get; init; }

    public long? MinimumGlobalFreeVramBytes { get; init; }

    public long? MinimumProcessBudgetVramBytes { get; init; }

    public long? PeakProcessRamBytes { get; init; }

    public bool ExternalPressureDetected { get; init; }

    public string? DiagnosticsJson { get; init; }

    /// <summary>Requests actively processing at the last scrape.</summary>
    public double? RequestsProcessingAtLastScrape { get; init; }

    /// <summary>Requests deferred at the last scrape.</summary>
    public double? RequestsDeferredAtLastScrape { get; init; }

    /// <summary>Largest server-reported context-token watermark.</summary>
    public double? ContextTokensHighWatermark { get; init; }

    /// <summary>Server-reported average busy slots per decode.</summary>
    public double? AverageBusySlotsPerDecode { get; init; }

    /// <summary>Ordered per-measured-pass warm-request timings; null for non-chat benchmarks.</summary>
    public IReadOnlyList<LlamaServerGenerationTimings?>? WarmPromptTimings { get; init; }

    /// <summary>A failed run carrying only the sanitized <paramref name="reason" />.</summary>
    public static InferenceBenchmarkMetrics Failed(string reason)
    {
        return new InferenceBenchmarkMetrics
        {
            Success = false,
            FailureReason = reason,
            TokensPerSecond = null,
            PpTokensPerSecond = null,
            TtftMs = null,
            TotalLatencyMs = null,
            CacheHitRate = null,
            ToolLoopMs = null,
            VramLoadBytes = null,
            VramAfterBytes = null,
            Runs = 0,
            RawJson = null
        };
    }
}
