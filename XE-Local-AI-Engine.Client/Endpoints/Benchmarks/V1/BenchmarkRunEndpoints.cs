namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class ListBenchmarkRunsEndpoint(IBenchmarkStore store)
    : Endpoint<ListBenchmarkRunsRequest, ListBenchmarkRunsResponse>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

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
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var project = await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false);
        if (project is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var expectedKldDigest = BenchmarkEndpointSupport.ExpectedKldDigest(project);

        var page = await _store.ListRunsAsync(req.ProjectId,
                                   (req.Page - 1) * req.PageSize,
                                   req.PageSize,
                                   req.ModelContentFingerprint,
                                   req.IncludeUnscored,
                                   ct)
                               .ConfigureAwait(false);
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
        }, ct)
                  .ConfigureAwait(false);
    }
}

public sealed class StartBenchmarkRunEndpoint(IBenchmarkRunFreezeService runs)
    : Endpoint<StartBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkRunFreezeService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

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
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        // Absent/blank is Auto and stays null; anything outside the allow-list is the caller's mistake, not an
        // unsupported runtime, so it is a 400 here rather than the 422 an unlaunchable-but-known type gets.
        if (!BenchmarkKvCacheType.TryNormalize(req.KvCacheType, out var kvCacheType))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Problem(StatusCodes.Status400BadRequest,
                BenchmarkErrorCode.InvalidRequest,
                "The requested KV-cache type is not supported.")).ConfigureAwait(false);
            return;
        }

        try
        {
            // The FIRST run of the group is the response: it is the one that starts, so it is the one an operator's
            // live pane should open on. The rest are reachable through its repeatGroupId.
            var created = await _runs.StartAsync(new BenchmarkRunStartRequest(req.ProjectId,
                                             req.ModelName,
                                             req.ExpectedProjectVersion,
                                             kvCacheType,
                                             req.RepeatCount,
                                             req.Warmup,
                                             req.RepeatMode,
                                             req.AnswerVarianceTemperature), scope: null, ct)
                                     .ConfigureAwait(false);
            await Send.ResultAsync(Results.Accepted(value: created[0].ToDetail())).ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
        catch (NotSupportedException exception)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Problem(StatusCodes.Status422UnprocessableEntity,
                BenchmarkErrorCode.UnsupportedSnapshot,
                exception.Message)).ConfigureAwait(false);
        }
    }
}

/// <summary>
///     Enqueues a whole model × KV-type matrix against one project. Per-item outcomes, not all-or-nothing: one
///     ineligible model must not cost the operator the other nine cells.
/// </summary>
public sealed class StartBenchmarkRunBatchEndpoint(IBenchmarkRunBatchService batches)
    : Endpoint<StartBenchmarkRunBatchRequest, StartBenchmarkRunBatchResponse>
{
    private const int MaxItems = 50;
    private readonly IBenchmarkRunBatchService _batches = batches ?? throw new ArgumentNullException(nameof(batches));

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
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var result = await _batches.StartAsync(new BenchmarkRunBatchRequest(req.ProjectId,
                                                 req.ExpectedProjectVersion,
                                                 [.. req.Items.Select(static item => new BenchmarkRunBatchItem(item.ModelName, item.KvCacheType))],
                                                 req.RepeatCount,
                                                 req.Warmup,
                                                 req.RepeatMode,
                                                 req.AnswerVarianceTemperature), ct)
                                   .ConfigureAwait(false);
        await Send.OkAsync(new StartBenchmarkRunBatchResponse
        {
            ProjectVersion = result.ProjectVersion,
            Started = [.. result.Started.Select(static item => new StartedBenchmarkRunBatchItemResponse
                      {
                          ModelName = item.ModelName,
                          KvCacheType = item.KvCacheType,
                          RunIds = item.RunIds
                      })],
            Rejected = [.. result.Rejected.Select(ToResponse)]
        }, ct)
                  .ConfigureAwait(false);
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

public sealed class GetBenchmarkRunEndpoint(IBenchmarkStore store)
    : Endpoint<BenchmarkRunRouteRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BenchmarkRunRouteRequest req, CancellationToken ct)
    {
        var run = await _store.GetRunAsync(req.RunId, ct).ConfigureAwait(false);
        if (run is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark run was not found."))).ConfigureAwait(false);
            return;
        }

        // A detail response is the only place the verdict is decrypted: a list of runs must not decrypt one blob per row.
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_store, run, ct).ConfigureAwait(false),
                      BenchmarkEndpointSupport.ExpectedKldDigest(await _store.GetProjectAsync(run.ProjectId, ct).ConfigureAwait(false))), ct)
                  .ConfigureAwait(false);
    }
}

