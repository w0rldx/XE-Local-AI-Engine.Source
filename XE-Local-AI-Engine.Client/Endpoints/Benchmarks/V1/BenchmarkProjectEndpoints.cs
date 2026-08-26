namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class ListBenchmarkProjectsEndpoint(IBenchmarkStore store)
    : EndpointWithoutRequest<ListBenchmarkProjectsResponse>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var projects = await _store.ListProjectsAsync(ct).ConfigureAwait(false);
        var items = new List<BenchmarkProjectSummaryResponse>(projects.Count);
        foreach (var project in projects)
        {
            var count = await _store.CountRunsAsync(project.Id, ct).ConfigureAwait(false);
            items.Add(project.ToSummary(count));
        }

        await Send.OkAsync(new ListBenchmarkProjectsResponse
        {
            Items = items
        }, ct).ConfigureAwait(false);
    }
}

public sealed class CreateBenchmarkProjectEndpoint(IBenchmarkProjectService projects, IBenchmarkStore store)
    : Endpoint<BenchmarkProjectMutationRequest, BenchmarkProjectDetailResponse>
{
    private readonly IBenchmarkProjectService _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(BenchmarkProjectMutationRequest req, CancellationToken ct)
    {
        try
        {
            var project = await _projects.CreateAsync(req.ToDraft(Guid.Empty), ct).ConfigureAwait(false);
            await Send.CreatedAtAsync<GetBenchmarkProjectEndpoint>(new
                          {
                              projectId = project.Id
                          }, project.ToDetail(runCount: 0, await BenchmarkJudgePolicyProjection.ReadAsync(_store, project.Id, ct).ConfigureAwait(false)),
                          cancellation: ct)
                      .ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

public sealed class GetBenchmarkProjectEndpoint(IBenchmarkStore store)
    : Endpoint<BenchmarkProjectRouteRequest, BenchmarkProjectDetailResponse>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BenchmarkProjectRouteRequest req, CancellationToken ct)
    {
        var project = await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false);
        if (project is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var runCount = await _store.CountRunsAsync(project.Id, ct).ConfigureAwait(false);
        await Send.OkAsync(project.ToDetail(runCount, await BenchmarkJudgePolicyProjection.ReadAsync(_store, project.Id, ct).ConfigureAwait(false)), ct)
                  .ConfigureAwait(false);
    }
}

public sealed class UpdateBenchmarkProjectEndpoint(IBenchmarkProjectService projects, IBenchmarkStore store)
    : Endpoint<UpdateBenchmarkProjectRequest, BenchmarkProjectDetailResponse>
{
    private readonly IBenchmarkProjectService _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Put(LocalApiRoutes.Benchmarks.ProjectById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict)
                                      .ProducesProblem(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(UpdateBenchmarkProjectRequest req, CancellationToken ct)
    {
        try
        {
            var project = await _projects.UpdateAsync(req.ProjectId, req.ExpectedVersion, req.ToDraft(req.ProjectId), ct).ConfigureAwait(false);
            await Send.OkAsync(project.ToDetail(runCount: 0, await BenchmarkJudgePolicyProjection.ReadAsync(_store, project.Id, ct).ConfigureAwait(false)), ct)
                      .ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

public sealed class DeleteBenchmarkProjectEndpoint(IBenchmarkStore store)
    : Endpoint<DeleteBenchmarkProjectRequest>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Benchmarks.ProjectById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DeleteBenchmarkProjectRequest req, CancellationToken ct)
    {
        try
        {
            await _store.DeleteProjectAsync(req.ProjectId, req.ExpectedVersion, ct).ConfigureAwait(false);
            await Send.NoContentAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

/// <summary>
///     Changes the judge on a project that may already be frozen. The judge is the one frozen-project knob an operator
///     can still turn, and turning it re-scores every run — so it is its own resource with its own confirmation, never
///     a field that rides along on the project PUT.
/// </summary>
public sealed class UpdateBenchmarkJudgePolicyEndpoint(IBenchmarkProjectService projects, IBenchmarkStore store)
    : Endpoint<UpdateBenchmarkJudgePolicyRequest, BenchmarkJudgeChangeResponse>
{
    private readonly IBenchmarkProjectService _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Put(LocalApiRoutes.Benchmarks.ProjectJudge);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict)
                                      .ProducesProblem(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(UpdateBenchmarkJudgePolicyRequest req, CancellationToken ct)
    {
        try
        {
            var draft = req.Policy is null
                ? null
                : new BenchmarkJudgePolicyDraft(req.Policy.ModelName,
                    req.Policy.ContextTokens,
                    req.Policy.Rubric.ToRubric(),
                    req.Policy.ReferenceAnswer,
                    req.Policy.Mode);
            var change = await _projects.UpdateJudgePolicyAsync(req.ProjectId, req.ExpectedVersion, draft, req.ConfirmRejudge, ct).ConfigureAwait(false);
            await Send.OkAsync(await ToResponseAsync(_store, change, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }

    internal static async Task<BenchmarkJudgeChangeResponse> ToResponseAsync(IBenchmarkStore store,
        BenchmarkJudgePolicyChange change,
        CancellationToken ct)
    {
        var runCount = await store.CountRunsAsync(change.Project.Id, ct).ConfigureAwait(false);
        return new BenchmarkJudgeChangeResponse
        {
            Project = change.Project.ToDetail(runCount, await BenchmarkJudgePolicyProjection.ReadAsync(store, change.Project.Id, ct).ConfigureAwait(false)),
            EnqueuedRunIds = change.EnqueuedRunIds,
            CohortGeneration = change.CohortGeneration
        };
    }
}

/// <summary>Moves the project's rank cohort to the current judge runtime by re-judging every succeeded run.</summary>
public sealed class RejudgeBenchmarkProjectEndpoint(IBenchmarkProjectService projects, IBenchmarkStore store)
    : Endpoint<RejudgeBenchmarkProjectRequest, BenchmarkJudgeChangeResponse>
{
    private readonly IBenchmarkProjectService _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.ProjectRejudge);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(RejudgeBenchmarkProjectRequest req, CancellationToken ct)
    {
        try
        {
            var change = await _projects.RejudgeProjectAsync(req.ProjectId, req.ExpectedVersion, ct).ConfigureAwait(false);
            await Send.OkAsync(await UpdateBenchmarkJudgePolicyEndpoint.ToResponseAsync(_store, change, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (BenchmarkExceptionFilter.IsHandled(exception))
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(exception)).ConfigureAwait(false);
        }
    }
}

/// <summary>The rubrics the judge-policy form offers, so the UI never carries a second copy of the wording.</summary>
public sealed class GetBenchmarkRubricPresetsEndpoint : EndpointWithoutRequest<BenchmarkRubricPresetsResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.RubricPresets);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new BenchmarkRubricPresetsResponse
        {
            Default = BenchmarkJudgeRubricDefaults.Default().ToDto(),
            Programming = BenchmarkJudgeRubricDefaults.Programming().ToDto(),
            Reasoning = BenchmarkJudgeRubricDefaults.Reasoning().ToDto(),
            Verifiable = BenchmarkJudgeRubricDefaults.Verifiable().ToDto()
        }, ct);
}

/// <summary>Reads and decrypts a project's current judge policy for the wire.</summary>
internal static class BenchmarkJudgePolicyProjection
{
    public static async Task<BenchmarkJudgePolicyResponse> ReadAsync(IBenchmarkStore store, Guid projectId, CancellationToken ct)
    {
        var revision = await store.GetCurrentJudgePolicyRevisionAsync(projectId, ct).ConfigureAwait(false);
        var policy = revision?.PolicyJson is { } payload && !payload.IsEmpty
            ? BenchmarkJudgeSerialization.DeserializePolicy(payload.Span)
            : null;
        return BenchmarkEndpointMapper.ToJudgePolicy(revision, policy);
    }
}

internal static class BenchmarkExceptionFilter
{
    public static bool IsHandled(Exception exception) =>
        exception is BenchmarkNotFoundException or BenchmarkValidationException or BenchmarkConflictException or BenchmarkEligibilityException
            or BenchmarkUnsupportedKvCacheTypeException or BenchmarkJudgePolicyChangedException;
}
