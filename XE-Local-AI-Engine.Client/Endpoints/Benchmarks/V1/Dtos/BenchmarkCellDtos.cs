namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

public sealed class ListBenchmarkCellsRequest
{
    public Guid ProjectId { get; init; }
}

/// <summary>One item's answer inside a cell.</summary>
public sealed class BenchmarkCellItemResponse
{
    public Guid RunId { get; init; }
    public Guid? TaskItemId { get; init; }
    public int? TaskItemIndex { get; init; }

    /// <summary>This run's own quality score, not the cell's mean.</summary>
    public int? QualityScore { get; init; }

    public string? PrimaryStopReason { get; init; }
    public string? RankExclusionReason { get; init; }
}

/// <summary>
///     One measurement cell: one model, one KV type, one repeat of the whole task-item suite. This is the unit that
///     ranks — the per-run listing shows the same numbers one row at a time and cannot say which items a cell is
///     missing.
/// </summary>
public sealed class BenchmarkCellResponse
{
    public required string CellKey { get; init; }
    public required string PrimaryModelName { get; init; }
    public required string ModelContentFingerprint { get; init; }
    public string? KvCacheType { get; init; }
    public Guid? RepeatGroupId { get; init; }
    public int? RepeatIndex { get; init; }

    /// <summary>The mean of the cell's scorable items' quality scores, or null when it does not rank.</summary>
    public int? Quality { get; init; }

    public int? Rank { get; init; }

    /// <summary>
    ///     Why the cell does not rank, or null when it does — <c>item-set-revised</c>, <c>item-incomplete</c>, or a
    ///     run-level reason on a pre-suite singleton cell.
    /// </summary>
    public string? RankExclusionReason { get; init; }

    public IReadOnlyList<BenchmarkCellItemResponse> Items { get; init; } = [];
}

public sealed class ListBenchmarkCellsResponse
{
    public IReadOnlyList<BenchmarkCellResponse> Cells { get; init; } = [];

    /// <summary>What the ranking is computed against, counted in CELLS.</summary>
    public required BenchmarkRankCohortResponse RankCohort { get; init; }

    /// <summary>
    ///     How many leaf items the project counts toward its score right now. A cell holding fewer of them is why a
    ///     reader sees <c>item-incomplete</c>, and it is not derivable from the cells alone.
    /// </summary>
    public int ScorableItemCount { get; init; }
}

/// <summary>
///     Two to six cells to compare. The cells are named explicitly rather than derived from a model filter: which
///     quant of which model an operator wants a difference between is a question only they can answer.
/// </summary>
public sealed class CompareBenchmarkCellsRequest
{
    public Guid ProjectId { get; init; }

    /// <summary>Repeated query parameter: <c>?cellKeys=…&amp;cellKeys=…</c>. Two to six, distinct.</summary>
    public IReadOnlyList<string> CellKeys { get; init; } = [];
}

/// <summary>
///     The paired difference between two cells over the items they BOTH answered rankably, with a 95 % percentile
///     bootstrap interval. Present only when at least three items are shared — an absent entry for a requested pair
///     means "too few shared items", and is never a delta of zero.
/// </summary>
public sealed class BenchmarkPairedDeltaResponse
{
    public required string ACellKey { get; init; }
    public required string BCellKey { get; init; }

    /// <summary>How many items were rankable in both cells; the resampling unit.</summary>
    public int SharedItemCount { get; init; }

    /// <summary>Mean of <c>qualityA − qualityB</c> over the shared items. Positive means A scored higher.</summary>
    public double Delta { get; init; }

    public double CiLow { get; init; }
    public double CiHigh { get; init; }

    /// <summary>
    ///     False exactly when 0 lies inside the interval — the flag a reader renders "not separated by this suite"
    ///     from, so no client re-derives it from the two bounds.
    /// </summary>
    public bool Separated { get; init; }
}

/// <summary>The requested cells, in the order they were asked for, plus every pairwise delta between them.</summary>
public sealed class CompareBenchmarkCellsResponse
{
    public IReadOnlyList<BenchmarkCellResponse> Cells { get; init; } = [];

    /// <summary>What the ranking is computed against, counted in CELLS — the whole project's, not the selection's.</summary>
    public required BenchmarkRankCohortResponse RankCohort { get; init; }

    /// <summary>How many leaf items the project counts toward its score right now.</summary>
    public int ScorableItemCount { get; init; }

    /// <summary>One entry per unordered pair of requested cells that shares enough items to support an interval.</summary>
    public IReadOnlyList<BenchmarkPairedDeltaResponse> PairedDeltas { get; init; } = [];
}
