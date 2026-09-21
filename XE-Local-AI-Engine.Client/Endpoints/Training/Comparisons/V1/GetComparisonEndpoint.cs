namespace XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;

public sealed class GetComparisonEndpoint : Endpoint<ComparisonByIdRequest, ComparisonResponse>
{
    private readonly IComparisonReportService _comparisons;

    public GetComparisonEndpoint(IComparisonReportService comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        _comparisons = comparisons;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.ComparisonById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ComparisonByIdRequest req, CancellationToken ct)
    {
        var report = await _comparisons.GetAsync(req.ComparisonId, ct);
        if (report is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(report.ToResponse(), ct);
    }
}
