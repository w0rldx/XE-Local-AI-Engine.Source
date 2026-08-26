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

    /// <summary>
    ///     The project's task items as it asks them NOW, prompts in clear — exactly as <c>project.coreTask</c> already
    ///     is, because an export is an operator downloading their own project.
    /// </summary>
    public IReadOnlyList<BenchmarkTaskItemResponse> TaskItems { get; init; } = [];

    /// <summary>
    ///     The measurement cells the ranking was computed over. A run row carries its cell key; this says what the
    ///     cell scored, what it ranked, and which items it holds.
    /// </summary>
    public IReadOnlyList<BenchmarkCellResponse> Cells { get; init; } = [];

    /// <summary>How many leaf items the project counts toward its score right now.</summary>
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

/// <summary>
///     Downloads a project's whole benchmark record as JSON. Every harness worth comparing against exports its
///     per-sample data, so this is the one endpoint that deliberately ships the full transcript: the run detail is the
///     SAME shape <c>GET benchmarks/runs/{runId}</c> returns, reused verbatim rather than reshaped, so an export and a
///     live read of one run can never disagree.
/// </summary>
public sealed class ExportBenchmarkProjectEndpoint(IBenchmarkStore store, TimeProvider timeProvider, IBenchmarkRuntimeSnapshotFactory snapshots)
    : Endpoint<BenchmarkProjectRouteRequest, BenchmarkExportResponse>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IBenchmarkRuntimeSnapshotFactory _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectExport);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BenchmarkProjectRouteRequest req, CancellationToken ct)
    {
        if (await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false) is not { } project)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var (records, rankCohort) = await BenchmarkExportProjection.ListAllForExportAsync(_store, req.ProjectId, ct).ConfigureAwait(false);

        // The same gate the live read applies. Without it every exported KLD figure carries kldState "stale" with its
        // numbers nulled, because a null expected digest matches nothing — the export would silently disagree with the
        // project page it was downloaded from.
        var expectedKldDigest = BenchmarkEndpointSupport.ExpectedKldDigest(project);
        var runs = new List<BenchmarkRunDetailResponse>(records.Count);

        // Grouped BEFORE the loop so the facts read below is scoped to the runs that actually need it: LlamaBenchRows
        // reads one run per group, while ReadFacts deserializes a snapshot and RE-HASHES it to validate. Doing that
        // for every run of a fifty-run project paid the whole cost fifty times to use it once per group.
        var groups = BenchmarkExportStatistics.Groups(records);
        var firstOfGroup = groups.Select(static group => group.RunIds[0]).ToHashSet();
        var facts = new Dictionary<Guid, BenchmarkExportRunFacts>(groups.Count);
        foreach (var summary in records)
        {
            // The listing projection deliberately never reads the encrypted payload columns, so the transcript and the
            // launch receipt are only available from a single-run read. Re-attach the rank, which is a project-wide
            // value the single-run read does not compute.
            var full = await _store.GetRunAsync(summary.Id, ct).ConfigureAwait(false);
            if (full is null)
            {
                continue;
            }

            var verdict = await BenchmarkEndpointSupport.ReadVerdictAsync(_store, full, ct).ConfigureAwait(false);
            runs.Add((full with
            {
                Rank = summary.Rank,
                CellQuality = summary.CellQuality
            }).ToDetail(verdict, expectedKldDigest));
            if (firstOfGroup.Contains(full.Id))
            {
                facts[full.Id] = ReadFacts(full);
            }
        }

        var taskItems = await _store.ListTaskItemsAsync(req.ProjectId, ct).ConfigureAwait(false);
        var cells = await _store.ListCellsAsync(req.ProjectId, ct).ConfigureAwait(false);

        HttpContext.Response.Headers.ContentDisposition = BenchmarkExportProjection.Attachment(project.Name, now, "json");
        await Send.OkAsync(new BenchmarkExportResponse
                  {
                      TaskItems = [.. taskItems.Select(BenchmarkEndpointMapper.ToResponse)],
                      Cells = cells.ToResponse().Cells,
                      ScorableItemCount = cells.ScorableItemCount,
                      ExportedAtUtc = now.ToUnixTimeMilliseconds(),
                      Project = new BenchmarkExportProjectResponse
                      {
                          Id = project.Id,
                          Name = project.Name,
                          CoreTask = JsonSerializer.Deserialize<string>(project.CoreTaskJson.Span) ?? string.Empty,
                          ContextTokens = project.ContextTokens,
                          MaxOutputTokens = project.MaxOutputTokens,
                          ReasoningBudgetTokens = project.ReasoningBudgetTokens,
                          InvocationTimeoutSeconds = project.InvocationTimeoutSeconds,
                          Agent = records.Count == 0
                              ? null
                              : new BenchmarkExportAgentResponse
                              {
                                  Name = records[0].AgentName,
                                  Version = records[0].AgentVersion
                              },
                          Judge = await BenchmarkJudgePolicyProjection.ReadAsync(_store, project.Id, ct).ConfigureAwait(false)
                      },
                      RankCohort = BenchmarkExportProjection.ToResponse(rankCohort),
                      Runs = runs,
                      RepeatGroups = groups,
                      LlamaBench = BenchmarkExportStatistics.LlamaBenchRows(groups, facts),
                      PairwiseFit = BenchmarkExportProjection.ToResponse(await _store.GetActivePairwiseFitAsync(req.ProjectId, ct).ConfigureAwait(false))
                  }, ct)
                  .ConfigureAwait(false);
    }

    /// <summary>
    ///     The model and runtime facts a llama-bench row needs, read from one run's frozen snapshot and its environment
    ///     capture. Non-throwing by contract: a payload that cannot be read leaves the row's fields empty rather than
    ///     failing the whole export — a corrupt receipt on one run must not cost the operator every other run's data.
    /// </summary>
    private BenchmarkExportRunFacts ReadFacts(BenchmarkRunRecord run)
    {
        string? modelFilename = null;
        long? modelSize = null;
        int? gpuLayers = null;
        try
        {
            var snapshot = _snapshots.Deserialize(run.RuntimeSnapshotJson.Span);
            var weights = snapshot.PrimaryModel.Members
                                  .Where(static member => member.Role == InstalledModelPhysicalMemberRole.Weight)
                                  .ToArray();
            modelFilename = snapshot.PrimaryModel.SourceFileName ?? weights.FirstOrDefault()?.RelativePath;
            modelSize = weights.Length == 0 ? null : weights.Sum(static member => member.SizeBytes);
            gpuLayers = snapshot.PrimaryRuntime.GpuLayers;
        }
        catch (Exception exception) when (exception is BenchmarkSnapshotException or JsonException)
        {
            Logger.LogWarning(exception, "Benchmark export: run {RunId} carries a snapshot that could not be read.", run.Id);
        }

        string? buildCommit = null;
        string? gpuInfo = null;
        if (run.PrimaryLaunchEvidence?.EnvironmentFactsJson is { } environmentJson && !environmentJson.IsEmpty)
        {
            try
            {
                var environment = BenchmarkCanonicalJson.Deserialize<RuntimeEnvironmentFactsV1>(environmentJson.Span);

                // llama-bench's build_commit is the source revision. A runtime installed from a release has none, so
                // the version it reported stands in — the field means "which build produced these numbers".
                buildCommit = environment?.LlamaRuntime?.SourceCommit ?? environment?.LlamaRuntime?.Version;
                gpuInfo = environment?.Hardware?.Gpus is { Count: > 0 } gpus
                    ? string.Join(", ", gpus.Select(static gpu => gpu.Name))
                    : null;
            }
            catch (JsonException exception)
            {
                Logger.LogWarning(exception, "Benchmark export: run {RunId} carries environment facts that could not be read.", run.Id);
            }
        }

        return new BenchmarkExportRunFacts(buildCommit, gpuInfo, modelFilename, modelSize, gpuLayers);
    }
}

