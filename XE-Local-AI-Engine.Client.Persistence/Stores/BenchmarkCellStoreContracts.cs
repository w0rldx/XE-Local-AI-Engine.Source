namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     One measurement cell as a reader sees it. <paramref name="Quality" /> is the mean of its scorable items'
///     qualities, and it is <see langword="null" /> exactly when <paramref name="RankExclusionReason" /> says why.
/// </summary>
/// <param name="Items">Every run of the cell, in task-item order; a pre-suite cell holds exactly one, naming no item.</param>
public sealed record BenchmarkCellRecord(
    string CellKey,
    string PrimaryModelName,
    string ModelContentFingerprint,
    string? KvCacheType,
    Guid? RepeatGroupId,
    int? RepeatIndex,
    int? Quality,
    int? Rank,
    string? RankExclusionReason,
    IReadOnlyList<BenchmarkCellItemRecord> Items);

/// <summary>One item's answer inside a cell.</summary>
public sealed record BenchmarkCellItemRecord(
    Guid RunId,
    Guid? TaskItemId,
    int? TaskItemIndex,
    int? QualityScore,
    string? PrimaryStopReason,
    string? RankExclusionReason);

/// <param name="ScorableItemCount">
///     How many leaf items the project counts toward its score right now. A cell holding fewer of them is why a reader
///     sees <c>item-incomplete</c>, and it is not derivable from the cells alone.
/// </param>
public sealed record BenchmarkCellPage(
    IReadOnlyList<BenchmarkCellRecord> Cells,
    BenchmarkRankCohort RankCohort,
    int ScorableItemCount);
