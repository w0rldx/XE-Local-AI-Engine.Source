namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>Bounds the page window before the project is read.</summary>
/// <remarks>
///     The route's 404 is written through <c>BenchmarkEndpointSupport</c>, a different body shape; this refusal ran
///     ahead of it in the handler and still does, so an out-of-bounds page on an unknown project answers 400.
/// </remarks>
public sealed class ListBenchmarkRunsRequestValidator : Validator<ListBenchmarkRunsRequest>
{
    private const int MaxPageSize = 200;

    public ListBenchmarkRunsRequestValidator()
    {
        this.AddFirstViolationRule(static request => request.Page < 1 || request.PageSize is < 1 or > MaxPageSize
            ? "Page must be positive and pageSize must be between 1 and 200."
            : null);
    }
}
