namespace XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.V1;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;

public sealed class DeleteComparisonEndpoint : Endpoint<DeleteComparisonRequest>
{
    private readonly IComparisonReportService _comparisons;

    public DeleteComparisonEndpoint(IComparisonReportService comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        _comparisons = comparisons;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Training.ComparisonById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces(StatusCodes.Status204NoContent)
                               .Produces<TrainingErrorResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DeleteComparisonRequest req, CancellationToken ct)
    {
        await _comparisons.DeleteAsync(req.ComparisonId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
    }
}
