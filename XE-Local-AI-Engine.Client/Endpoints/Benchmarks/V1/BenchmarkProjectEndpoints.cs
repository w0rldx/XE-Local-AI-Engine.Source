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
            var count = (await _store.ListRunsAsync(project.Id, ct).ConfigureAwait(false)).Count;
            items.Add(project.ToSummary(count));
        }

        await Send.OkAsync(new ListBenchmarkProjectsResponse
        {
            Items = items
        }, ct).ConfigureAwait(false);
    }
}

public sealed class CreateBenchmarkProjectEndpoint(IBenchmarkProjectService projects)
    : Endpoint<BenchmarkProjectMutationRequest, BenchmarkProjectDetailResponse>
{
    private readonly IBenchmarkProjectService _projects = projects ?? throw new ArgumentNullException(nameof(projects));

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(BenchmarkProjectMutationRequest req, CancellationToken ct)
    {
        try
        {
            var project = await _projects.CreateAsync(req.ToDraft(Guid.Empty), ct).ConfigureAwait(false);
            await Send.CreatedAtAsync<GetBenchmarkProjectEndpoint>(new
                      {
                          projectId = project.Id
                      }, project.ToDetail(runCount: 0), cancellation: ct)
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
    }

    public override async Task HandleAsync(BenchmarkProjectRouteRequest req, CancellationToken ct)
    {
        var project = await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false);
        if (project is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var runCount = (await _store.ListRunsAsync(project.Id, ct).ConfigureAwait(false)).Count;
        await Send.OkAsync(project.ToDetail(runCount), ct).ConfigureAwait(false);
    }
}

public sealed class UpdateBenchmarkProjectEndpoint(IBenchmarkProjectService projects)
    : Endpoint<UpdateBenchmarkProjectRequest, BenchmarkProjectDetailResponse>
{
    private readonly IBenchmarkProjectService _projects = projects ?? throw new ArgumentNullException(nameof(projects));

    public override void Configure()
    {
        Put(LocalApiRoutes.Benchmarks.ProjectById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateBenchmarkProjectRequest req, CancellationToken ct)
    {
        try
        {
            var project = await _projects.UpdateAsync(req.ProjectId, req.ExpectedVersion, req.ToDraft(req.ProjectId), ct).ConfigureAwait(false);
            await Send.OkAsync(project.ToDetail(runCount: 0), ct).ConfigureAwait(false);
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

internal static class BenchmarkExceptionFilter
{
    public static bool IsHandled(Exception exception) =>
        exception is BenchmarkNotFoundException or BenchmarkValidationException or BenchmarkConflictException or BenchmarkEligibilityException;
}
