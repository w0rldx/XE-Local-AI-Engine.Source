namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence;
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
            SupportedScheduleKinds = descriptor.SupportedScheduleKinds,
            DefaultScheduleKind = descriptor.DefaultScheduleKind,
            DefaultMisfirePolicy = descriptor.DefaultMisfirePolicy,
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
            ScheduleKind = record.ScheduleKind,
            CronExpression = record.CronExpression,
            IntervalSeconds = record.IntervalSeconds,
            RepeatCount = record.RepeatCount,
            StartAtUtc = record.StartAtUtc,
            EndAtUtc = record.EndAtUtc,
            TimeZoneId = record.TimeZoneId,
            MisfirePolicy = record.MisfirePolicy,
            PreventOverlap = record.PreventOverlap,
            MaxRuntimeSeconds = record.MaxRuntimeSeconds,
            // Raw ParameterJson is deliberately omitted; only presence is surfaced.
            HasParameters = !string.IsNullOrEmpty(record.ParameterJson),
            CreatedBy = record.CreatedBy,
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
            request.ScheduleKind,
            request.CronExpression,
            request.IntervalSeconds,
            request.RepeatCount,
            request.StartAtUtc,
            request.EndAtUtc,
            request.TimeZoneId,
            request.MisfirePolicy,
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
            request.ScheduleKind,
            request.CronExpression,
            request.IntervalSeconds,
            request.RepeatCount,
            request.StartAtUtc,
            request.EndAtUtc,
            request.TimeZoneId,
            request.MisfirePolicy,
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
            TriggeredBy = record.TriggeredBy,
            Status = record.Status,
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
}
