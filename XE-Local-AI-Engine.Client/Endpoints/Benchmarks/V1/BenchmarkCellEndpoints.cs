namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     The project's measurement cells. A cell is what ranks, so a comparison reads this shape rather than the run
///     listing: the runs of one cell are its answers, and the items MISSING from it are why it does not rank.
/// </summary>
public sealed class ListBenchmarkCellsEndpoint(IBenchmarkStore store)
    : Endpoint<ListBenchmarkCellsRequest, ListBenchmarkCellsResponse>
{
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectCells);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ListBenchmarkCellsRequest req, CancellationToken ct)
    {
        if (await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var cells = await _store.ListCellsAsync(req.ProjectId, ct).ConfigureAwait(false);
        await Send.OkAsync(cells.ToResponse(), ct).ConfigureAwait(false);
    }
}
