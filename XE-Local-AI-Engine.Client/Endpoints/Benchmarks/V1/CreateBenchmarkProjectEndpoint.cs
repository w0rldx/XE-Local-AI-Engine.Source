namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class CreateBenchmarkProjectEndpoint : Endpoint<BenchmarkProjectMutationRequest, BenchmarkProjectDetailResponse>
{
    private readonly IBenchmarkProjectService _projects;
    private readonly BenchmarkRecordService _records;

    public CreateBenchmarkProjectEndpoint(IBenchmarkProjectService projects, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(records);
        _projects = projects;
        _records = records;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status422UnprocessableEntity));
    }

    public override async Task HandleAsync(BenchmarkProjectMutationRequest req, CancellationToken ct)
    {
        var project = await _projects.CreateAsync(req.ToDraft(Guid.Empty), ct);
        await Send.CreatedAtAsync<GetBenchmarkProjectEndpoint>(new
            {
                projectId = project.Id
            }, await BenchmarkProjectDetailProjection.ReadAsync(_records, project, runCount: 0, ct),
            cancellation: ct);
    }
}
