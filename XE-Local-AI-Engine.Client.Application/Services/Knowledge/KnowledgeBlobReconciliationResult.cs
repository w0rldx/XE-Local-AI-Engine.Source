namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>What one reconciliation pass over the interrupted-write siblings of the document blobs did.</summary>
/// <remarks>
///     A restore means a crash was recovered from, so the names travel back for the sweeper to log them one by one;
///     the removals are ordinary litter and only their count is worth a line.
/// </remarks>
public sealed class KnowledgeBlobReconciliationResult
{
    /// <summary>The file names restored from a backup because the live blob they belong to was missing.</summary>
    public required IReadOnlyList<string> RestoredBlobNames { get; init; }

    /// <summary>How many superseded temp or backup siblings were reclaimed.</summary>
    public required int RemovedLitterCount { get; init; }
}
