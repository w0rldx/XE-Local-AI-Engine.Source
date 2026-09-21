namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

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
