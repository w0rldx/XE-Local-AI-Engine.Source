namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     One measurement cell as a reader sees it. <see cref="Quality" /> is the mean of its scorable items'
///     qualities, and it is <see langword="null" /> exactly when <see cref="RankExclusionReason" /> says why.
/// </summary>
public sealed class BenchmarkCellRecord
{
    public required string CellKey { get; init; }

    public required string PrimaryModelName { get; init; }

    public required string ModelContentFingerprint { get; init; }

    public required string? KvCacheType { get; init; }

    public required Guid? RepeatGroupId { get; init; }

    public required int? RepeatIndex { get; init; }

    public required int? Quality { get; init; }

    public required int? Rank { get; init; }

    public required string? RankExclusionReason { get; init; }

    /// <summary>Every run of the cell, in task-item order; a pre-suite cell holds exactly one, naming no item.</summary>
    public required IReadOnlyList<BenchmarkCellItemRecord> Items { get; init; }
}

/// <summary>One item's answer inside a cell.</summary>
public sealed class BenchmarkCellItemRecord
{
    public required Guid RunId { get; init; }

    public required Guid? TaskItemId { get; init; }

    public required int? TaskItemIndex { get; init; }

    public required int? QualityScore { get; init; }

    public required string? PrimaryStopReason { get; init; }

    public required string? RankExclusionReason { get; init; }
}

public sealed class BenchmarkCellPage
{
    public required IReadOnlyList<BenchmarkCellRecord> Cells { get; init; }

    public required BenchmarkRankCohort RankCohort { get; init; }

    /// <summary>
    ///     How many leaf items the project counts toward its score right now. A cell holding fewer of them is why a reader
    ///     sees <c>item-incomplete</c>, and it is not derivable from the cells alone.
    /// </summary>
    public required int ScorableItemCount { get; init; }
}
