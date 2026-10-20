namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>Bounds the page before it becomes a skip; the store refuses an unknown state and a take outside 1..200.</summary>
public sealed class ListBenchmarkReviewsRequestValidator : Validator<ListBenchmarkReviewsRequest>
{
    private const int MaxPage = 1_000_000;

    public ListBenchmarkReviewsRequestValidator()
    {
        this.AddFirstViolationRule(static request => request.Page is < 1 or > MaxPage ? "Page must be between 1 and 1000000." : null);
    }
}
