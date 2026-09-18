namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     A project's task items: the questions it asks. Their own sub-resource rather than fields on the project PUT,
///     because each write recomputes the project's item-set hash — and a moved set hash resets the rank cohort, which
///     is not something a field could express.
/// </summary>
public sealed class ListBenchmarkTaskItemsEndpoint : Endpoint<BenchmarkProjectRouteRequest, ListBenchmarkTaskItemsResponse>
{
    private readonly IBenchmarkTaskItemService _items;
    private readonly BenchmarkRecordService _records;

    public ListBenchmarkTaskItemsEndpoint(IBenchmarkTaskItemService items, BenchmarkRecordService records)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(records);
        _items = items;
        _records = records;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectTaskItems);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(BenchmarkProjectRouteRequest req, CancellationToken ct)
    {
        // Get-or-create, not a plain list: a project created before task items existed has none, and materializing
        // item 0 needs the node encryption key that a migration does not have. Every project created since gets its
        // items with itself, so this is a read for all of them.
        var taskItems = await _items.GetOrCreateItemsAsync(req.ProjectId, ct);
        var project = await _records.GetProjectAsync(req.ProjectId, ct)
                      ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");
        await Send.OkAsync(taskItems.ToResponse(project), ct);
    }
}

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
