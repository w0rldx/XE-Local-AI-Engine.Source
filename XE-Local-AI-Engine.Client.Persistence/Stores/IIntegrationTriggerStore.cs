namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A trigger as a reader sees it. Field order mirrors the entity. Every value is plaintext structural — the name is
///     the external contract, and the display fields are sorted and filtered on.
/// </summary>
public sealed record IntegrationTriggerSnapshot
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public required string? Description { get; init; }

    public required bool Enabled { get; init; }

    public required IntegrationTargetKind TargetKind { get; init; }

    public required Guid TargetAgentDefinitionId { get; init; }

    public required IntegrationSessionPolicy SessionPolicy { get; init; }

    public required IntegrationInputKinds AcceptedInputKinds { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long Version { get; init; }
}

/// <summary>Everything a create needs. The store stamps <c>CreatedAtUtc</c>, <c>UpdatedAtUtc</c> and <c>Version</c>.</summary>
public sealed class IntegrationTriggerCreateCommand
{
    public required Guid TriggerId { get; init; }

    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public required string? Description { get; init; }

    public required bool Enabled { get; init; }

    public required IntegrationTargetKind TargetKind { get; init; }

    public required Guid TargetAgentDefinitionId { get; init; }

    public required IntegrationSessionPolicy SessionPolicy { get; init; }

    public required IntegrationInputKinds AcceptedInputKinds { get; init; }
}

/// <summary>
///     An optimistic update. <c>Name</c> is absent on purpose: it is the external contract a caller addresses, so
///     renaming a live trigger is a delete-and-create decision rather than an edit.
/// </summary>
public sealed record IntegrationTriggerUpdateCommand
{
    public required Guid TriggerId { get; init; }

    public required long ExpectedVersion { get; init; }

    public required string DisplayName { get; init; }

    public required string? Description { get; init; }

    public required bool Enabled { get; init; }

    public required Guid TargetAgentDefinitionId { get; init; }

    public required IntegrationSessionPolicy SessionPolicy { get; init; }

    public required IntegrationInputKinds AcceptedInputKinds { get; init; }
}

/// <summary>
///     Persistence boundary for integration triggers. The interface is <c>public</c> and speaks only in the records
///     above; the <c>IntegrationTrigger</c> entity is <c>internal</c> and never crosses this boundary.
/// </summary>
public interface IIntegrationTriggerStore
{
    /// <summary>Inserts a trigger and returns it as stored. A duplicate <c>Name</c> surfaces as <c>DbUpdateException</c>.</summary>
    Task<IntegrationTriggerSnapshot> CreateAsync(IntegrationTriggerCreateCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies an optimistic update. Returns <see langword="false" /> — never an exception — when the row is missing
    ///     or the version no longer matches, so the caller maps it to 409 without a try/catch on every admin PUT.
    /// </summary>
    Task<bool> UpdateAsync(IntegrationTriggerUpdateCommand command, CancellationToken cancellationToken = default);

    Task<IntegrationTriggerSnapshot?> GetByIdAsync(Guid triggerId, CancellationToken cancellationToken = default);

    /// <summary>Resolves the trigger an external caller addressed by name, enabled or not; the caller decides what a disabled one means.</summary>
    Task<IntegrationTriggerSnapshot?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Every trigger, ordered <c>Name</c> ascending.</summary>
    Task<IReadOnlyList<IntegrationTriggerSnapshot>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes a trigger. Returns <see langword="false" /> when no row matched.</summary>
    Task<bool> DeleteAsync(Guid triggerId, CancellationToken cancellationToken = default);
}
