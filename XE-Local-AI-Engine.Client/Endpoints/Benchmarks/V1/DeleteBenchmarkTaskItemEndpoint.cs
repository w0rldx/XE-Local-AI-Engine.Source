namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class DeleteBenchmarkTaskItemEndpoint : Endpoint<DeleteBenchmarkTaskItemRequest>
{
    private readonly IBenchmarkTaskItemService _items;

    public DeleteBenchmarkTaskItemEndpoint(IBenchmarkTaskItemService items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Benchmarks.ProjectTaskItemById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DeleteBenchmarkTaskItemRequest req, CancellationToken ct)
    {
        await _items.DeleteAsync(req.ProjectId, req.ItemId, req.ExpectedVersion, ct);
        await Send.NoContentAsync(ct);
    }
}
