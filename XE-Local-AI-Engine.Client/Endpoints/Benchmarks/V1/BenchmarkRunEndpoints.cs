namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class ListBenchmarkRunsEndpoint : Endpoint<ListBenchmarkRunsRequest, ListBenchmarkRunsResponse>
{
    private readonly BenchmarkRecordService _records;

    public ListBenchmarkRunsEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectRuns);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ListBenchmarkRunsRequest req, CancellationToken ct)
    {
        if (req.Page < 1 || req.PageSize is < 1 or > 200)
        {
            AddError("Page must be positive and pageSize must be between 1 and 200.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var project = await _records.GetProjectAsync(req.ProjectId, ct);
        if (project is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found.")));
            return;
        }

        var expectedKldDigest = BenchmarkEndpointSupport.ExpectedKldDigest(project);

        var page = await _records.ListRunsAsync(req.ProjectId,
                                   (req.Page - 1) * req.PageSize,
                                   req.PageSize,
                                   req.ModelContentFingerprint,
                                   req.IncludeUnscored,
                                   ct);
        await Send.OkAsync(new ListBenchmarkRunsResponse
                  {
                      Items = page.Items.Select(run => run.ToSummary(expectedKldDigest)).ToArray(),
                      Page = req.Page,
                      PageSize = req.PageSize,
                      TotalCount = page.TotalCount,
                      RankCohort = new BenchmarkRankCohortResponse
                      {
                          PolicyRevision = page.RankCohort?.PolicyRevision,
                          ExecutionKey = page.RankCohort?.ExecutionKey,
                          CohortGeneration = page.RankCohort?.CohortGeneration,
                          RankedCount = page.RankCohort?.RankedCount ?? 0,
                          TotalScored = page.RankCohort?.TotalScored ?? 0
                      }
                  }, ct);
    }
}

public sealed class StartBenchmarkRunEndpoint : Endpoint<StartBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkRunFreezeService _runs;

    public StartBenchmarkRunEndpoint(IBenchmarkRunFreezeService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.ProjectRuns);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<BenchmarkRunDetailResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict)
                                      .ProducesProblem(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(StartBenchmarkRunRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
        {
            AddError("A primary model is required.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // Absent/blank is Auto and stays null; anything outside the allow-list is the caller's mistake, not an
        // unsupported runtime, so it is a 400 here rather than the 422 an unlaunchable-but-known type gets.
        if (!BenchmarkKvCacheType.TryNormalize(req.KvCacheType, out var kvCacheType))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Problem(StatusCodes.Status400BadRequest,
                BenchmarkErrorCode.InvalidRequest,
                "The requested KV-cache type is not supported."));
            return;
        }

        try
        {
            // The FIRST run of the group is the response: it is the one that starts, so it is the one an operator's
            // live pane should open on. The rest are reachable through its repeatGroupId.
            var created = await _runs.StartAsync(new BenchmarkRunStartRequest
            {
                ProjectId = req.ProjectId,
                PrimaryModelName = req.ModelName,
                ExpectedProjectVersion = req.ExpectedProjectVersion,
                KvCacheType = kvCacheType,
                RepeatCount = req.RepeatCount,
                Warmup = req.Warmup,
                RepeatMode = req.RepeatMode,
                AnswerVarianceTemperature = req.AnswerVarianceTemperature
            }, scope: null, ct);
            await Send.ResultAsync(Results.Accepted(value: created[0].ToDetail()));
        }
        catch (KeyNotFoundException exception)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception));
        }
        catch (NotSupportedException exception)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Problem(StatusCodes.Status422UnprocessableEntity,
                BenchmarkErrorCode.UnsupportedSnapshot,
                exception.Message));
        }
    }
}

/// <summary>
///     Enqueues a whole model × KV-type matrix against one project. Per-item outcomes, not all-or-nothing: one
///     ineligible model must not cost the operator the other nine cells.
/// </summary>
public sealed class StartBenchmarkRunBatchEndpoint : Endpoint<StartBenchmarkRunBatchRequest, StartBenchmarkRunBatchResponse>
{
    private const int MaxItems = 50;
    private readonly IBenchmarkRunBatchService _batches;

    public StartBenchmarkRunBatchEndpoint(IBenchmarkRunBatchService batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        _batches = batches;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.ProjectRunsBatch);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<StartBenchmarkRunBatchResponse>(StatusCodes.Status200OK)
                                      .ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartBenchmarkRunBatchRequest req, CancellationToken ct)
    {
        if (req.Items.Count is 0 or > MaxItems)
        {
            AddError($"A batch must carry between 1 and {MaxItems} items.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var result = await _batches.StartAsync(new BenchmarkRunBatchRequest
        {
            ProjectId = req.ProjectId,
            ExpectedProjectVersion = req.ExpectedProjectVersion,
            Items = [.. req.Items.Select(static item => new BenchmarkRunBatchItem { ModelName = item.ModelName, KvCacheType = item.KvCacheType })],
            RepeatCount = req.RepeatCount,
            Warmup = req.Warmup,
            RepeatMode = req.RepeatMode,
            AnswerVarianceTemperature = req.AnswerVarianceTemperature
        }, ct);
        await Send.OkAsync(new StartBenchmarkRunBatchResponse
                  {
                      ProjectVersion = result.ProjectVersion,
                      Started =
                      [
                          .. result.Started.Select(static item => new StartedBenchmarkRunBatchItemResponse
                          {
                              ModelName = item.ModelName,
                              KvCacheType = item.KvCacheType,
                              RunIds = item.RunIds
                          })
                      ],
                      Rejected = [.. result.Rejected.Select(ToResponse)]
                  }, ct);
    }

    private static RejectedBenchmarkRunBatchItemResponse ToResponse(BenchmarkRunBatchRejectedItem item)
    {
        var (code, message) = item.Kind switch
        {
            BenchmarkRunBatchRejectionKind.NotAttempted => (BenchmarkErrorCode.NotAttempted, item.Message),
            BenchmarkRunBatchRejectionKind.TimeBudget => (BenchmarkErrorCode.BatchTimeBudget, item.Message),
            _ when item.Failure is NotSupportedException => (BenchmarkErrorCode.UnsupportedSnapshot, item.Message),
            _ when item.Failure is not null => Classify(item.Failure),
            _ => throw new InvalidOperationException("A failed benchmark batch item must carry its failure.")
        };

        return new RejectedBenchmarkRunBatchItemResponse
        {
            ModelName = item.ModelName,
            KvCacheType = item.KvCacheType,
            Code = code.ToString(),
            Message = message
        };
    }

    private static (BenchmarkErrorCode Code, string Message) Classify(Exception exception)
    {
        var (_, code, message) = BenchmarkEndpointSupport.Classify(exception);
        return (code, message);
    }
}