/// <summary>
///     The same export as one spreadsheet row per run. Only flat, already-projected columns — no transcript, no
///     verdict text — so the CSV never decrypts a payload and stays readable in any tool.
/// </summary>
public sealed class ExportBenchmarkProjectCsvEndpoint(IBenchmarkStore store, TimeProvider timeProvider)
    : Endpoint<BenchmarkProjectRouteRequest>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectExportCsv);
        Policies(NodeAuthorizationPolicies.Operator);

        // Declared explicitly: an endpoint with no response type documents itself as 204, and a spec claiming this
        // route answers with no content is a lie a consumer would believe.
        Description(builder => builder.Produces<string>(StatusCodes.Status200OK, "text/csv")
                                      .ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BenchmarkProjectRouteRequest req, CancellationToken ct)
    {
        if (await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false) is not { } project)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var (records, _) = await BenchmarkExportProjection.ListAllForExportAsync(_store, req.ProjectId, ct).ConfigureAwait(false);
        var csv = BenchmarkExportCsv.Render(records,
            BenchmarkEndpointSupport.ExpectedKldDigest(project),
            await _store.GetActivePairwiseFitAsync(req.ProjectId, ct).ConfigureAwait(false));
        await Send.BytesAsync(Encoding.UTF8.GetBytes(csv),
                      BenchmarkExportProjection.FileName(project.Name, now, "csv"),
                      "text/csv",
                      cancellation: ct)
                  .ConfigureAwait(false);
    }
}

