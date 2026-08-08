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
    ///     Upserts a single tour entry by key (replacing any existing entry for that key) and stamps it now.
    ///     Returns false when the current user cannot be resolved.
    /// </summary>
    Task<bool> SaveEntryAsync(ClaimsPrincipal principal, string key, TutorialStatus status, CancellationToken cancellationToken);
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
public sealed record TutorialStateEntry(string Key, TutorialStatus Status, DateTime AtUtc);
