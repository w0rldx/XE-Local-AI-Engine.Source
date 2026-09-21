namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class CreateBenchmarkTaskItemEndpoint : Endpoint<CreateBenchmarkTaskItemRequest, BenchmarkTaskItemResponse>
{
    private readonly IBenchmarkTaskItemService _items;

    public CreateBenchmarkTaskItemEndpoint(IBenchmarkTaskItemService items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Benchmarks.ProjectTaskItems);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateBenchmarkTaskItemRequest req, CancellationToken ct)
    {
        var item = await _items.CreateAsync(req.ProjectId, req.ExpectedProjectVersion, req.ToDraft(), ct);
        await Send.OkAsync(item.ToResponse(), ct);
    }
}