/// <summary>
///     Groups a project's runs into their repeat groups and summarizes the throughput each one measured, then
///     translates the same numbers into <c>llama-bench -o json</c> field names.
/// </summary>
/// <remarks>
///     Everything here is derived from columns the listing already carries plus the run's own frozen snapshot and
///     environment capture. Nothing is measured here and nothing is inferred: a run that reported no timings
///     contributes no sample, and a group with no samples reports a null mean rather than a zero.
/// </remarks>
internal static class BenchmarkExportStatistics
{
    /// <summary>
    ///     One entry per repeat group, plus one per ungrouped run. Warm-ups are dropped — absorbing the first-launch
    ///     cost is their whole job, and averaging them back in would put the cost right back into the numbers the
    ///     repeats after them exist to isolate.
    /// </summary>
    public static IReadOnlyList<BenchmarkExportRepeatGroupResponse> Groups(IReadOnlyList<BenchmarkRunRecord> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return runs.Where(static run => !run.IsWarmup && run.Throughput is not null)
                   .GroupBy(static run => run.RepeatGroupId ?? run.Id)
                   .Select(static group =>
                   {
                       var ordered = group.OrderBy(static run => run.RepeatIndex ?? 0).ThenBy(static run => run.CreatedAtUtc).ToArray();
                       return new BenchmarkExportRepeatGroupResponse
                       {
                           RepeatGroupId = ordered[0].RepeatGroupId,
                           ModelName = ordered[0].PrimaryModelName,
                           RepeatMode = ordered[0].RepeatMode,
                           RunIds = [.. ordered.Select(static run => run.Id)],
                           MeanPromptTokens = Summarize(ordered.Select(static run => (double?)run.Throughput?.PromptTokens)).Mean,
                           MeanGenerationTokens = Summarize(ordered.Select(static run => (double?)run.Throughput?.GenerationTokens)).Mean,
                           TtftMs = Summarize(ordered.Select(static run => run.Throughput?.TtftMs)),
                           PromptTokensPerSecond = Summarize(ordered.Select(static run => run.Throughput?.PromptTokensPerSecond)),
                           GenerationTokensPerSecond = Summarize(ordered.Select(static run => run.Throughput?.GenerationTokensPerSecond))
                       };
                   })
                   .ToArray();
    }

    /// <summary>
    ///     Mean and POPULATION standard deviation. The runs are the population — this is every measurement that was
    ///     taken, not a draw from a larger set — and the sample form would divide by zero on the single run that is by
    ///     far the commonest group size.
    /// </summary>
    public static BenchmarkExportSampleStatisticsResponse Summarize(IEnumerable<double?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var samples = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
        if (samples.Length == 0)
        {
            return new BenchmarkExportSampleStatisticsResponse();
        }

        var mean = samples.Average();
        var variance = samples.Sum(sample => (sample - mean) * (sample - mean)) / samples.Length;
        return new BenchmarkExportSampleStatisticsResponse
        {
            SampleCount = samples.Length,
            Mean = mean,
            StdDev = Math.Sqrt(variance),
            Samples = samples
        };
    }

