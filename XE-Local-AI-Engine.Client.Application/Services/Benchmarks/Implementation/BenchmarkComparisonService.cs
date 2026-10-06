namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The Benchmarks comparison read model: paired cell deltas and the pairwise verdict listing. Read-time only —
///     nothing here is stored, so every answer is computed from what the project holds right now.
/// </summary>
public sealed class BenchmarkComparisonService
{
    private readonly ILogger<BenchmarkComparisonService> _logger;
    private readonly IBenchmarkStore _store;

    public BenchmarkComparisonService(IBenchmarkStore store, ILogger<BenchmarkComparisonService> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    /// <summary>
    ///     Two to six cells side by side with the paired-difference interval between every pair of them, or null when
    ///     the project is gone.
    /// </summary>
    /// <exception cref="BenchmarkValidationException">The selection is out of bounds, repeats a key, or names a cell the project does not hold.</exception>
    public async Task<BenchmarkCellComparison?> CompareCellsAsync(Guid projectId, IReadOnlyList<string> cellKeys, CancellationToken cancellationToken = default)
    {
        BenchmarkPairedBootstrap.ValidateCellSelection(cellKeys);
        if (await _store.GetProjectAsync(projectId, cancellationToken) is null)
        {
            return null;
        }

        var page = await _store.ListCellsAsync(projectId, cancellationToken);

        // Which leaves count toward a quality number: a NIAH case is judged, but its score is a recall figure on its own axis that never enters the cell mean, so it
        // must not enter a paired delta either. Only the item rows can answer that, at one extra decrypting read - acceptable by hand; if it stops being, put the scorable id set on the ranking.
        var items = await _store.ListTaskItemsAsync(projectId, cancellationToken);
        var scorable = items.Where(static item => item.IsLeaf && item.CountsTowardScore).Select(static item => item.Id).ToHashSet();
        var byKey = page.Cells.ToDictionary(static cell => cell.CellKey, StringComparer.Ordinal);
        var selected = new List<BenchmarkCellRecord>(cellKeys.Count);
        foreach (var key in cellKeys)
        {
            // Named, not counted: an operator comparing a cell a re-freeze replaced needs to know WHICH key is
            // gone, and a cell key is the project's own opaque identifier, not user content.
            selected.Add(byKey.TryGetValue(key, out var cell)
                ? cell
                : throw new BenchmarkValidationException($"Cell '{key}' is not part of this project."));
        }

        return new BenchmarkCellComparison
        {
            Cells = new BenchmarkCellPage
            {
                Cells = selected,
                RankCohort = page.RankCohort,
                ScorableItemCount = page.ScorableItemCount
            },
            PairedDeltas = PairedDeltas(selected, scorable)
        };
    }

    /// <summary>The project's verdict matrix and active fit, or null when the project is gone.</summary>
    /// <remarks>
    ///     One read, deliberately: splitting them would let a client render a strength beside a verdict set that did
    ///     not produce it, and nothing on the wire would say so.
    /// </remarks>
    public async Task<BenchmarkComparisonsView?> GetComparisonsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        if (await _store.GetProjectAsync(projectId, cancellationToken) is null)
        {
            return null;
        }

        var cohort = await _store.GetPairwiseCohortAsync(projectId, cancellationToken);
        var fit = await _store.GetActivePairwiseFitAsync(projectId, cancellationToken);
        return new BenchmarkComparisonsView
        {
            Cohort = cohort,
            Fit = fit,
            FitScores = BenchmarkPairwiseFitScores.Read(fit, _logger),

            // The same comparison the ranking makes: one integer against the revision's current value, plus the
            // promoted execution key. No verdict is read to answer it.
            FitIsCurrent = fit is not null
                           && fit.ComparisonSetVersion == cohort.ComparisonSetVersion
                           && string.Equals(fit.JudgeExecutionKey, cohort.ReferenceExecutionKey ?? string.Empty, StringComparison.Ordinal)
        };
    }

    /// <summary>
    ///     Every unordered pair, in the order the caller named the cells. A pair sharing fewer than
    ///     <see cref="BenchmarkPairedBootstrap.MinimumSharedItems" /> rankable items produces no entry at all: the
    ///     absence is the answer, and a zero would be indistinguishable from a measured tie.
    /// </summary>
    private static List<BenchmarkCellPairDelta> PairedDeltas(IReadOnlyList<BenchmarkCellRecord> cells, IReadOnlySet<Guid> scorable)
    {
        var deltas = new List<BenchmarkCellPairDelta>();
        for (var left = 0; left < cells.Count; left++)
        {
            for (var right = left + 1; right < cells.Count; right++)
            {
                var (a, b) = SharedQuality(cells[left], cells[right], scorable);
                if (BenchmarkPairedBootstrap.Estimate(a, b) is not { } estimate)
                {
                    continue;
                }

                deltas.Add(new BenchmarkCellPairDelta
                {
                    ACellKey = cells[left].CellKey,
                    BCellKey = cells[right].CellKey,
                    Estimate = estimate
                });
            }
        }

        return deltas;
    }

    /// <summary>
    ///     The two cells' quality scores for the items they SHARE, aligned and in task-item order.
    /// </summary>
    /// <remarks>
    ///     An item is shared only when both sides answered it rankably AND its item counts toward the score: a run the
    ///     ranking excluded — truncated, item-revised, item-set-revised — carries a null quality and takes its item
    ///     out of the comparison rather than into it with a guessed number, and a display-only leaf (a NIAH case) is
    ///     left out for the same reason it is left out of the cell mean. A run naming no item is a pre-suite singleton
    ///     and can be shared with nothing.
    /// </remarks>
    private static SharedScores SharedQuality(BenchmarkCellRecord left, BenchmarkCellRecord right, IReadOnlySet<Guid> scorable)
    {
        var rightByItem = Rankable(right, scorable);
        var a = new List<int>();
        var b = new List<int>();

        // Deterministic order: the bootstrap draws by index, so the same two cells must present their shared items
        // the same way every time or the seeded interval is not reproducible.
        foreach (var (itemId, score) in Rankable(left, scorable)
                                        .Where(entry => rightByItem.ContainsKey(entry.Key))
                                        .OrderBy(static entry => entry.Value.Index)
                                        .ThenBy(static entry => entry.Key))
        {
            a.Add(score.Quality);
            b.Add(rightByItem[itemId].Quality);
        }

        return new SharedScores([.. a], [.. b]);
    }

    private readonly record struct SharedScores(int[] A, int[] B);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct RankedScore(int Index, int Quality);

    private static Dictionary<Guid, RankedScore> Rankable(BenchmarkCellRecord cell, IReadOnlySet<Guid> scorable)
    {
        var scores = new Dictionary<Guid, RankedScore>();

        // Both nulls are already excluded by the filter, so the GetValueOrDefault calls cannot reach their defaults.
        foreach (var item in cell.Items.Where(item => item is { TaskItemId: not null, QualityScore: not null } && scorable.Contains(item.TaskItemId.GetValueOrDefault())))
        {
            scores.TryAdd(item.TaskItemId.GetValueOrDefault(), new RankedScore(item.TaskItemIndex ?? int.MaxValue, item.QualityScore.GetValueOrDefault()));
        }

        return scores;
    }
}