public sealed class GetBenchmarkRunEndpoint : Endpoint<BenchmarkRunRouteRequest, BenchmarkRunDetailResponse>
{
    private readonly BenchmarkRecordService _records;

    public GetBenchmarkRunEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BenchmarkRunRouteRequest req, CancellationToken ct)
    {
        var run = await _records.GetRunAsync(req.RunId, ct);
        if (run is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark run was not found.")));
            return;
        }

        // A detail response is the only place the verdict is decrypted: a list of runs must not decrypt one blob per row.
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_records, run, ct),
                      BenchmarkEndpointSupport.ExpectedKldDigest(await _records.GetProjectAsync(run.ProjectId, ct))), ct);
    }
}

public sealed class DeleteBenchmarkRunEndpoint : Endpoint<DeleteBenchmarkRunRequest>
{
    private readonly BenchmarkRecordService _records;

    public DeleteBenchmarkRunEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Benchmarks.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DeleteBenchmarkRunRequest req, CancellationToken ct)
    {
        await _records.DeleteRunAsync(req.RunId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
    }
}

public sealed class CancelBenchmarkRunEndpoint : Endpoint<CancelBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkCancellationService _cancellation;
    private readonly BenchmarkRecordService _records;

    public CancelBenchmarkRunEndpoint(IBenchmarkCancellationService cancellation, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        ArgumentNullException.ThrowIfNull(records);
        _cancellation = cancellation;
        _records = records;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.RunCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancelBenchmarkRunRequest req, CancellationToken ct)
    {
        var run = await _cancellation.CancelAsync(req.RunId, req.ExpectedVersion, req.Target, ct);
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_records, run, ct),
                      BenchmarkEndpointSupport.ExpectedKldDigest(await _records.GetProjectAsync(run.ProjectId, ct))), ct);
    }
}

public sealed class ScoreBenchmarkRunEndpoint : Endpoint<ScoreBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly BenchmarkRecordService _records;

    public ScoreBenchmarkRunEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Benchmarks.RunScore);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ScoreBenchmarkRunRequest req, CancellationToken ct)
    {
        // An omitted score is a 400, never a silent 0: zero is a valid operator verdict now.
        if (req.Score is not { } score)
        {
            AddError("Score is required and must be between 0 and 100.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var run = await _records.SetUserScoreAsync(req.RunId, score, req.ExpectedVersion, ct);
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_records, run, ct),
                      BenchmarkEndpointSupport.ExpectedKldDigest(await _records.GetProjectAsync(run.ProjectId, ct))), ct);
    }
}

/// <summary>Clears the operator override, so the run ranks by its judge score again (or not at all).</summary>
public sealed class ClearBenchmarkRunScoreEndpoint : Endpoint<ClearBenchmarkRunScoreRequest, BenchmarkRunDetailResponse>
{
    private readonly BenchmarkRecordService _records;

    public ClearBenchmarkRunScoreEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Benchmarks.RunScore);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ClearBenchmarkRunScoreRequest req, CancellationToken ct)
    {
        var run = await _records.SetUserScoreAsync(req.RunId, score: null, req.ExpectedVersion, ct);
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_records, run, ct),
                      BenchmarkEndpointSupport.ExpectedKldDigest(await _records.GetProjectAsync(run.ProjectId, ct))), ct);
    }
}

/// <summary>Judges one succeeded run again under the project's current policy.</summary>
public sealed class RejudgeBenchmarkRunEndpoint : Endpoint<RejudgeBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkProjectService _projects;
    private readonly BenchmarkRecordService _records;

    public RejudgeBenchmarkRunEndpoint(IBenchmarkProjectService projects, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(records);
        _projects = projects;
        _records = records;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.RunRejudge);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(RejudgeBenchmarkRunRequest req, CancellationToken ct)
    {
        _ = await _projects.RejudgeRunAsync(req.RunId, req.ExpectedVersion, req.Force, ct);
        var run = await _records.GetRunAsync(req.RunId, ct)
                  ?? throw new BenchmarkNotFoundException("Benchmark run was not found.");
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_records, run, ct),
                      BenchmarkEndpointSupport.ExpectedKldDigest(await _records.GetProjectAsync(run.ProjectId, ct))), ct);
    }
}
