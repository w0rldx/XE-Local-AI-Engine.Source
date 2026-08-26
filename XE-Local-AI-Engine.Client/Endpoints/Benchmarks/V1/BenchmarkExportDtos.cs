namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>The agent identity a project's runs were frozen against, read from the runs themselves.</summary>
public sealed class BenchmarkExportAgentResponse
{
    public required string Name { get; init; }
    public long Version { get; init; }
}

/// <summary>The project half of an export: the frozen task plus the judge configuration the runs were scored under.</summary>
public sealed class BenchmarkExportProjectResponse
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string CoreTask { get; init; }
    public int ContextTokens { get; init; }
    public int? MaxOutputTokens { get; init; }

    /// <summary>The thinking budget the runs were frozen with, or null when the reasoning was bounded only by effort.</summary>
    public int? ReasoningBudgetTokens { get; init; }

    /// <summary>The generation budget the runs were given, or null for the node's frozen default.</summary>
    public int? InvocationTimeoutSeconds { get; init; }

    /// <summary>Null on a project with no runs — the frozen agent identity only exists once a run has been frozen.</summary>
    public BenchmarkExportAgentResponse? Agent { get; init; }

    public required BenchmarkJudgePolicyResponse Judge { get; init; }
}

/// <summary>
///     The spread of one measured quantity across a repeat group. Population standard deviation, not sample: the runs
///     ARE the population — this is every measurement that was taken, not a draw from a larger set — and the sample
///     form would report a spread for a group of one it cannot know.
/// </summary>
public sealed class BenchmarkExportSampleStatisticsResponse
{
    public int SampleCount { get; init; }
    public double? Mean { get; init; }
    public double? StdDev { get; init; }

    /// <summary>Every reading the statistics were derived from, in run order — a reader may want its own summary.</summary>
    public IReadOnlyList<double> Samples { get; init; } = [];
}

/// <summary>
///     One repeat group's raw throughput samples plus their summary. A group is the runs of one
///     <c>repeatGroupId</c>, or a single ungrouped run on its own; warm-ups are excluded, which is the entire reason
///     they exist.
/// </summary>
public sealed class BenchmarkExportRepeatGroupResponse
{
    /// <summary>Null for a run that was launched on its own rather than as part of a group.</summary>
    public Guid? RepeatGroupId { get; init; }

    public required string ModelName { get; init; }

    /// <summary>What the group measured — <c>Throughput</c> or <c>AnswerVariance</c>.</summary>
    public BenchmarkRepeatMode RepeatMode { get; init; }

    public IReadOnlyList<Guid> RunIds { get; init; } = [];

    /// <summary>Mean prompt tokens across the group, or null when nothing measured them.</summary>
    public double? MeanPromptTokens { get; init; }

    /// <summary>
    ///     Mean generated tokens across the group. Worth its own field rather than reading one run: an
    ///     answer-variance group's repeats answer at different lengths, which is exactly what it measures.
    /// </summary>
    public double? MeanGenerationTokens { get; init; }

    public required BenchmarkExportSampleStatisticsResponse TtftMs { get; init; }
    public required BenchmarkExportSampleStatisticsResponse PromptTokensPerSecond { get; init; }
    public required BenchmarkExportSampleStatisticsResponse GenerationTokensPerSecond { get; init; }
}

/// <summary>
///     One row shaped like a <c>llama-bench -o json</c> record, for the fields this node has an equivalent of. It is a
///     TRANSLATION, not a claim of comparability: llama-bench times a fixed synthetic prompt inside one process, while
///     these numbers come from a real agent turn against a freshly launched server, so the two are the same units and
///     not the same experiment. Fields llama-bench carries and this node does not observe are omitted rather than
///     invented.
/// </summary>
/// <remarks>
///     Two rows per group, mirroring llama-bench's own shape: a prompt-processing row (<c>nGen</c> 0) and a
///     token-generation row (<c>nPrompt</c> 0).
/// </remarks>
public sealed class BenchmarkExportLlamaBenchRowResponse
{
    /// <summary>llama.cpp's <c>build_commit</c> — the installed runtime's source commit, or its version when built from a release.</summary>
    public string? BuildCommit { get; init; }

