namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class ListEligibleBenchmarkAgentsEndpoint(IBenchmarkCatalogService catalog)
    : Endpoint<EligibleBenchmarkAgentsRequest, ListEligibleBenchmarkAgentsResponse>
{
    private readonly IBenchmarkCatalogService _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.EligibleAgents);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(EligibleBenchmarkAgentsRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ModelName))
        {
            AddError("A model name is required.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var agents = await _catalog.ListEligibleAgentsAsync(req.ModelName, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListEligibleBenchmarkAgentsResponse
                  {
                      Items = [.. agents.Select(static agent => agent.ToResponse())]
                  }, ct)
                  .ConfigureAwait(false);
    }
}

public sealed class ListEligibleBenchmarkModelsEndpoint(IBenchmarkCatalogService catalog)
    : Endpoint<EligibleBenchmarkModelsRequest, ListEligibleBenchmarkModelsResponse>
{
    private readonly IBenchmarkCatalogService _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.EligibleModels);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(EligibleBenchmarkModelsRequest req, CancellationToken ct)
    {
        var models = await _catalog.ListEligibleModelsAsync(req.ContextTokens, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListEligibleBenchmarkModelsResponse
                  {
                      Items = [.. models.Select(static model => model.ToResponse())]
                  }, ct)
                  .ConfigureAwait(false);
    }
}
