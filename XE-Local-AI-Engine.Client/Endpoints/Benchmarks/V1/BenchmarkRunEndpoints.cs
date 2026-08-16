namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text.Json;
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

        if (await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var page = await _store.ListRunsAsync(req.ProjectId,
                               (req.Page - 1) * req.PageSize,
                               req.PageSize,
                               req.ModelGroupKey,
                               req.IncludeUnscored,
                               ct)
                           .ConfigureAwait(false);
        await Send.OkAsync(new ListBenchmarkRunsResponse
                  {
                      Items = page.Items.Select(static run => run.ToSummary()).ToArray(),
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
            var run = await _runs.StartAsync(req.ProjectId, req.ModelName, req.ExpectedProjectVersion, kvCacheType, ct).ConfigureAwait(false);
            await Send.ResultAsync(Results.Accepted(value: run.ToDetail())).ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception) || exception is KeyNotFoundException)
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

        // The detail view is the only place the verdict is decrypted: a list of runs must not decrypt one blob per row.
        await Send.OkAsync(run.ToDetail(await ReadVerdictAsync(run, ct).ConfigureAwait(false)), ct).ConfigureAwait(false);
    }

    private async Task<BenchmarkJudgeResultV2?> ReadVerdictAsync(BenchmarkRunRecord run, CancellationToken ct)
    {
        if (run.Judge?.AttemptId is not { } attemptId)
        {
            return null;
        }

        var attempt = await _store.GetJudgeAttemptAsync(attemptId, ct).ConfigureAwait(false);
        return attempt?.ResultJson is { } payload && !payload.IsEmpty
            ? JsonSerializer.Deserialize<BenchmarkJudgeResultV2>(payload.Span, JsonSerializerOptions.Web)
            : null;
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
        try
        {
            await _store.DeleteRunAsync(req.RunId, req.ExpectedVersion, ct).ConfigureAwait(false);
            await Send.NoContentAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

public sealed class CancelBenchmarkRunEndpoint(IBenchmarkCancellationService cancellation)
    : Endpoint<CancelBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly IBenchmarkCancellationService _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.RunCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancelBenchmarkRunRequest req, CancellationToken ct)
    {
        try
        {
            var run = await _cancellation.CancelAsync(req.RunId, req.ExpectedVersion, req.Target, ct).ConfigureAwait(false);
            await Send.OkAsync(run.ToDetail(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
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

        try
        {
            var run = await _store.SetUserScoreAsync(req.RunId, score, req.ExpectedVersion, ct).ConfigureAwait(false);
            await Send.OkAsync(run.ToDetail(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
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
        try
        {
            var run = await _store.SetUserScoreAsync(req.RunId, score: null, req.ExpectedVersion, ct).ConfigureAwait(false);
            await Send.OkAsync(run.ToDetail(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
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
        try
        {
            _ = await _projects.RejudgeRunAsync(req.RunId, req.ExpectedVersion, req.Force, ct).ConfigureAwait(false);
            var run = await _store.GetRunAsync(req.RunId, ct).ConfigureAwait(false)
                      ?? throw new BenchmarkNotFoundException("Benchmark run was not found.");
            await Send.OkAsync(run.ToDetail(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}