    /// <summary>llama.cpp's <c>gpu_info</c> — the enumerated device names, joined, or null when none was captured.</summary>
    public string? GpuInfo { get; init; }

    public string? ModelFilename { get; init; }

    /// <summary>Bytes of the model's weight members, as the frozen snapshot recorded them.</summary>
    public long? ModelSize { get; init; }

    public int? NGpuLayers { get; init; }

    /// <summary>Prompt tokens the row measures, rounded from the group MEAN. Zero on a generation row.</summary>
    public int NPrompt { get; init; }

    /// <summary>Generated tokens the row measures, rounded from the group MEAN. Zero on a prompt row.</summary>
    public int NGen { get; init; }

    public double? AvgTs { get; init; }
    public double? StddevTs { get; init; }
    public int Samples { get; init; }
    public Guid? RepeatGroupId { get; init; }

    /// <summary>This node's own model name for the row, which llama-bench has no field for.</summary>
    public required string ModelName { get; init; }
}

/// <summary>
///     One project's complete benchmark record: every run at full detail (transcript and judge verdict included), the
///     project and judge configuration they were produced under, and what the ranking was computed against.
/// </summary>
public sealed class BenchmarkExportResponse
{
    public int SchemaVersion { get; init; } = BenchmarkExportProjection.SchemaVersion;
    public long ExportedAtUtc { get; init; }
    public required BenchmarkExportProjectResponse Project { get; init; }
    public required BenchmarkRankCohortResponse RankCohort { get; init; }
    public IReadOnlyList<BenchmarkRunDetailResponse> Runs { get; init; } = [];

    /// <summary>Per repeat group: the raw throughput readings and their spread. Empty when no run measured anything.</summary>
    public IReadOnlyList<BenchmarkExportRepeatGroupResponse> RepeatGroups { get; init; } = [];

    /// <summary>The same measurements translated into <c>llama-bench -o json</c> field names.</summary>
    public IReadOnlyList<BenchmarkExportLlamaBenchRowResponse> LlamaBench { get; init; } = [];

    /// <summary>The active Bradley-Terry fit the project ranked through, or null when it judges pointwise.</summary>
    public BenchmarkExportPairwiseFitResponse? PairwiseFit { get; init; }

    /// <summary>The project's current task items, including their clear-text prompts.</summary>
    public IReadOnlyList<BenchmarkTaskItemResponse> TaskItems { get; init; } = [];

    /// <summary>The measurement cells the ranking was computed over.</summary>
    public IReadOnlyList<BenchmarkCellResponse> Cells { get; init; } = [];

    /// <summary>How many leaf items the project counts toward its score now.</summary>
    public int ScorableItemCount { get; init; }
}

/// <summary>
///     The published fit a pairwise project's scores were read out of. Exported as ONE object rather than smeared over
///     the runs, because that is what it is: a fit is a single immutable row whose identity (<see cref="FitKey" />)
///     covers the whole comparison set. Per-run strengths stay on the run rows, where every other score already is.
/// </summary>
public sealed class BenchmarkExportPairwiseFitResponse
{
    public Guid Id { get; init; }
    public required string FitKey { get; init; }
    public required string JudgeExecutionKey { get; init; }
    public int CohortGeneration { get; init; }
    public int ComparisonSetVersion { get; init; }
    public int Iterations { get; init; }
    public int BootstrapReplicates { get; init; }
    public long CreatedAtUtc { get; init; }

    /// <summary>The ordered verdicts actually fitted — the auditable answer to "which comparisons produced this".</summary>
    public required string FittedSetJson { get; init; }

    public IReadOnlyList<BenchmarkExportPairwiseScoreResponse> Scores { get; init; } = [];
}

/// <summary>One run's fitted strength and its bootstrap interval.</summary>
public sealed class BenchmarkExportPairwiseScoreResponse
{
    public Guid RunId { get; init; }
    public int? Score { get; init; }
    public int? CiLow { get; init; }
    public int? CiHigh { get; init; }
    public int Comparisons { get; init; }
    public int BootstrapAppearances { get; init; }
    public string? Reason { get; init; }
}
