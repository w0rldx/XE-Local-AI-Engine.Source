namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;

using PersistenceEntities = XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Extension methods that translate between endpoint DTOs and the management service's input/record types.
///     This is the sole point in the Client project that references the scheduler record member names — only this
///     file needs adjustment if those names change.
/// </summary>
internal static class SchedulerMapper
{
    // -----------------------------------------------------------------------
    // Template → response
    // -----------------------------------------------------------------------

    public static ScheduledJobTemplateResponse ToResponse(this ScheduledJobTemplateDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        return new ScheduledJobTemplateResponse
        {
            TemplateId = descriptor.TemplateId,
            DisplayName = descriptor.DisplayName,
            Description = descriptor.Description,
            ParameterSchema = descriptor.ParameterSchema,
            DefaultParameters = descriptor.DefaultParameters,
            SupportedScheduleKinds = [.. descriptor.SupportedScheduleKinds.Select(static k => k.ToWire())],
            DefaultScheduleKind = descriptor.DefaultScheduleKind.ToWire(),
            DefaultMisfirePolicy = descriptor.DefaultMisfirePolicy.ToWire(),
            DefaultMaxRuntimeSeconds = descriptor.DefaultMaxRuntimeSeconds,
            AllowManualTrigger = descriptor.AllowManualTrigger,
            AllowAgentCreation = descriptor.AllowAgentCreation,
            HistoryDetailLevel = descriptor.HistoryDetailLevel.ToString()
        };
    }

    // -----------------------------------------------------------------------
    // Job definition record → response
    // -----------------------------------------------------------------------

    public static ScheduledJobResponse ToResponse(this ScheduledJobDefinitionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new ScheduledJobResponse
        {
            Id = record.Id,
            TemplateId = record.TemplateId,
            DisplayName = record.DisplayName,
            Description = record.Description,
            Enabled = record.Enabled,
            ScheduleKind = record.ScheduleKind.ToWire(),
            CronExpression = record.CronExpression,
            IntervalSeconds = record.IntervalSeconds,
            RepeatCount = record.RepeatCount,
            StartAtUtc = record.StartAtUtc,
            EndAtUtc = record.EndAtUtc,
            TimeZoneId = record.TimeZoneId,
            MisfirePolicy = record.MisfirePolicy.ToWire(),
            PreventOverlap = record.PreventOverlap,
            MaxRuntimeSeconds = record.MaxRuntimeSeconds,
            // Raw ParameterJson is deliberately omitted; only presence is surfaced.
            HasParameters = !string.IsNullOrEmpty(record.ParameterJson),
            CreatedBy = record.CreatedBy.ToWire(),
            CreatedAtUtc = record.CreatedAtUtc,
            UpdatedAtUtc = record.UpdatedAtUtc,
            DisabledAtUtc = record.DisabledAtUtc,
            DeletedAtUtc = record.DeletedAtUtc
        };
    }

    // -----------------------------------------------------------------------
    // Create/Update request → management input
    // -----------------------------------------------------------------------

    public static ScheduledJobManagementInput ToInput(this CreateScheduledJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ScheduledJobManagementInput(request.TemplateId,
            request.DisplayName,
            request.Description,
            request.ScheduleKind.ToPersistence(),
            request.CronExpression,
            request.IntervalSeconds,
            request.RepeatCount,
            request.StartAtUtc,
            request.EndAtUtc,
            request.TimeZoneId,
            request.MisfirePolicy.ToPersistence(),
            request.PreventOverlap,
            request.MaxRuntimeSeconds,
            request.Parameters);
    }

    public static ScheduledJobManagementInput ToInput(this UpdateScheduledJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ScheduledJobManagementInput(request.TemplateId,
            request.DisplayName,
            request.Description,
            request.ScheduleKind.ToPersistence(),
            request.CronExpression,
            request.IntervalSeconds,
            request.RepeatCount,
            request.StartAtUtc,
            request.EndAtUtc,
            request.TimeZoneId,
            request.MisfirePolicy.ToPersistence(),
            request.PreventOverlap,
            request.MaxRuntimeSeconds,
            request.Parameters);
    }

    // -----------------------------------------------------------------------
    // Run record → response
    // -----------------------------------------------------------------------

    public static ScheduledJobRunResponse ToResponse(this ScheduledJobRunRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new ScheduledJobRunResponse
        {
            Id = record.Id,
            ScheduledJobId = record.ScheduledJobId,
            TemplateId = record.TemplateId,
            TriggeredBy = record.TriggeredBy.ToWire(),
            Status = record.Status.ToWire(),
            ScheduledFireTimeUtc = record.ScheduledFireTimeUtc,
            ActualFireTimeUtc = record.ActualFireTimeUtc,
            CompletedAtUtc = record.CompletedAtUtc,
            DurationMs = record.DurationMs,
            Summary = record.Summary,
            // DetailsJson and ErrorDetails are deliberately omitted (redaction).
            ErrorMessage = record.ErrorMessage,
            CancellationRequestedAtUtc = record.CancellationRequestedAtUtc,
            CreatedAtUtc = record.CreatedAtUtc
        };
    }

