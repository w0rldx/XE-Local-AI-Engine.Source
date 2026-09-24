namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

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
