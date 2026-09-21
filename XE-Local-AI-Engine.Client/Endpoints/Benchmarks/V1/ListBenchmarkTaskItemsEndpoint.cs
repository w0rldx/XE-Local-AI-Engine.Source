namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>A project's task items: the questions it asks.</summary>
/// <remarks>
///     Their own sub-resource rather than fields on the project PUT, because each write recomputes the project's
///     item-set hash — and a moved set hash resets the rank cohort, which is not something a field could express.
/// </remarks>
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
        // Get-or-create, not a plain list: a project that predates task items has none, and materializing item 0 needs the node encryption key a migration does not
        // have. Every project created since gets its items with itself, so this is a read for all of them.
        var taskItems = await _items.GetOrCreateItemsAsync(req.ProjectId, ct);
        var project = await _records.GetProjectAsync(req.ProjectId, ct)
                      ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");
        await Send.OkAsync(taskItems.ToResponse(project), ct);
    }
}
