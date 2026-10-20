namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

/// <summary>Records a reviewer's decision on one scoring run of a review project; the latest decision wins under the run-version check.</summary>
public sealed class SubmitBenchmarkRunReviewEndpoint : Endpoint<SubmitBenchmarkRunReviewRequest, BenchmarkRunDetailResponse>
{
    private readonly BenchmarkRecordService _records;
    private readonly IBenchmarkReviewService _reviews;

    public SubmitBenchmarkRunReviewEndpoint(IBenchmarkReviewService reviews, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(records);
        _reviews = reviews;
        _records = records;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Benchmarks.RunReview);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(SubmitBenchmarkRunReviewRequest req, CancellationToken ct)
    {
        var run = await _reviews.SubmitAsync(new BenchmarkReviewCommand
        {
            RunId = req.RunId,
            ExpectedRunVersion = req.ExpectedVersion,
            State = req.State,
            Score = req.Score,
            Criteria = req.Criteria?.Select(static criterion => new BenchmarkReviewCriterionScore
                          {
                              CriterionId = criterion.CriterionId,
                              Score = criterion.Score ?? 0
                          })
                          .ToArray(),
            Notes = req.Notes
        }, ct);
        await Send.OkAsync(await BenchmarkEndpointSupport.ToDetailAsync(_records, run, ct), ct);
    }
}
