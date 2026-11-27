namespace XE_Local_AI_Engine.Client.Services.Eval;

/// <summary>
///     Per-run summary of a golden harvest: how many thumbs-up sources were scanned, and
///     how the candidates split across created, already-harvested and skipped. Counts only, never turn text.
/// </summary>
public sealed class GoldenHarvestOutcome
{
    public required int ThumbsUpScanned { get; init; }

    public required int CreatedCount { get; init; }

    public required int DuplicateCount { get; init; }

    public required int SkippedCount { get; init; }
}