    // -----------------------------------------------------------------------
    // Enum mapping: persistence <-> wire
    //
    // The wire enums (Endpoints.Scheduler.V1) mirror the persistence enums
    // member-for-member; this is the single point that translates between them,
    // isolating the wire contract from a persistence-side rename. Member names
    // are kept byte-identical so the JSON form (serialized by name) is unchanged.
    // -----------------------------------------------------------------------

    public static ScheduleKind ToWire(this PersistenceEntities.ScheduleKind value) =>
        value switch
        {
            PersistenceEntities.ScheduleKind.Cron => ScheduleKind.Cron,
            PersistenceEntities.ScheduleKind.OneShot => ScheduleKind.OneShot,
            PersistenceEntities.ScheduleKind.SimpleInterval => ScheduleKind.SimpleInterval,
            PersistenceEntities.ScheduleKind.Manual => ScheduleKind.Manual,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    public static PersistenceEntities.ScheduleKind ToPersistence(this ScheduleKind value) =>
        value switch
        {
            ScheduleKind.Cron => PersistenceEntities.ScheduleKind.Cron,
            ScheduleKind.OneShot => PersistenceEntities.ScheduleKind.OneShot,
            ScheduleKind.SimpleInterval => PersistenceEntities.ScheduleKind.SimpleInterval,
            ScheduleKind.Manual => PersistenceEntities.ScheduleKind.Manual,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    public static SchedulerMisfirePolicy ToWire(this PersistenceEntities.SchedulerMisfirePolicy value) =>
        value switch
        {
            PersistenceEntities.SchedulerMisfirePolicy.Smart => SchedulerMisfirePolicy.Smart,
            PersistenceEntities.SchedulerMisfirePolicy.SkipMissed => SchedulerMisfirePolicy.SkipMissed,
            PersistenceEntities.SchedulerMisfirePolicy.FireOnceNow => SchedulerMisfirePolicy.FireOnceNow,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    public static PersistenceEntities.SchedulerMisfirePolicy ToPersistence(this SchedulerMisfirePolicy value) =>
        value switch
        {
            SchedulerMisfirePolicy.Smart => PersistenceEntities.SchedulerMisfirePolicy.Smart,
            SchedulerMisfirePolicy.SkipMissed => PersistenceEntities.SchedulerMisfirePolicy.SkipMissed,
            SchedulerMisfirePolicy.FireOnceNow => PersistenceEntities.SchedulerMisfirePolicy.FireOnceNow,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    public static ScheduledJobCreator ToWire(this PersistenceEntities.ScheduledJobCreator value) =>
        value switch
        {
            PersistenceEntities.ScheduledJobCreator.User => ScheduledJobCreator.User,
            PersistenceEntities.ScheduledJobCreator.Agent => ScheduledJobCreator.Agent,
            PersistenceEntities.ScheduledJobCreator.System => ScheduledJobCreator.System,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    public static ScheduledRunStatus ToWire(this PersistenceEntities.ScheduledRunStatus value) =>
        value switch
        {
            PersistenceEntities.ScheduledRunStatus.Queued => ScheduledRunStatus.Queued,
            PersistenceEntities.ScheduledRunStatus.Running => ScheduledRunStatus.Running,
            PersistenceEntities.ScheduledRunStatus.Succeeded => ScheduledRunStatus.Succeeded,
            PersistenceEntities.ScheduledRunStatus.Failed => ScheduledRunStatus.Failed,
            PersistenceEntities.ScheduledRunStatus.Cancelled => ScheduledRunStatus.Cancelled,
            PersistenceEntities.ScheduledRunStatus.TimedOut => ScheduledRunStatus.TimedOut,
            PersistenceEntities.ScheduledRunStatus.Skipped => ScheduledRunStatus.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    public static PersistenceEntities.ScheduledRunStatus ToPersistence(this ScheduledRunStatus value) =>
        value switch
        {
            ScheduledRunStatus.Queued => PersistenceEntities.ScheduledRunStatus.Queued,
            ScheduledRunStatus.Running => PersistenceEntities.ScheduledRunStatus.Running,
            ScheduledRunStatus.Succeeded => PersistenceEntities.ScheduledRunStatus.Succeeded,
            ScheduledRunStatus.Failed => PersistenceEntities.ScheduledRunStatus.Failed,
            ScheduledRunStatus.Cancelled => PersistenceEntities.ScheduledRunStatus.Cancelled,
            ScheduledRunStatus.TimedOut => PersistenceEntities.ScheduledRunStatus.TimedOut,
            ScheduledRunStatus.Skipped => PersistenceEntities.ScheduledRunStatus.Skipped,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };

    /// <summary>Nullable convenience used by the run-list filter (a null filter stays null).</summary>
    public static PersistenceEntities.ScheduledRunStatus? ToPersistence(this ScheduledRunStatus? value) =>
        value is null ? null : value.Value.ToPersistence();

    public static ScheduledRunTrigger ToWire(this PersistenceEntities.ScheduledRunTrigger value) =>
        value switch
        {
            PersistenceEntities.ScheduledRunTrigger.Schedule => ScheduledRunTrigger.Schedule,
            PersistenceEntities.ScheduledRunTrigger.Manual => ScheduledRunTrigger.Manual,
            PersistenceEntities.ScheduledRunTrigger.Agent => ScheduledRunTrigger.Agent,
            PersistenceEntities.ScheduledRunTrigger.System => ScheduledRunTrigger.System,
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
        };
}
