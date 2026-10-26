namespace XE_Local_AI_Engine.Client.Services.Tutorial;

using System.Security.Claims;

/// <summary>
///     Reads and upserts the authenticated node user's onboarding tour progress. Single-admin node: progress is
///     scoped to the resolved <c>NodeUser</c>, stored as a JSON array on the identity row.
/// </summary>
public interface INodeTutorialStateService
{
    /// <summary>
    ///     Returns every recorded tour entry for the current user (empty when none recorded).
    /// </summary>
    Task<IReadOnlyList<TutorialStateEntry>> GetEntriesAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);

    /// <summary>
    ///     Upserts a single tour entry by key and stamps it now. Completed is terminal and cannot be downgraded to skipped.
    ///     Returns false when the current user cannot be resolved.
    /// </summary>
    Task<bool> SaveEntryAsync(ClaimsPrincipal principal, string key, TutorialStatus status, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns when the current user acknowledged the AI responsibility notice, or null when they have not (or cannot
    ///     be resolved).
    /// </summary>
    Task<DateTime?> GetAiAcknowledgedAtAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);

    /// <summary>
    ///     Records the AI responsibility acknowledgement now, keeping the first timestamp on a repeat. Returns the stored
    ///     timestamp, or null when the current user cannot be resolved or the write failed.
    /// </summary>
    Task<DateTime?> AcknowledgeAiAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}

/// <summary>
///     Terminal state of a single tour for a user.
/// </summary>
public enum TutorialStatus
{
    Completed,
    Skipped
}

/// <summary>
///     One recorded tour outcome: which tour, how it ended, and when.
/// </summary>
public sealed class TutorialStateEntry
{
    public required string Key { get; init; }

    public required TutorialStatus Status { get; init; }

    public required DateTime AtUtc { get; init; }
}
