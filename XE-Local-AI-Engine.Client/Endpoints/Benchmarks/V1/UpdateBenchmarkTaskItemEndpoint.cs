namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class UpdateBenchmarkTaskItemEndpoint : Endpoint<UpdateBenchmarkTaskItemRequest, BenchmarkTaskItemResponse>
{
    private readonly IBenchmarkTaskItemService _items;

    public UpdateBenchmarkTaskItemEndpoint(IBenchmarkTaskItemService items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Benchmarks.ProjectTaskItemById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(UpdateBenchmarkTaskItemRequest req, CancellationToken ct)
    {
        var item = await _items.UpdateAsync(req.ProjectId, req.ItemId, req.ExpectedVersion, req.ToDraft(), ct);
        await Send.OkAsync(item.ToResponse(), ct);
    }
}
