namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class ScoreBenchmarkRunEndpoint : Endpoint<ScoreBenchmarkRunRequest, BenchmarkRunDetailResponse>
{
    private readonly BenchmarkRecordService _records;

    public ScoreBenchmarkRunEndpoint(BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records = records;
    }

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
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var run = await _records.SetUserScoreAsync(req.RunId, score, req.ExpectedVersion, ct);
        await Send.OkAsync(run.ToDetail(await BenchmarkEndpointSupport.ReadVerdictAsync(_records, run, ct),
                      BenchmarkEndpointSupport.ExpectedKldDigest(await _records.GetProjectAsync(run.ProjectId, ct))), ct);
    }
}
