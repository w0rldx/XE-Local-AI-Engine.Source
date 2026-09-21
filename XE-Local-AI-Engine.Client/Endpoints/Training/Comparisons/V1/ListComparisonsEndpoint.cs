namespace XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Comparisons.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Comparison;

public sealed class ListComparisonsEndpoint : EndpointWithoutRequest<ListComparisonsResponse>
{
    private readonly IComparisonReportService _comparisons;

    public ListComparisonsEndpoint(IComparisonReportService comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        _comparisons = comparisons;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Training.Comparisons);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var items = await _comparisons.ListAsync(ct);
        await Send.OkAsync(new ListComparisonsResponse
        {
            Items = items.Select(item => item.ToResponse()).ToArray()
        }, ct);
    }
}