    /// <summary>
    ///     Two rows per group in llama-bench's own shape: a prompt-processing row and a token-generation row.
    ///     <para>
    ///         The model and runtime facts come from the group's FIRST run. An answer-variance group has one snapshot
    ///         per repeat rather than one shared snapshot — the seed differs — but every fact read here (model file,
    ///         its size, the GPU-layer count, the runtime build and the host GPUs) comes from the parts that do not,
    ///         because the whole group is frozen against one launch. The token COUNTS are group means, not the first
    ///         run's: those genuinely vary across an answer-variance group.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<BenchmarkExportLlamaBenchRowResponse> LlamaBenchRows(IReadOnlyList<BenchmarkExportRepeatGroupResponse> groups,
        IReadOnlyDictionary<Guid, BenchmarkExportRunFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(facts);
        var rows = new List<BenchmarkExportLlamaBenchRowResponse>(groups.Count * 2);
        foreach (var group in groups)
        {
            var runFacts = facts.TryGetValue(group.RunIds[0], out var found) ? found : BenchmarkExportRunFacts.Empty;
            rows.Add(Row(group, runFacts, group.PromptTokensPerSecond, nPrompt: Round(group.MeanPromptTokens), nGen: 0));
            rows.Add(Row(group, runFacts, group.GenerationTokensPerSecond, nPrompt: 0, nGen: Round(group.MeanGenerationTokens)));
        }

        return rows;
    }

    private static BenchmarkExportLlamaBenchRowResponse Row(BenchmarkExportRepeatGroupResponse group,
        BenchmarkExportRunFacts facts,
        BenchmarkExportSampleStatisticsResponse statistics,
        int nPrompt,
        int nGen) =>
        new()
        {
            BuildCommit = facts.BuildCommit,
            GpuInfo = facts.GpuInfo,
            ModelFilename = facts.ModelFilename,
            ModelSize = facts.ModelSizeBytes,
            NGpuLayers = facts.GpuLayers,
            NPrompt = nPrompt,
            NGen = nGen,
            AvgTs = statistics.Mean,
            StddevTs = statistics.StdDev,
            Samples = statistics.SampleCount,
            RepeatGroupId = group.RepeatGroupId,
            ModelName = group.ModelName
        };

    private static int Round(double? value) =>
        value is { } number && number > 0 ? (int)Math.Round(number, MidpointRounding.AwayFromZero) : 0;
}

/// <summary>
///     The model and runtime facts one run's llama-bench row needs, read from its frozen snapshot and its environment
///     capture. Every member is optional: a run frozen before a field existed, or one whose spawn never reached
///     readiness, simply omits it.
/// </summary>
internal sealed record BenchmarkExportRunFacts(
    string? BuildCommit,
    string? GpuInfo,
    string? ModelFilename,
    long? ModelSizeBytes,
    int? GpuLayers)
{
    public static BenchmarkExportRunFacts Empty { get; } = new(null, null, null, null, null);
}

/// <summary>Shared collection and naming for both export representations.</summary>
internal static class BenchmarkExportProjection
{
    /// <summary>
    ///     2 since the export gained <c>repeatGroups</c> and <c>llamaBench</c>. Additive — every v1 member is present
    ///     and unchanged — but a consumer that keys off the version needs to see that there is more to read.
    /// </summary>
    /// <summary>
    ///     3 since the P2 axes joined the record: the fidelity block on every run, and the pairwise fit the project
    ///     ranked through. Both are additive — a version 2 reader still finds every column it knew.
    /// </summary>
    /// <summary>
    ///     4 since task suites: the project's <c>taskItems</c>, its measurement <c>cells</c>, and the four identity
    ///     stamps plus the cell mean on every run row. Additive again, and the fourth stamp is what makes an exported
    ///     cell self-describing about the suite it measured rather than about the one the project asks today.
    /// </summary>
    public const int SchemaVersion = 4;

