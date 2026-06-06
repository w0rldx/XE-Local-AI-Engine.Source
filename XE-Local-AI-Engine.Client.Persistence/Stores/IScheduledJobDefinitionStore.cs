namespace XE_Local_AI_Engine.Client.Persistence;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Node-scoped persistence for scheduled job definitions. <c>ParameterJson</c> is encrypted at rest by the node
///     encryption interceptors; reads return it decrypted on the <see cref="ScheduledJobDefinitionRecord" />. This store
///     performs no schedule validation — that is the application-layer service's responsibility; it owns only
///     id/timestamp stamping and the soft-delete/enable lifecycle.
/// </summary>
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
public sealed record ScheduledJobDefinitionRecord(
    Guid Id,
    string TemplateId,
    string DisplayName,
    string? Description,
    bool Enabled,
    ScheduleKind ScheduleKind,
    string? CronExpression,
    long? IntervalSeconds,
    int? RepeatCount,
    long? StartAtUtc,
    long? EndAtUtc,
    string TimeZoneId,
    SchedulerMisfirePolicy MisfirePolicy,
    bool PreventOverlap,
    int? MaxRuntimeSeconds,
    string? ParameterJson,
    ScheduledJobCreator CreatedBy,
    long CreatedAtUtc,
    long UpdatedAtUtc,
    long? DisabledAtUtc,
    long? DeletedAtUtc);

/// <summary>
///     Mutable fields of a scheduled job definition supplied on create/update. <see cref="ParameterJson" /> is passed as
///     a plaintext string; the store encodes it to UTF-8 bytes before the interceptors encrypt it.
/// </summary>
public sealed record ScheduledJobDefinitionInput(
    string TemplateId,
    string DisplayName,
    string? Description,
    bool Enabled,
    ScheduleKind ScheduleKind,
    string? CronExpression,
    long? IntervalSeconds,
    int? RepeatCount,
    long? StartAtUtc,
    long? EndAtUtc,
    string TimeZoneId,
    SchedulerMisfirePolicy MisfirePolicy,
    bool PreventOverlap,
    int? MaxRuntimeSeconds,
    string? ParameterJson,
    ScheduledJobCreator CreatedBy);
