namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

/// <summary>
///     Two to six cells side by side, with the paired-difference interval between every pair of them.
/// </summary>
/// <remarks>
///     The difference is a read-time projection over the cell table computed by <see cref="BenchmarkComparisonService" />
///     — nothing here is stored, so it is always computed from the scores the project holds right now.
/// </remarks>
public sealed class CompareBenchmarkCellsEndpoint : Endpoint<CompareBenchmarkCellsRequest, CompareBenchmarkCellsResponse>
{
    private readonly BenchmarkComparisonService _comparisons;

    public CompareBenchmarkCellsEndpoint(BenchmarkComparisonService comparisons)
    {
        ArgumentNullException.ThrowIfNull(comparisons);
        _comparisons = comparisons;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectCompare);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(CompareBenchmarkCellsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // A refused selection throws BenchmarkValidationException, which the global handler maps through
        // BenchmarkEndpointSupport — the same 400 body this endpoint has always sent.
        if (await _comparisons.CompareCellsAsync(req.ProjectId, req.CellKeys ?? [], ct) is not { } comparison)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found.")));
            return;
        }

        var listed = comparison.Cells.ToResponse();
        await Send.OkAsync(new CompareBenchmarkCellsResponse
        {
            Cells = listed.Cells,
            RankCohort = listed.RankCohort,
            ScorableItemCount = listed.ScorableItemCount,
            PairedDeltas =
            [
                .. comparison.PairedDeltas.Select(static pair => new BenchmarkPairedDeltaResponse
                {
                    ACellKey = pair.ACellKey,
                    BCellKey = pair.BCellKey,
                    SharedItemCount = pair.Estimate.SharedItemCount,
                    Delta = pair.Estimate.Delta,
                    CiLow = pair.Estimate.CiLow,
                    CiHigh = pair.Estimate.CiHigh,
                    Separated = pair.Estimate.Separated
                })
            ]
        }, ct);
    }
}
