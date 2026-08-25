namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;

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
}

/// <summary>
///     Downloads a project's whole benchmark record as JSON. Every harness worth comparing against exports its
///     per-sample data, so this is the one endpoint that deliberately ships the full transcript: the run detail is the
///     SAME shape <c>GET benchmarks/runs/{runId}</c> returns, reused verbatim rather than reshaped, so an export and a
///     live read of one run can never disagree.
/// </summary>
public sealed class ExportBenchmarkProjectEndpoint(IBenchmarkStore store, TimeProvider timeProvider)
    : Endpoint<BenchmarkProjectRouteRequest, BenchmarkExportResponse>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

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
        var runs = new List<BenchmarkRunDetailResponse>(records.Count);
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
                Rank = summary.Rank
            }).ToDetail(verdict));
        }

        HttpContext.Response.Headers.ContentDisposition = BenchmarkExportProjection.Attachment(project.Name, now, "json");
        await Send.OkAsync(new BenchmarkExportResponse
                  {
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
                      Runs = runs
                  }, ct)
                  .ConfigureAwait(false);
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
        var csv = BenchmarkExportCsv.Render(records);
        await Send.BytesAsync(Encoding.UTF8.GetBytes(csv),
                      BenchmarkExportProjection.FileName(project.Name, now, "csv"),
                      "text/csv",
                      cancellation: ct)
                  .ConfigureAwait(false);
    }
}

/// <summary>Shared collection and naming for both export representations.</summary>
internal static class BenchmarkExportProjection
{
    public const int SchemaVersion = 1;

    /// <summary>The store's page ceiling; a project's runs are counted in tens, so one or two pages is the norm.</summary>
    private const int PageSize = 200;

    private const int MaxSlugLength = 40;

    /// <summary>Every run of a project, newest first, with the ranking they were ranked against.</summary>
    public static async Task<(IReadOnlyList<BenchmarkRunRecord> Runs, BenchmarkRankCohort? RankCohort)> ListAllForExportAsync(IBenchmarkStore store,
        Guid projectId,
        CancellationToken ct)
    {
        var runs = new List<BenchmarkRunRecord>();
        BenchmarkRankCohort? cohort = null;
        while (true)
        {
            var page = await store.ListRunsAsync(projectId, runs.Count, PageSize, modelContentFingerprint: null, includeUnscored: true, ct)
                                  .ConfigureAwait(false);
            cohort ??= page.RankCohort;
            if (page.Items.Count == 0)
            {
                return (runs, cohort);
            }

            runs.AddRange(page.Items);
            if (runs.Count >= page.TotalCount)
            {
                return (runs, cohort);
            }
        }
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
internal static class BenchmarkExportCsv
{
    private const string Header =
        "rank,modelGroupKey,model,quant,kvCacheType,flashAttention,backend,placement,contextTokens,status,stopReason,"
        + "repeatGroupId,repeatIndex,isWarmup,repeatMode,samplingSeed,samplingTemperature,"
        + "totalTokens,tokensPerSecond,ttftMs,promptTokens,promptTokensPerSecond,"
        + "generationTokens,generationTokensPerSecond,cachedPromptTokens,segmentCount,durationMs,qualityScore,qualityScoreSource,"
        + "judgeScore,userScore,rankExclusionReason,launchIdentity,receiptHash";

    public static string Render(IReadOnlyList<BenchmarkRunRecord> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var builder = new StringBuilder(Header).Append("\r\n");
        foreach (var run in runs)
        {
            AppendRow(builder, run);
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

    private static void AppendRow(StringBuilder builder, BenchmarkRunRecord run)
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
               .Append(Field(JsonNamingPolicy.CamelCase.ConvertName(run.RepeatMode.ToString())))
               .Append(',')

               // The one input that differs between the runs of an answer-variance group, so a reader can attribute
               // the spread without decrypting a snapshot.
               .Append(Field(run.SamplingSeed))
               .Append(',')
               .Append(Rate(run.SamplingTemperature))
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
               .Append("\r\n");
    }

    private static string Rate(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(long? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