    public static BenchmarkExportPairwiseFitResponse? ToResponse(BenchmarkPairwiseFitRecord? fit) =>
        fit is null
            ? null
            : new BenchmarkExportPairwiseFitResponse
            {
                Id = fit.Id,
                FitKey = fit.FitKey,
                JudgeExecutionKey = fit.JudgeExecutionKey,
                CohortGeneration = fit.CohortGeneration,
                ComparisonSetVersion = fit.ComparisonSetVersion,
                Iterations = fit.Iterations,
                BootstrapReplicates = fit.BootstrapReplicates,
                CreatedAtUtc = fit.CreatedAtUtc,
                FittedSetJson = fit.FittedSetJson,
                Scores =
                [
                    .. BenchmarkExportPairwise.Scores(fit)
                                              .Values.OrderBy(static entry => entry.RunId)
                                              .Select(static entry => new BenchmarkExportPairwiseScoreResponse
                                              {
                                                  RunId = entry.RunId,
                                                  Score = entry.Score,
                                                  CiLow = entry.CiLow,
                                                  CiHigh = entry.CiHigh,
                                                  Comparisons = entry.Comparisons,
                                                  BootstrapAppearances = entry.BootstrapAppearances,
                                                  Reason = entry.Reason
                                              })
                ]
            };

    private const int MaxSlugLength = 40;

    /// <summary>
    ///     Every run of a project, newest first, with the ranking they were ranked against — in ONE store call. It used
    ///     to page, which recomputed the whole-project ranking for every page: a full scan plus a judge-view join
    ///     across three more tables, repeated, to produce the same answer each time.
    /// </summary>
    public static async Task<(IReadOnlyList<BenchmarkRunRecord> Runs, BenchmarkRankCohort? RankCohort)> ListAllForExportAsync(IBenchmarkStore store,
        Guid projectId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var page = await store.ListAllRunsAsync(projectId, ct).ConfigureAwait(false);
        return (page.Items, page.RankCohort);
    }

    public static BenchmarkRankCohortResponse ToResponse(BenchmarkRankCohort? cohort) =>
        new()
        {
            PolicyRevision = cohort?.PolicyRevision,
            ExecutionKey = cohort?.ExecutionKey,
            CohortGeneration = cohort?.CohortGeneration,
            RankedCount = cohort?.RankedCount ?? 0,
            TotalScored = cohort?.TotalScored ?? 0
        };

    /// <summary>
    ///     <c>benchmark-&lt;slug&gt;-&lt;yyyyMMdd-HHmm&gt;.&lt;extension&gt;</c>. The project name is operator-supplied
    ///     free text, so the slug is reduced to <c>[a-z0-9-]</c> before it can reach a <c>Content-Disposition</c>
    ///     header or steer where a browser writes the file.
    /// </summary>
    public static string FileName(string projectName, DateTimeOffset now, string extension) =>
        $"benchmark-{Slug(projectName)}{now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.{extension}";

    public static string Attachment(string projectName, DateTimeOffset now, string extension) =>
        $"attachment; filename=\"{FileName(projectName, now, extension)}\"";

    private static string Slug(string projectName)
    {
        var builder = new StringBuilder(MaxSlugLength);
        foreach (var character in projectName)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            if (builder.Length >= MaxSlugLength)
            {
                break;
            }
        }

        // A trailing separator is always emitted so an empty name degrades to `benchmark-<timestamp>` rather than
        // `benchmark<timestamp>`.
        return builder.Length > 0 && builder[^1] != '-' ? builder.Append('-').ToString() : builder.ToString();
    }
}

