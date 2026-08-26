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
public sealed class StartBenchmarkRunBatchEndpoint(IBenchmarkRunFreezeService runs, TimeProvider timeProvider)
    : Endpoint<StartBenchmarkRunBatchRequest, StartBenchmarkRunBatchResponse>
{
    /// <summary>Matrix ceiling. Ten models × four KV types is already a very long night; beyond that is a mistake.</summary>
    private const int MaxItems = 50;

    /// <summary>
    ///     How long the request keeps freezing cells before it answers with what it has. The freeze is synchronous per
    ///     cell by design — no background job — so without a budget a fifty-cell matrix over cold, unverified models
    ///     holds the connection until something times out and the operator cannot tell which cells started. With one,
    ///     the answer always names every started cell and every cell to resubmit.
    ///     <para>
    ///         Checked BETWEEN cells, never inside one: a cell that has begun freezing runs to completion, so the
    ///         budget can be overrun by one cell and no cell is ever half-frozen.
    ///     </para>
    /// </summary>
    private static readonly TimeSpan RequestTimeBudget = TimeSpan.FromSeconds(45);

    private readonly IBenchmarkRunFreezeService _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.ProjectRunsBatch);
        Policies(NodeAuthorizationPolicies.Operator);

        // 400 can come from BOTH an AddError body and a Results.Problem body here, so it is declared as the permissive
        // ASP.NET shape, which validates either; 404/409 are Results.Problem bodies from the same helper.
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

        var started = new List<StartedBenchmarkRunBatchItemResponse>(req.Items.Count);
        var rejected = new List<RejectedBenchmarkRunBatchItemResponse>();

        // One scope for the whole matrix: the llama-server capability probe runs once instead of once per cell, and
        // each distinct model is verified once and then held. Holding the verified leases is the point — a model that
        // changed halfway through would give the later cells a different snapshot from the earlier ones, which is the
        // variable a matrix exists to hold still.
        await using var scope = new BenchmarkFreezeScope();
        var startedAt = _timeProvider.GetTimestamp();

        // Every insert bumps the project version by exactly one, so the version the NEXT item must present is the
        // running total of runs created so far. Re-reading the project between items would be the same number with an
        // extra round trip and a wider race window.
        var expectedVersion = req.ExpectedProjectVersion;
        for (var index = 0; index < req.Items.Count; index++)
        {
            var item = req.Items[index];

            // The budget is spent: answer with what started rather than holding the connection. Every remaining cell
            // is reported by name so the operator resubmits exactly those, against the version below.
            if (_timeProvider.GetElapsedTime(startedAt) >= RequestTimeBudget)
            {
                var exhausted = $"The batch reached its {RequestTimeBudget.TotalSeconds:0} second time budget after {started.Count} cell(s) started; "
                                + "resubmit the remaining items with the project version in this response.";
                for (var untried = index; untried < req.Items.Count; untried++)
                {
                    rejected.Add(Rejection(req.Items[untried], BenchmarkErrorCode.BatchTimeBudget, exhausted));
                }

                break;
            }

            // Same rule as the single-run endpoint, as a per-cell verdict: a blank name would reach the freeze and come
            // back as an ArgumentException, i.e. a 500 for what is one operator typo in one cell of a matrix.
            if (string.IsNullOrWhiteSpace(item.ModelName))
            {
                rejected.Add(Rejection(item, BenchmarkErrorCode.InvalidRequest, "A primary model is required."));
                continue;
            }

            if (!BenchmarkKvCacheType.TryNormalize(item.KvCacheType, out var kvCacheType))
            {
                rejected.Add(Rejection(item, BenchmarkErrorCode.InvalidRequest, "The requested KV-cache type is not supported."));
                continue;
            }

            try
            {
                var created = await _runs.StartAsync(new BenchmarkRunStartRequest(req.ProjectId,
                                                 item.ModelName,
                                                 expectedVersion,
                                                 kvCacheType,
                                                 req.RepeatCount,
                                                 req.Warmup,
                                                 req.RepeatMode,
                                                 req.AnswerVarianceTemperature), scope, ct)
                                             .ConfigureAwait(false);
                expectedVersion += created.Count;
                started.Add(new StartedBenchmarkRunBatchItemResponse
                {
                    ModelName = item.ModelName,
                    KvCacheType = kvCacheType,
                    RunIds = [.. created.Select(static run => run.Id)]
                });
            }
            catch (Exception exception) when (IsWholeBatchFailure(exception))
            {
                // A stale project version or a vanished project is a fact about the BATCH, not about one cell: every
                // remaining item would fail the same way, and reporting nine identical rejections would bury it. That
                // holds only while NOTHING has started — a top-level error carries no body, so once runs are queued it
                // would discard their ids: the operator could not find them and a retry would enqueue duplicates.
                if (started.Count == 0)
                {
                    await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
                    return;
                }

                var (_, code, message) = BenchmarkEndpointSupport.Classify(exception);
                rejected.Add(Rejection(item, code, message));
                var stopped =
                    $"The batch stopped after {started.Count} cell(s) started; re-read the project version and resubmit the remaining items.";
                for (var untried = index + 1; untried < req.Items.Count; untried++)
                {
                    rejected.Add(Rejection(req.Items[untried], BenchmarkErrorCode.NotAttempted, stopped));
                }

                break;
            }
            catch (Exception exception) when (BenchmarkEndpointSupport.IsHandled(exception) || exception is KeyNotFoundException)
            {
                var (_, code, message) = BenchmarkEndpointSupport.Classify(exception);
                rejected.Add(Rejection(item, code, message));
            }
            catch (NotSupportedException exception)
            {
                rejected.Add(Rejection(item, BenchmarkErrorCode.UnsupportedSnapshot, exception.Message));
            }
        }

        await Send.OkAsync(new StartBenchmarkRunBatchResponse
                  {
                      ProjectVersion = expectedVersion,
                      Started = started,
                      Rejected = rejected
                  }, ct)
                  .ConfigureAwait(false);
    }

    /// <summary>
    ///     Facts about the BATCH rather than about one cell: a vanished project, or a project version that moved under
    ///     the caller. Every remaining item would fail identically, and burying that in N identical rejections would
    ///     hide the one thing the operator has to fix. A <see cref="KeyNotFoundException" /> is deliberately NOT here —
    ///     that is the MODEL not being installed, which is exactly a per-cell verdict. Only fails the WHOLE batch while
    ///     nothing has started; after that the batch stops and answers partially, so the started run ids survive.
    /// </summary>
    private static bool IsWholeBatchFailure(Exception exception) =>
        exception is BenchmarkNotFoundException
        || (exception is BenchmarkConflictException conflict && string.Equals(conflict.Code, "VersionConflict", StringComparison.Ordinal));

    private static RejectedBenchmarkRunBatchItemResponse Rejection(StartBenchmarkRunBatchItem item, BenchmarkErrorCode code, string message) =>
        new()
        {
            ModelName = item.ModelName,
            KvCacheType = item.KvCacheType,
            Code = code.ToString(),
            Message = message
        };
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
