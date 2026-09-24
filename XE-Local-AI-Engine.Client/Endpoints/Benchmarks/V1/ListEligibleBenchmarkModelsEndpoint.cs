namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class ListEligibleBenchmarkModelsEndpoint : Endpoint<EligibleBenchmarkModelsRequest, ListEligibleBenchmarkModelsResponse>
{
    private readonly IBenchmarkCatalogService _catalog;

    public ListEligibleBenchmarkModelsEndpoint(IBenchmarkCatalogService catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.EligibleModels);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(EligibleBenchmarkModelsRequest req, CancellationToken ct)
    {
        var models = await _catalog.ListEligibleModelsAsync(req.ContextTokens, ct);
        await Send.OkAsync(new ListEligibleBenchmarkModelsResponse
        {
            Items = [.. models.Select(static model => model.ToResponse())]
        }, ct);
    }
}
