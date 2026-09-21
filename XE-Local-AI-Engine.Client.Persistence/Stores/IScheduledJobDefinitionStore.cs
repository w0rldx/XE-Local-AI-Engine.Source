namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Node-scoped persistence for scheduled job definitions.
/// </summary>
/// <remarks>
///     <c>ParameterJson</c> is encrypted at rest by the node encryption interceptors; reads return it decrypted on the
///     <see cref="ScheduledJobDefinitionRecord" />. This store performs no schedule validation — that is the
///     application-layer service's responsibility — and owns only id and timestamp stamping plus the
///     soft-delete/enable lifecycle.
/// </remarks>
public interface IScheduledJobDefinitionStore
{
    /// <summary>
    ///     Persists a new definition (assigning <c>Id</c>, <c>CreatedAtUtc</c> and <c>UpdatedAtUtc</c>) and returns the
    ///     stored record with <c>ParameterJson</c> decrypted.
    /// </summary>
    Task<ScheduledJobDefinitionRecord> AddAsync(ScheduledJobDefinitionInput input, CancellationToken cancellationToken = default);

    /// <summary>Returns the record for <paramref name="id" />, or <c>null</c> when no definition has that id.</summary>
    Task<ScheduledJobDefinitionRecord?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns every definition, ordered by CreatedAtUtc. Soft-deleted definitions are excluded unless
    ///     <paramref name="includeDeleted" /> is <c>true</c>.
    /// </summary>
    Task<IReadOnlyList<ScheduledJobDefinitionRecord>> ListAsync(bool includeDeleted = false, CancellationToken cancellationToken = default);

    /// <summary>Returns every non-deleted definition for <paramref name="templateId" />, ordered by CreatedAtUtc.</summary>
    Task<IReadOnlyList<ScheduledJobDefinitionRecord>> ListByTemplateAsync(string templateId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Scheduler fast-path: enabled, non-deleted definitions filtered server-side and ordered by CreatedAtUtc.
    /// </summary>
    Task<IReadOnlyList<ScheduledJobDefinitionRecord>> ListEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Overwrites the mutable fields of the definition with <paramref name="id" /> and bumps <c>UpdatedAtUtc</c>.
    ///     Returns the updated record, or <c>null</c> when no definition has that id.
    /// </summary>
    Task<ScheduledJobDefinitionRecord?> UpdateAsync(Guid id, ScheduledJobDefinitionInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets <c>Enabled</c> on the definition with <paramref name="id" />, stamping <c>DisabledAtUtc</c> when disabling
    ///     and clearing it when enabling, and bumps <c>UpdatedAtUtc</c>. Returns the updated record, or <c>null</c> when no
    ///     definition has that id.
    /// </summary>
    Task<ScheduledJobDefinitionRecord?> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Soft-deletes the definition with <paramref name="id" />: stamps <c>DeletedAtUtc</c> and sets
    ///     <c>Enabled</c> to <c>false</c>. Returns <c>true</c> when a row was updated.
    /// </summary>
    Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
///     Decrypted, typed projection of a persisted scheduled job definition. <see cref="ParameterJson" /> is returned in
///     plaintext (decrypted on materialization); the store converts to and from this shape at the boundary so callers
///     never touch the encrypted byte column.
/// </summary>
public sealed class ScheduledJobDefinitionRecord
{
    public required Guid Id { get; init; }

    public required string TemplateId { get; init; }

    public required string DisplayName { get; init; }

    public required string? Description { get; init; }

    public required bool Enabled { get; init; }

    public required ScheduleKind ScheduleKind { get; init; }

    public required string? CronExpression { get; init; }

    public required long? IntervalSeconds { get; init; }

    public required int? RepeatCount { get; init; }

    public required long? StartAtUtc { get; init; }

    public required long? EndAtUtc { get; init; }

    public required string TimeZoneId { get; init; }

    public required SchedulerMisfirePolicy MisfirePolicy { get; init; }

    public required bool PreventOverlap { get; init; }

    public required int? MaxRuntimeSeconds { get; init; }

    public required string? ParameterJson { get; init; }

    public required ScheduledJobCreator CreatedBy { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required long? DisabledAtUtc { get; init; }

    public required long? DeletedAtUtc { get; init; }
}

/// <summary>
///     Mutable fields of a scheduled job definition supplied on create/update. <see cref="ParameterJson" /> is passed as
///     a plaintext string; the store encodes it to UTF-8 bytes before the interceptors encrypt it.
/// </summary>
public sealed record ScheduledJobDefinitionInput
{
    public required string TemplateId { get; init; }

    public required string DisplayName { get; init; }

    public required string? Description { get; init; }

    public required bool Enabled { get; init; }

    public required ScheduleKind ScheduleKind { get; init; }

    public required string? CronExpression { get; init; }

    public required long? IntervalSeconds { get; init; }

    public required int? RepeatCount { get; init; }

    public required long? StartAtUtc { get; init; }

    public required long? EndAtUtc { get; init; }

    public required string TimeZoneId { get; init; }

    public required SchedulerMisfirePolicy MisfirePolicy { get; init; }

    public required bool PreventOverlap { get; init; }

    public required int? MaxRuntimeSeconds { get; init; }

    public required string? ParameterJson { get; init; }

    public required ScheduledJobCreator CreatedBy { get; init; }
}
