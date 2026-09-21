namespace XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;

/// <summary>
///     What the create dialog pre-fills from one training run: the base and tuned model names its lineage implies, and
///     the evaluations that already exist for them. Read-only — it creates nothing.
/// </summary>
public sealed class SuggestComparisonEndpoint : Endpoint<SuggestComparisonRequest, ComparisonSuggestionResponse>
{
    private readonly IComparisonReportService _comparisons;

    public SuggestComparisonEndpoint(IComparisonReportService comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        _comparisons = comparisons;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.ComparisonSuggest);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ComparisonSuggestionResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(SuggestComparisonRequest req, CancellationToken ct)
    {
        var suggestion = await _comparisons.SuggestAsync(req.TrainingRunId, ct);
        await Send.OkAsync(suggestion.ToResponse(), ct);
    }
}