/// <summary>RFC 4180 rendering of the flat run columns.</summary>
internal static class BenchmarkExportPairwise
{
    /// <summary>Same options the store wrote the blob with, so a member name cannot bind on one side only.</summary>
    private static readonly JsonSerializerOptions ScoreOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The fit's per-run entries, indexed by run. Non-throwing: a blob that cannot be parsed exports as no
    ///     pairwise columns rather than failing the whole download, exactly as a bad snapshot does for llama-bench.
    /// </summary>
    public static IReadOnlyDictionary<Guid, BenchmarkPairwiseScoreEntry> Scores(BenchmarkPairwiseFitRecord? fit)
    {
        if (fit is null)
        {
            return new Dictionary<Guid, BenchmarkPairwiseScoreEntry>();
        }

        try
        {
            var entries = JsonSerializer.Deserialize<BenchmarkPairwiseScoreEntry[]>(fit.ScoresJson, ScoreOptions) ?? [];
            return entries.GroupBy(static entry => entry.RunId).ToDictionary(static group => group.Key, static group => group.First());
        }
        catch (JsonException)
        {
            return new Dictionary<Guid, BenchmarkPairwiseScoreEntry>();
        }
    }
}

internal static class BenchmarkExportCsv
{
    private const string Header =
        "rank,modelGroupKey,model,quant,kvCacheType,flashAttention,backend,placement,contextTokens,status,stopReason,"
        + "repeatGroupId,repeatIndex,isWarmup,totalTokens,tokensPerSecond,ttftMs,promptTokens,promptTokensPerSecond,"
        + "generationTokens,generationTokensPerSecond,cachedPromptTokens,segmentCount,durationMs,qualityScore,qualityScoreSource,"
        + "judgeScore,userScore,rankExclusionReason,launchIdentity,receiptHash,"

        // APPENDED, never inserted. A CSV consumer that reads by column INDEX — which is most of them, and the whole
        // reason this export is flat — breaks silently on an inserted column and reads a seed as a token count.
        + "repeatMode,samplingSeed,samplingTemperature,"

        // Appended again, same rule. The P2 axes: quant fidelity (display-only, never a rank input) and the pairwise
        // fit's per-run interval. The strength itself is already in qualityScore with qualityScoreSource "pairwise".
        + "fidelityStatus,perplexityMean,perplexityStdErr,perplexityChunks,perplexityContextTokens,perplexityCorpusId,"
        + "kldState,kldMean,kldP99,topTokenAgreement,kldBaseFingerprint,kldBaseLogitsDigest,"
        + "pairwiseScore,pairwiseCiLow,pairwiseCiHigh,pairwiseComparisons,pairwiseFitKey,"

        // Appended once more. The four identity stamps plus the cell mean: which case this row answered, which cell
        // its score aggregates into, exactly what it was asked, and what the whole question set was at the time.
        + "taskItemId,taskItemIndex,cellKey,taskInputHash,taskItemSetHash,cellQuality";

    /// <param name="expectedKldBaseLogitsDigest">
    ///     The digest the project's CURRENT settings recompute. A run whose stored digest differs exports
    ///     <c>kldState=stale</c> with its KLD cells EMPTY — the same withholding the API does, because a number a
    ///     reader can still see is a number they will still compare.
    /// </param>
    public static string Render(IReadOnlyList<BenchmarkRunRecord> runs,
        string? expectedKldBaseLogitsDigest = null,
        BenchmarkPairwiseFitRecord? pairwiseFit = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var scores = BenchmarkExportPairwise.Scores(pairwiseFit);
        var builder = new StringBuilder(Header).Append("\r\n");
        foreach (var run in runs)
        {
            AppendRow(builder, run, expectedKldBaseLogitsDigest, pairwiseFit, scores);
        }

        return builder.ToString();
    }

    /// <summary>
    ///     A value whose first character makes a spreadsheet read the cell as a formula. Several columns here are
    ///     operator-supplied (a model name, an HF repo id) or provider-verbatim (<c>stopReason</c>), so a row can carry
    ///     <c>=HYPERLINK(...)</c> into a workbook that evaluates it.
    /// </summary>
    private static readonly SearchValues<char> FormulaLeadCharacters = SearchValues.Create("=+-@\t\r");

