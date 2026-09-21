namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>Deletes a project with its runs and all their evidence.</summary>
/// <remarks>
///     Answers 409 <c>ActiveRun</c>, having deleted nothing, while any of the project's runs is still queued,
///     generating, judging or being compared — a finished run goes, a live one blocks the whole call.
/// </remarks>
public sealed class DeleteBenchmarkProjectEndpoint : Endpoint<DeleteBenchmarkProjectRequest>
{
    private readonly BenchmarkRecordService _records;

    public DeleteBenchmarkProjectEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Benchmarks.ProjectById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DeleteBenchmarkProjectRequest req, CancellationToken ct)
    {
        await _records.DeleteProjectAsync(req.ProjectId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
    }
}
