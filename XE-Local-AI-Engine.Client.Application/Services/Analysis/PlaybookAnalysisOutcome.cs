namespace XE_Local_AI_Engine.Client.Services.Analysis;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>The result of an analysis run. The counts let the operator see what was proposed vs kept vs filtered.</summary>
public sealed class PlaybookAnalysisOutcome
{
    public required bool MeetsThreshold { get; init; }

    public required IReadOnlyList<PlaybookActionRecord> CreatedSuggestions { get; init; }

    public required int ProposedCount { get; init; }

    public required int RejectedCount { get; init; }

    public required int DuplicateCount { get; init; }
}
