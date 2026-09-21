namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class ListEligibleBenchmarkAgentsEndpoint : Endpoint<EligibleBenchmarkAgentsRequest, ListEligibleBenchmarkAgentsResponse>
{
    private readonly IBenchmarkCatalogService _catalog;

    public ListEligibleBenchmarkAgentsEndpoint(IBenchmarkCatalogService catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.EligibleAgents);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(EligibleBenchmarkAgentsRequest req, CancellationToken ct)
    {
        var agents = await _catalog.ListEligibleAgentsAsync(req.ModelName, ct);
        await Send.OkAsync(new ListEligibleBenchmarkAgentsResponse
                  {
                      Items = [.. agents.Select(static agent => agent.ToResponse())]
                  }, ct);
    }
}
