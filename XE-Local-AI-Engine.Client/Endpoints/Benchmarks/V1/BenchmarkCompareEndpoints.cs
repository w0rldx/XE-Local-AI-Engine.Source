namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     Two to six cells side by side, with the paired-difference interval between every pair of them (B3). The
///     difference is a read-time projection over the cell table — nothing here is stored, so it is always computed
///     from the scores the project holds right now.
/// </summary>
public sealed class CompareBenchmarkCellsEndpoint(IBenchmarkStore store)
    : Endpoint<CompareBenchmarkCellsRequest, CompareBenchmarkCellsResponse>
{
    private const int MinimumCells = 2;
    private const int MaximumCells = 6;

    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.Benchmarks.ProjectCompare);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(CompareBenchmarkCellsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var requested = req.CellKeys ?? [];
        if (requested.Count is < MinimumCells or > MaximumCells)
        {
            await RefuseAsync($"Provide between {MinimumCells} and {MaximumCells} cellKeys to compare.").ConfigureAwait(false);
            return;
        }

        if (requested.Distinct(StringComparer.Ordinal).Count() != requested.Count)
        {
            // A cell against itself is a delta of exactly zero with a zero-width interval — a true statement that
            // reads as a finding. Refused rather than served.
            await RefuseAsync("The cellKeys to compare must be distinct.").ConfigureAwait(false);
            return;
        }

        if (await _store.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false) is null)
        {
            await Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkNotFoundException("Benchmark project was not found."))).ConfigureAwait(false);
            return;
        }

        var page = await _store.ListCellsAsync(req.ProjectId, ct).ConfigureAwait(false);
        var byKey = page.Cells.ToDictionary(static cell => cell.CellKey, StringComparer.Ordinal);
        var selected = new List<BenchmarkCellRecord>(requested.Count);
        foreach (var key in requested)
        {
            if (!byKey.TryGetValue(key, out var cell))
            {
                // Named, not counted: an operator comparing a cell a re-freeze replaced needs to know WHICH key is
                // gone, and a cell key is the project's own opaque identifier, not user content.
                await RefuseAsync($"Cell '{key}' is not part of this project.").ConfigureAwait(false);
                return;
            }

            selected.Add(cell);
        }

        var listed = new BenchmarkCellPage(selected, page.RankCohort, page.ScorableItemCount).ToResponse();
        await Send.OkAsync(new CompareBenchmarkCellsResponse
        {
            Cells = listed.Cells,
            RankCohort = listed.RankCohort,
            ScorableItemCount = listed.ScorableItemCount,
            PairedDeltas = PairedDeltas(selected)
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Every unordered pair, in the order the caller named the cells. A pair sharing fewer than
    ///     <see cref="BenchmarkPairedBootstrap.MinimumSharedItems" /> rankable items produces no entry at all: the
    ///     absence is the answer, and a zero would be indistinguishable from a measured tie.
    /// </summary>
    private static List<BenchmarkPairedDeltaResponse> PairedDeltas(IReadOnlyList<BenchmarkCellRecord> cells)
    {
        var deltas = new List<BenchmarkPairedDeltaResponse>();
        for (var left = 0; left < cells.Count; left++)
        {
            for (var right = left + 1; right < cells.Count; right++)
            {
                var (a, b) = SharedQuality(cells[left], cells[right]);
                if (BenchmarkPairedBootstrap.Estimate(a, b) is not { } estimate)
                {
                    continue;
                }

                deltas.Add(new BenchmarkPairedDeltaResponse
                {
                    ACellKey = cells[left].CellKey,
                    BCellKey = cells[right].CellKey,
                    SharedItemCount = estimate.SharedItemCount,
                    Delta = estimate.Delta,
                    CiLow = estimate.CiLow,
                    CiHigh = estimate.CiHigh,
                    Separated = estimate.Separated
                });
            }
        }

        return deltas;
    }

    /// <summary>
    ///     The two cells' quality scores for the items they SHARE, aligned and in task-item order. An item is shared
    ///     only when both sides answered it rankably: a run the ranking excluded — truncated, item-revised,
    ///     item-set-revised — carries a null quality and takes its item out of the comparison rather than into it
    ///     with a guessed number. A run naming no item is a pre-suite singleton and can be shared with nothing.
    /// </summary>
    private static (int[] A, int[] B) SharedQuality(BenchmarkCellRecord left, BenchmarkCellRecord right)
    {
        var rightByItem = Rankable(right);
        var a = new List<int>();
        var b = new List<int>();

        // Deterministic order: the bootstrap draws by index, so the same two cells must present their shared items
        // the same way every time or the seeded interval is not reproducible.
        foreach (var (itemId, score) in Rankable(left)
                                        .Where(entry => rightByItem.ContainsKey(entry.Key))
                                        .OrderBy(static entry => entry.Value.Index)
                                        .ThenBy(static entry => entry.Key))
        {
            a.Add(score.Quality);
            b.Add(rightByItem[itemId].Quality);
        }

        return ([.. a], [.. b]);
    }

    private static Dictionary<Guid, (int Index, int Quality)> Rankable(BenchmarkCellRecord cell)
    {
        var scores = new Dictionary<Guid, (int Index, int Quality)>();

        // Both nulls are already excluded by the filter, so the GetValueOrDefault calls cannot reach their defaults.
        foreach (var item in cell.Items.Where(static item => item is { TaskItemId: not null, QualityScore: not null }))
        {
            scores.TryAdd(item.TaskItemId.GetValueOrDefault(), (item.TaskItemIndex ?? int.MaxValue, item.QualityScore.GetValueOrDefault()));
        }

        return scores;
    }

    private Task RefuseAsync(string message) =>
        Send.ResultAsync(BenchmarkEndpointSupport.Error(new BenchmarkValidationException(message)));
}