public sealed class DeleteBenchmarkRunEndpoint(IBenchmarkStore store)
    : Endpoint<DeleteBenchmarkRunRequest>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Benchmarks.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DeleteBenchmarkRunRequest req, CancellationToken ct)
    {
        await _store.DeleteRunAsync(req.RunId, req.ExpectedVersion, ct).ConfigureAwait(false);
        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}

public sealed class CancelBenchmarkRunEndpoint(IBenchmarkCancellationService cancellation, IBenchmarkStore store)
    : Endpoint<CancelBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkCancellationService _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.RunCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancelBenchmarkRunRequest req, CancellationToken ct)
    {
        var run = await _cancellation.CancelAsync(req.RunId, req.ExpectedVersion, req.Target, ct).ConfigureAwait(false);
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_store, run, ct).ConfigureAwait(false),
                  BenchmarkEndpointSupport.ExpectedKldDigest(await _store.GetProjectAsync(run.ProjectId, ct).ConfigureAwait(false))), ct)
              .ConfigureAwait(false);
    }
}

public sealed class ScoreBenchmarkRunEndpoint(IBenchmarkStore store)
    : Endpoint<ScoreBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

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
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var run = await _store.SetUserScoreAsync(req.RunId, score, req.ExpectedVersion, ct).ConfigureAwait(false);
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_store, run, ct).ConfigureAwait(false),
                  BenchmarkEndpointSupport.ExpectedKldDigest(await _store.GetProjectAsync(run.ProjectId, ct).ConfigureAwait(false))), ct)
              .ConfigureAwait(false);
    }
}

/// <summary>Clears the operator override, so the run ranks by its judge score again (or not at all).</summary>
public sealed class ClearBenchmarkRunScoreEndpoint(IBenchmarkStore store)
    : Endpoint<ClearBenchmarkRunScoreRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Benchmarks.RunScore);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ClearBenchmarkRunScoreRequest req, CancellationToken ct)
    {
        var run = await _store.SetUserScoreAsync(req.RunId, score: null, req.ExpectedVersion, ct).ConfigureAwait(false);
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_store, run, ct).ConfigureAwait(false),
                  BenchmarkEndpointSupport.ExpectedKldDigest(await _store.GetProjectAsync(run.ProjectId, ct).ConfigureAwait(false))), ct)
              .ConfigureAwait(false);
    }
}

/// <summary>Judges one succeeded run again under the project's current policy.</summary>
public sealed class RejudgeBenchmarkRunEndpoint(IBenchmarkProjectService projects, IBenchmarkStore store)
    : Endpoint<RejudgeBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkProjectService _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.RunRejudge);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(RejudgeBenchmarkRunRequest req, CancellationToken ct)
    {
        _ = await _projects.RejudgeRunAsync(req.RunId, req.ExpectedVersion, req.Force, ct).ConfigureAwait(false);
        var run = await _store.GetRunAsync(req.RunId, ct).ConfigureAwait(false)
                  ?? throw new BenchmarkNotFoundException("Benchmark run was not found.");
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_store, run, ct).ConfigureAwait(false),
                  BenchmarkEndpointSupport.ExpectedKldDigest(await _store.GetProjectAsync(run.ProjectId, ct).ConfigureAwait(false))), ct)
              .ConfigureAwait(false);
    }
}
