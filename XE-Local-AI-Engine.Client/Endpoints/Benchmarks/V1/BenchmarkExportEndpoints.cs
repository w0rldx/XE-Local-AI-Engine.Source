namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text;
using System.Text.Json;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Downloads a project's complete benchmark record as JSON.</summary>
public sealed class ExportBenchmarkProjectEndpoint(IBenchmarkExportQuery exports, TimeProvider timeProvider)
    : Endpoint<BenchmarkProjectRouteRequest, BenchmarkExportResponse>
{
    private readonly IBenchmarkExportQuery _exports = exports ?? throw new ArgumentNullException(nameof(exports));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectExport);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BenchmarkProjectRouteRequest req, CancellationToken ct)
    {
        var export = await _exports.GetJsonAsync(req.ProjectId, ct).ConfigureAwait(false);
        if (export is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var runs = export.Runs.Select(item => (item.Full with
                         {
                             Rank = item.Summary.Rank,
                             CellQuality = item.Summary.CellQuality
                         }).ToDetail(item.Verdict, export.Fidelity.ExpectedKldDigest))
                         .ToArray();
        var groups = BenchmarkExportStatistics.Groups(export.Summaries);

        HttpContext.Response.Headers.ContentDisposition = BenchmarkExportProjection.Attachment(export.Project.Name, now, "json");
        await Send.OkAsync(new BenchmarkExportResponse
                  {
                      TaskItems = [.. export.TaskItems.Select(BenchmarkEndpointMapper.ToResponse)],
                      Cells = export.Cells.ToResponse().Cells,
                      ScorableItemCount = export.Cells.ScorableItemCount,
                      ExportedAtUtc = now.ToUnixTimeMilliseconds(),
                      Project = new BenchmarkExportProjectResponse
                      {
                          Id = export.Project.Id,
                          Name = export.Project.Name,
                          CoreTask = JsonSerializer.Deserialize<string>(export.Project.CoreTaskJson.Span) ?? string.Empty,
                          ContextTokens = export.Project.ContextTokens,
                          MaxOutputTokens = export.Project.MaxOutputTokens,
                          ReasoningBudgetTokens = export.Project.ReasoningBudgetTokens,
                          InvocationTimeoutSeconds = export.Project.InvocationTimeoutSeconds,
                          Agent = export.Summaries.Count == 0
                              ? null
                              : new BenchmarkExportAgentResponse
                              {
                                  Name = export.Summaries[0].AgentName,
                                  Version = export.Summaries[0].AgentVersion
                              },
                          Judge = ToJudgePolicy(export.JudgePolicyRevision)
                      },
                      RankCohort = BenchmarkExportProjection.ToResponse(export.RankCohort),
                      Runs = runs,
                      RepeatGroups = groups,
                      LlamaBench = BenchmarkExportStatistics.LlamaBenchRows(groups, export.Facts),
                      PairwiseFit = BenchmarkExportProjection.ToResponse(export.PairwiseFit)
                  }, ct)
                  .ConfigureAwait(false);
    }

    private static BenchmarkJudgePolicyResponse ToJudgePolicy(BenchmarkJudgePolicyRevisionRecord? revision)
    {
        var policy = revision?.PolicyJson is { } payload && !payload.IsEmpty
            ? BenchmarkJudgeSerialization.DeserializePolicy(payload.Span)
            : null;
        return BenchmarkEndpointMapper.ToJudgePolicy(revision, policy);
    }
}

/// <summary>Downloads a project's flat benchmark run projection as RFC 4180 CSV.</summary>
public sealed class ExportBenchmarkProjectCsvEndpoint(IBenchmarkExportQuery exports, TimeProvider timeProvider)
    : Endpoint<BenchmarkProjectRouteRequest>
{
    private readonly IBenchmarkExportQuery _exports = exports ?? throw new ArgumentNullException(nameof(exports));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectExportCsv);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<string>(StatusCodes.Status200OK, "text/csv")
                                      .ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BenchmarkProjectRouteRequest req, CancellationToken ct)
    {
        var export = await _exports.GetCsvAsync(req.ProjectId, ct).ConfigureAwait(false);
        if (export is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var csv = BenchmarkExportCsv.Render(export.Runs,
            export.Fidelity.ExpectedKldDigest,
            export.PairwiseFit);
        await Send.BytesAsync(Encoding.UTF8.GetBytes(csv),
                      BenchmarkExportProjection.FileName(export.Project.Name, now, "csv"),
                      "text/csv",
                      cancellation: ct)
                  .ConfigureAwait(false);
    }
}
