namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Diagnostics.CodeAnalysis;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Everything a trigger create carries. <see cref="Name" /> is the external contract a caller addresses, so it is
///     present here and absent from <see cref="IntegrationTriggerUpdateInput" />: renaming a live trigger is a
///     delete-and-create decision, not an edit.
/// </summary>
public sealed record IntegrationTriggerCreateInput(
    string Name,
    string DisplayName,
    string? Description,
    bool Enabled,
    IntegrationTargetKind TargetKind,
    Guid TargetAgentDefinitionId,
    IntegrationSessionPolicy SessionPolicy,
    IntegrationInputKinds AcceptedInputKinds);

/// <summary>An optimistic update. <see cref="ExpectedVersion" /> is the caller's copy of the row's concurrency token.</summary>
public sealed record IntegrationTriggerUpdateInput(
    long ExpectedVersion,
    string DisplayName,
    string? Description,
    bool Enabled,
    Guid TargetAgentDefinitionId,
    IntegrationSessionPolicy SessionPolicy,
    IntegrationInputKinds AcceptedInputKinds);

/// <summary>
///     What a trigger write decided. Every non-<see cref="Saved" /> value maps to exactly one HTTP status at the
///     endpoint, which is why the service returns an outcome rather than throwing: none of these is exceptional.
/// </summary>
public enum IntegrationTriggerOutcome
{
    /// <summary>The row was written; the result carries it.</summary>
    Saved,

    /// <summary>Another trigger already owns the normalised name. 409.</summary>
    NameConflict,

    /// <summary>The target agent definition does not exist. 400.</summary>
    AgentMissing,

    /// <summary>The target agent is an orchestrator, and ruling D2 scopes V1 to a saved single agent. 400.</summary>
    TargetKindRejected,

    /// <summary>No row with that id. 404.</summary>
    NotFound,

    /// <summary>The row moved on since the caller read it. 409.</summary>
    VersionConflict
}

/// <summary><see cref="Trigger" /> is non-null exactly when <see cref="Outcome" /> is <see cref="IntegrationTriggerOutcome.Saved" />.</summary>
public sealed record IntegrationTriggerResult(IntegrationTriggerOutcome Outcome, IntegrationTriggerSnapshot? Trigger, string? Message);

/// <summary>
///     Trigger CRUD with the two checks a validator cannot make because they need the store: the target agent must
///     exist, and ruling D2 scopes V1 to a single agent, so an orchestrator is refused.
/// </summary>
public interface IIntegrationTriggerService
{
    Task<IReadOnlyList<IntegrationTriggerSnapshot>> ListAsync(CancellationToken cancellationToken = default);

    Task<IntegrationTriggerSnapshot?> GetAsync(Guid triggerId, CancellationToken cancellationToken = default);

    Task<IntegrationTriggerResult> CreateAsync(IntegrationTriggerCreateInput input, CancellationToken cancellationToken = default);

    Task<IntegrationTriggerResult> UpdateAsync(Guid triggerId, IntegrationTriggerUpdateInput input, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid triggerId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Normalises an external trigger name the one way the whole feature agrees on: trimmed and lowercased. Both
    ///     the admin writes and the invoke lookup call it, so a name saved from the UI and a name typed into a curl
    ///     command resolve to the same row.
    /// </summary>
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "The trigger name is lowercase BY CONTRACT (^[a-z0-9][a-z0-9-]{1,63}$) and is the external route segment a caller types, not a security identifier that must round-trip.")]
    static string NormalizeName(string? name) =>
        name?.Trim().ToLowerInvariant() ?? string.Empty;
}