    /// <summary>
    ///     Quoted only when it has to be, an embedded quote is doubled, and a value that would be read as a formula is
    ///     quoted with a leading apostrophe — the spreadsheet-standard text escape, which every reader strips back off.
    /// </summary>
    internal static string Field(string? value)
    {
        if (value is null or { Length: 0 })
        {
            return string.Empty;
        }

        if (FormulaLeadCharacters.Contains(value[0]))
        {
            return $"\"'{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static void AppendRow(StringBuilder builder,
        BenchmarkRunRecord run,
        string? expectedKldBaseLogitsDigest,
        BenchmarkPairwiseFitRecord? pairwiseFit,
        IReadOnlyDictionary<Guid, BenchmarkPairwiseScoreEntry> scores)
    {
        var intent = run.PrimaryLaunchIntent;
        var evidence = run.PrimaryLaunchEvidence;
        var placement = evidence?.PlacementOffloaded is { } offloaded && evidence.PlacementTotal is { } total
            ? $"{offloaded}/{total}"
            : string.Empty;
        builder.Append(Number(run.Rank))
               .Append(',')
               .Append(Field(BenchmarkModelGroupKey.From(run.PrimaryModelName, run.PrimaryModelOrigin)))
               .Append(',')
               .Append(Field(run.PrimaryModelName))
               .Append(',')
               .Append(Field(BenchmarkModelGroupKey.QuantTag(run.PrimaryModelName)))
               .Append(',')
               .Append(Field(intent?.KvCacheType))
               .Append(',')
               .Append(Field(intent?.FlashAttentionMode))
               .Append(',')
               .Append(Field(evidence?.EffectiveBackend))
               .Append(',')
               .Append(placement)
               .Append(',')
               .Append(Number(run.EffectiveContextTokens ?? run.RequestedContextTokens))
               .Append(',')
               .Append(Field(JsonNamingPolicy.CamelCase.ConvertName(run.PrimaryStatus.ToString())))
               .Append(',')
               .Append(Field(run.PrimaryStopReason))
               .Append(',')
               .Append(run.RepeatGroupId?.ToString() ?? string.Empty)
               .Append(',')
               .Append(Number(run.RepeatIndex))
               .Append(',')
               .Append(run.IsWarmup ? "true" : "false")
               .Append(',')
               .Append(Number(run.TotalTokens))
               .Append(',')
               .Append(Rate(run.TokensPerSecond))
               .Append(',')
               .Append(Rate(run.Throughput?.TtftMs))
               .Append(',')
               .Append(Number(run.Throughput?.PromptTokens))
               .Append(',')
               .Append(Rate(run.Throughput?.PromptTokensPerSecond))
               .Append(',')
               .Append(Number(run.Throughput?.GenerationTokens))
               .Append(',')
               .Append(Rate(run.Throughput?.GenerationTokensPerSecond))
               .Append(',')
               .Append(Number(run.Throughput?.CachedPromptTokens))
               .Append(',')

               // Above 1 the token counts span several llama-server requests of one tool-calling turn, which is a
               // different fact from the same counts over a single prefill — and nothing else in the row says so.
               .Append(Number(run.Throughput?.SegmentCount))
               .Append(',')
               .Append(Number(run.DurationMs))
               .Append(',')
               .Append(Number(run.QualityScore))
               .Append(',')
               .Append(Field(run.QualityScoreSource ?? BenchmarkQualityScoreSources.None))
               .Append(',')
               .Append(Number(run.Judge?.Score))
               .Append(',')
               .Append(Number(run.UserScore))
               .Append(',')
               .Append(Field(run.Judge?.RankExclusionReason))
               .Append(',')
               .Append(Field(evidence?.EffectiveLaunchIdentity))
               .Append(',')
               .Append(Field(evidence?.ReceiptHash))
               .Append(',')
               .Append(Field(JsonNamingPolicy.CamelCase.ConvertName(run.RepeatMode.ToString())))
               .Append(',')

               // The one input that differs between the runs of an answer-variance group, so a reader can attribute
               // the spread without decrypting a snapshot.
               .Append(Field(run.SamplingSeed))
               .Append(',')
               .Append(Rate(run.SamplingTemperature))
               .Append(',');
        AppendFidelity(builder, run, expectedKldBaseLogitsDigest);
        AppendPairwise(builder, run, pairwiseFit, scores);

        // taskItemIndex goes through Field, not Number: an index is numeric, but a project that renumbered into a
        // negative range would hand a spreadsheet a leading '-' to evaluate.
        _ = builder.Append(',')
                   .Append(Field(run.TaskItemId?.ToString()))
                   .Append(',')
                   .Append(Field(run.TaskItemIndex?.ToString(CultureInfo.InvariantCulture)))
                   .Append(',')
                   .Append(Field(run.CellKey))
                   .Append(',')
                   .Append(Field(run.TaskInputHash))
                   .Append(',')
                   .Append(Field(run.TaskItemSetHash))
                   .Append(',')
                   .Append(Number(run.CellQuality))
                   .Append("\r\n");
    }

    private static void AppendFidelity(StringBuilder builder, BenchmarkRunRecord run, string? expectedKldBaseLogitsDigest)
    {
        var fidelity = run.Fidelity;
        var comparable = fidelity is not null
                         && BenchmarkKldCacheKey.IsComparable(fidelity.KldBaseLogitsDigest, expectedKldBaseLogitsDigest);
        var measured = comparable ? BenchmarkFidelityKldStates.Ok : BenchmarkFidelityKldStates.Stale;
        var kldState = fidelity?.KldMean is null ? BenchmarkFidelityKldStates.None : measured;
        _ = builder.Append(Field(fidelity?.Status))
                   .Append(',')
                   .Append(Precise(fidelity?.PerplexityMean))
                   .Append(',')
                   .Append(Precise(fidelity?.PerplexityStdErr))
                   .Append(',')
                   .Append(Number(fidelity?.PerplexityChunks))
                   .Append(',')
                   .Append(Number(fidelity?.PerplexityContextTokens))
                   .Append(',')
                   .Append(Field(fidelity?.PerplexityCorpusId))
                   .Append(',')
                   .Append(Field(fidelity is null ? null : kldState))
                   .Append(',')
                   .Append(Precise(comparable ? fidelity?.KldMean : null))
                   .Append(',')
                   .Append(Precise(comparable ? fidelity?.KldP99 : null))
                   .Append(',')
                   .Append(Precise(comparable ? fidelity?.TopTokenAgreement : null))
                   .Append(',')
                   .Append(Field(fidelity?.KldBaseFingerprint))
                   .Append(',')

                   // The digest is exported even when the figure is withheld: it is the EVIDENCE for the withholding,
                   // and a reader comparing it against the project's current one can see exactly what moved.
                   .Append(Field(fidelity?.KldBaseLogitsDigest))
                   .Append(',');
    }

    private static void AppendPairwise(StringBuilder builder,
        BenchmarkRunRecord run,
        BenchmarkPairwiseFitRecord? pairwiseFit,
        IReadOnlyDictionary<Guid, BenchmarkPairwiseScoreEntry> scores)
    {
        _ = scores.TryGetValue(run.Id, out var entry);
        _ = builder.Append(Number(entry?.Score))
                   .Append(',')
                   .Append(Number(entry?.CiLow))
                   .Append(',')
                   .Append(Number(entry?.CiHigh))
                   .Append(',')
                   .Append(Number(entry?.Comparisons))
                   .Append(',')

                   // On every row of the cohort, not once at the top: a flat CSV has nowhere else to put it, and a
                   // reader filtering rows would otherwise lose which fit the numbers came from.
                   .Append(Field(entry is null ? null : pairwiseFit?.FitKey));
    }

    private static string Rate(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    ///     Six decimals, for the fidelity numbers. <see cref="Rate" />'s three are right for a token rate and wrong
    ///     here: the measured Q4_K_M/UD-Q3_K_XL perplexity gap is 6.7977 vs 6.9497 with standard errors around 0.074,
    ///     so rounding at three decimals throws away the digits that decide whether two quants separate at all.
    /// </summary>
    private static string Precise(double? value) =>
        value?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
