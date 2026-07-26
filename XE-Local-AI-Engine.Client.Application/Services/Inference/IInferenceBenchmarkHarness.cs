namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Text;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Drives a FIXED golden transcript against a transient profiling llama-server process and captures the comparable
///     throughput/latency/cache/VRAM metrics for one inference profile. The transcript and sampling are fixed so two
///     profiles benchmarked on the same box are directly comparable.
/// </summary>
public interface IInferenceBenchmarkHarness
{
    /// <summary>Runs the golden transcript against the <paramref name="context" /> endpoint and returns its measured metrics.</summary>
    Task<InferenceBenchmarkMetrics> RunAsync(LlamaServerProfilingContext context, InferenceBenchmarkSpec spec, CancellationToken ct);
}

/// <summary>A deterministic mock tool the tool-call stage offers the model; the result is fixed so the round is reproducible.</summary>
/// <param name="Name">Tool function name.</param>
/// <param name="Description">Tool description the model sees.</param>
/// <param name="DeterministicResult">The fixed result returned whenever the model invokes the tool.</param>
public sealed record InferenceBenchmarkToolDefinition(string Name, string Description, string DeterministicResult);

/// <summary>
///     The fixed golden transcript + sampling for a benchmark run. Built per-profile by
///     <see cref="Golden" /> so the long-context stage is sized near the profile's context window.
/// </summary>
/// <param name="Backend">The lowercase backend token (<c>cuda</c>/<c>vulkan</c>/<c>cpu</c>) for the host-VRAM probe.</param>
/// <param name="CtxSize">The profile's context size; drives the long-context injection length.</param>
/// <param name="SystemPersona">The fixed system persona for every stage.</param>
/// <param name="ColdUserTurn">The first user turn (cold cache).</param>
/// <param name="WarmFollowUpTurn">The follow-up user turn that reuses the cold context (warm cache).</param>
/// <param name="ToolUserTurn">The user turn that should trigger the mock tool.</param>
/// <param name="Tool">The deterministic mock tool offered in the tool-call stage.</param>
/// <param name="LongContextUserTurn">A long user turn sized near <paramref name="CtxSize" /> to exercise long-context handling.</param>
/// <param name="Seed">Fixed RNG seed for reproducibility.</param>
/// <param name="Temperature">Fixed sampling temperature (0 = greedy/deterministic).</param>
public sealed record InferenceBenchmarkSpec(
    string Backend,
    int CtxSize,
    string SystemPersona,
    string ColdUserTurn,
    string WarmFollowUpTurn,
    string ToolUserTurn,
    InferenceBenchmarkToolDefinition Tool,
    string LongContextUserTurn,
    int Seed,
    float Temperature)
{
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
    ///     Minimum absolute process-budget/global-free divergence that can invalidate a benchmark. The relative threshold
    ///     below must also be exceeded, avoiding false pressure evidence from small driver-reporting jitter.
    /// </summary>
    public long VramDivergenceAbsoluteThresholdBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Minimum process-budget/global-free divergence ratio that can invalidate a benchmark.</summary>
    public double VramDivergenceRatioThreshold { get; init; } = 0.05d;

    /// <summary>Builds the canonical golden transcript for <paramref name="backend" />, with the long-context stage sized to ~75% of <paramref name="ctxSize" />.</summary>
    public static InferenceBenchmarkSpec Golden(string backend, int ctxSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);

        var safeCtx = ctxSize > 0 ? ctxSize : 4096;

        return new InferenceBenchmarkSpec(Backend: backend,
            CtxSize: safeCtx,
            SystemPersona: "You are a concise benchmarking assistant. Answer briefly and deterministically.",
            ColdUserTurn: "List three primary colors, comma separated.",
            WarmFollowUpTurn: "Now list three secondary colors, comma separated.",
            ToolUserTurn: "What is the current bench status? Call the bench_status tool to find out.",
            Tool: new InferenceBenchmarkToolDefinition("bench_status",
                "Returns a fixed benchmark status payload for the deterministic tool-call round.",
                "{\"status\":\"ok\",\"phase\":\"benchmark\"}"),
            LongContextUserTurn: BuildLongContextTurn(safeCtx),
            Seed: 0,
            Temperature: 0f);
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
///     The measured outcome of one role-specific benchmark run. Chat keeps TG/PP/cache/tool metrics; embedding and
///     reranker add item throughput, latency distribution, batch shape, and output-correctness evidence. Global-free VRAM
///     and llama.cpp's process-local budget are recorded separately so WDDM pressure cannot masquerade as a valid run.
///     Any figure that could not be derived is <see langword="null" />.
/// </summary>
/// <param name="Success">Whether the run completed; <see langword="false" /> blocks the freeze gate.</param>
/// <param name="FailureReason">Sanitized failure reason when <paramref name="Success" /> is false.</param>
/// <param name="TokensPerSecond">Token-generation throughput (TG tok/s) from <c>/metrics</c>.</param>
/// <param name="PpTokensPerSecond">Prompt-processing throughput (PP tok/s) from <c>/metrics</c>.</param>
/// <param name="TtftMs">Wall-clock time-to-first-token of the cold stage, in milliseconds.</param>
/// <param name="TotalLatencyMs">Total wall-clock of the whole transcript, in milliseconds.</param>
/// <param name="CacheHitRate">Prompt-token reuse ratio (cold vs warm), 0..1.</param>
/// <param name="ToolLoopMs">Wall-clock of the tool-call round, in milliseconds.</param>
/// <param name="VramLoadBytes">Effective free VRAM observed at load (global-free when available, otherwise process budget).</param>
/// <param name="VramAfterBytes">Effective free VRAM observed after the loop (global-free when available, otherwise process budget).</param>
/// <param name="Runs">Number of measured passes.</param>
/// <param name="RawJson">Raw <c>/metrics</c> scrape for operator diagnostics.</param>
public sealed record InferenceBenchmarkMetrics(
    bool Success,
    string? FailureReason,
    double? TokensPerSecond,
    double? PpTokensPerSecond,
    double? TtftMs,
    double? TotalLatencyMs,
    double? CacheHitRate,
    double? ToolLoopMs,
    long? VramLoadBytes,
    long? VramAfterBytes,
    int Runs,
    string? RawJson,
    string? Role = null,
    double? ItemsPerSecond = null,
    double? InputTokensPerSecond = null,
    double? P50LatencyMs = null,
    double? P95LatencyMs = null,
    int? BatchSize = null,
    int? OutputDimension = null,
    bool? ValuesFinite = null,
    bool? DeterministicOutput = null,
    long? GlobalFreeVramLoadBytes = null,
    long? GlobalFreeVramAfterBytes = null,
    long? ProcessBudgetVramLoadBytes = null,
    long? ProcessBudgetVramAfterBytes = null,
    bool ExternalPressureDetected = false,
    string? DiagnosticsJson = null)
{
    /// <summary>A failed run carrying only the sanitized <paramref name="reason" />.</summary>
    public static InferenceBenchmarkMetrics Failed(string reason)
    {
        return new InferenceBenchmarkMetrics(Success: false,
            FailureReason: reason,
            TokensPerSecond: null,
            PpTokensPerSecond: null,
            TtftMs: null,
            TotalLatencyMs: null,
            CacheHitRate: null,
            ToolLoopMs: null,
            VramLoadBytes: null,
            VramAfterBytes: null,
            Runs: 0,
            RawJson: null);
    }
}
