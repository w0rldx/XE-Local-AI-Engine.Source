namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class UpdateBenchmarkProjectEndpoint : Endpoint<UpdateBenchmarkProjectRequest, BenchmarkProjectDetailResponse>
{
    private readonly IBenchmarkProjectService _projects;
    private readonly BenchmarkRecordService _records;

    public UpdateBenchmarkProjectEndpoint(IBenchmarkProjectService projects, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(records);
        _projects = projects;
        _records = records;
    }

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
        var project = await _projects.UpdateAsync(req.ProjectId, req.ExpectedVersion, req.ToDraft(req.ProjectId), ct);
        await Send.OkAsync(await BenchmarkProjectDetailProjection.ReadAsync(_records, project, runCount: 0, ct), ct);
    }
}
