namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     Renumbers the whole item list at once. Not a revision bump and not a cohort reset: the index is a display
///     position that no hash carries, so a drag-and-drop must not unrank a completed suite.
/// </summary>
public sealed class ReorderBenchmarkTaskItemsEndpoint : Endpoint<ReorderBenchmarkTaskItemsRequest, ListBenchmarkTaskItemsResponse>
{
    private readonly IBenchmarkTaskItemService _items;
    private readonly BenchmarkRecordService _records;

    public ReorderBenchmarkTaskItemsEndpoint(IBenchmarkTaskItemService items, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(records);
        _items = items;
        _records = records;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Benchmarks.ProjectTaskItemOrder);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest)
                                      .ProducesProblem(StatusCodes.Status404NotFound)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ReorderBenchmarkTaskItemsRequest req, CancellationToken ct)
    {
        var reordered = await _items.ReorderAsync(req.ProjectId, req.ItemIds, ct);
        var project = await _records.GetProjectAsync(req.ProjectId, ct)
                      ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");
        await Send.OkAsync(reordered.ToResponse(project), ct);
    }
}
