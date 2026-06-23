namespace XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;

using System.Globalization;
using Quartz;
using Quartz.Plugin.Interrupt;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Default <see cref="IScheduledJobManagementService" />. Validates the requested schedule, persists the definition
///     through the scheduled-job stores first, then reconciles the live Quartz job/trigger to match the stored state
///     (delete-and-recreate is the simplest correct path for an update). All logging is sanitized — definition ids,
///     template ids, schedule kinds, and enabled state are safe to log; raw parameters and run details are never logged.
/// </summary>
public sealed class ScheduledJobManagementService(
    IScheduledJobDefinitionStore definitionStore,
    IScheduledJobRunStore runStore,
    IScheduledJobTemplateRegistry templateRegistry,
    ISchedulerFactory schedulerFactory,
    ISchedulerEventPublisher eventPublisher,
    ILogger<ScheduledJobManagementService> logger,
    TimeProvider timeProvider) : IScheduledJobManagementService
{
    private readonly IScheduledJobDefinitionStore _definitionStore =
        definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));

    private readonly ISchedulerEventPublisher _eventPublisher =
        eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));

    private readonly ILogger<ScheduledJobManagementService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly IScheduledJobRunStore _runStore =
        runStore ?? throw new ArgumentNullException(nameof(runStore));

    private readonly ISchedulerFactory _schedulerFactory =
        schedulerFactory ?? throw new ArgumentNullException(nameof(schedulerFactory));

    private readonly IScheduledJobTemplateRegistry _templateRegistry =
        templateRegistry ?? throw new ArgumentNullException(nameof(templateRegistry));

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public IReadOnlyList<ScheduledJobTemplateDescriptor> ListTemplatesAsync()
    {
        return _templateRegistry.ListTemplates();
    }

    public Task<IReadOnlyList<ScheduledJobDefinitionRecord>> ListJobsAsync(bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        return _definitionStore.ListAsync(includeDeleted, cancellationToken);
    }

    public Task<ScheduledJobDefinitionRecord?> GetJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _definitionStore.GetByIdAsync(id, cancellationToken);
    }

    public async Task<ScheduledJobDefinitionRecord> CreateJobAsync(ScheduledJobManagementInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var descriptor = Validate(input);

        // Operator-created jobs are persisted enabled and scheduled immediately; disabling is the dedicated action.
        var storeInput = ToStoreInput(input, enabled: true, ScheduledJobCreator.User);
        var record = await _definitionStore.AddAsync(storeInput, cancellationToken).ConfigureAwait(false);

        await ReconcileScheduleAsync(record, descriptor, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Created scheduled job {ScheduledJobId} from template {TemplateId} ({ScheduleKind}, enabled={Enabled}).",
            record.Id,
            record.TemplateId,
            record.ScheduleKind,
            record.Enabled);

        await SafePublishDefinitionAsync(record.Id, "created").ConfigureAwait(false);

        return record;
    }

    public async Task<ScheduledJobDefinitionRecord?> UpdateJobAsync(Guid id,
        ScheduledJobManagementInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var descriptor = Validate(input);

        var existing = await _definitionStore.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        // A PUT edit never flips the enabled state — that is the dedicated SetEnabledAsync action — so carry the current
        // enabled flag and original creator through to the store regardless of what the request body claims.
        var storeInput = ToStoreInput(input, existing.Enabled, existing.CreatedBy);
        var updated = await _definitionStore.UpdateAsync(id, storeInput, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return null;
        }

        // Delete-and-recreate is the simplest correct path: the new definition fully determines the Quartz job/trigger.
        await ReconcileScheduleAsync(updated, descriptor, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Updated scheduled job {ScheduledJobId} (template {TemplateId}, {ScheduleKind}, enabled={Enabled}).",
            updated.Id,
            updated.TemplateId,
            updated.ScheduleKind,
            updated.Enabled);

        await SafePublishDefinitionAsync(updated.Id, "updated").ConfigureAwait(false);

        return updated;
    }

    public async Task<ScheduledJobDefinitionRecord?> SetEnabledAsync(Guid id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var updated = await _definitionStore.SetEnabledAsync(id, enabled, cancellationToken).ConfigureAwait(false);
        if (updated is null)
        {
            return null;
        }

        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var jobKey = BuildJobKey(updated.Id);

        if (enabled)
        {
            var descriptor = _templateRegistry.GetTemplate(updated.TemplateId);
            // A registered template is required to build the dispatch job; a definition referencing an unknown template
            // cannot be scheduled. Leave it unscheduled (store flag already flipped) and surface the inconsistency.
            if (descriptor is null)
            {
                throw new ScheduledJobValidationException($"Template '{updated.TemplateId}' is not registered, so this job cannot be enabled.");
            }

            await ScheduleAsync(scheduler, updated, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            _ = await scheduler.DeleteJob(jobKey, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("{Action} scheduled job {ScheduledJobId} (template {TemplateId}).",
            enabled ? "Enabled" : "Disabled",
            updated.Id,
            updated.TemplateId);

        await SafePublishDefinitionAsync(updated.Id, enabled ? "enabled" : "disabled").ConfigureAwait(false);

        return updated;
    }

    public async Task<bool> DeleteJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Store-first soft-delete preserves run history; DeleteJob on the scheduler is idempotent, so a missing Quartz
        // job is not an error. The whole operation is idempotent: a second delete of the same id simply returns false.
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        _ = await scheduler.DeleteJob(BuildJobKey(id), cancellationToken).ConfigureAwait(false);

        var deleted = await _definitionStore.SoftDeleteAsync(id, cancellationToken).ConfigureAwait(false);

        if (deleted)
        {
            _logger.LogInformation("Soft-deleted scheduled job {ScheduledJobId} and unscheduled its Quartz job.", id);
            await SafePublishDefinitionAsync(id, "deleted").ConfigureAwait(false);
        }

        return deleted;
    }

    public async Task TriggerNowAsync(Guid id,
        IReadOnlyDictionary<string, string>? parameterOverrides = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await _definitionStore.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (definition is null || definition.DeletedAtUtc is not null)
        {
            throw new ScheduledJobValidationException("Scheduled job not found.");
        }

        if (!definition.Enabled)
        {
            throw new ScheduledJobValidationException("A disabled scheduled job cannot be triggered manually.");
        }

        var descriptor = _templateRegistry.GetTemplate(definition.TemplateId);
        if (descriptor is null)
        {
            throw new ScheduledJobValidationException($"Template '{definition.TemplateId}' is not registered, so this job cannot be triggered.");
        }

        if (!descriptor.AllowManualTrigger)
        {
            throw new ScheduledJobValidationException("This template does not allow manual triggering.");
        }

        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var jobKey = BuildJobKey(definition.Id);
        if (!await scheduler.CheckExists(jobKey, cancellationToken).ConfigureAwait(false))
        {
            throw new ScheduledJobValidationException("This job is not currently scheduled and cannot be triggered.");
        }

        // Self-heal a persisted JobDetail whose stored class name no longer resolves: re-add the durable detail with
        // replace=true so its JOB_CLASS_NAME refreshes to the current typeof(...) value. A JobDetail stored before the
        // dispatch job moved namespaces would otherwise fail TriggerJob with "Could not load type ...". BuildJobDetail
        // produces an identical detail for an already-current job, so this is a no-op in the common case. AddJob with a
        // durable detail and no trigger never fires the job — TriggerJob below performs the actual fire.
        // Best-effort: a transient AddJob failure (e.g. a momentary DB hiccup) must not surface as a raw 500 from the
        // heal that exists to remove that very symptom. Log and continue to TriggerJob, which then either succeeds or
        // surfaces the real, actionable error.
        try
        {
            await scheduler.AddJob(BuildJobDetail(definition), replace: true, cancellationToken).ConfigureAwait(false);
        }
        catch (SchedulerException ex)
        {
            _logger.LogWarning(ex,
                "Self-heal re-add of durable job detail for scheduled job {ScheduledJobId} failed; attempting to trigger the existing job anyway.",
                definition.Id);
        }

        // Per-fire overrides ride the firing trigger's JobDataMap (never the stored definition). The dispatcher decides
        // which keys may override stored parameters; an empty/absent map fires the stored definition unchanged.
        // Quartz honors [DisallowConcurrentExecution] on the non-overlapping dispatch job, so an overlapping manual fire
        // of a prevent-overlap definition is serialized by Quartz rather than rejected here.
        if (parameterOverrides is { Count: > 0 })
        {
            var fireDataMap = new JobDataMap();
            foreach (var (key, value) in parameterOverrides)
            {
                fireDataMap[key] = value;
            }

            await scheduler.TriggerJob(jobKey, fireDataMap, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await scheduler.TriggerJob(jobKey, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Manually triggered scheduled job {ScheduledJobId} (template {TemplateId}, overrides={OverrideCount}).",
            definition.Id,
            definition.TemplateId,
            parameterOverrides?.Count ?? 0);
    }

    public async Task<int> ReconcileDurableJobsAsync(CancellationToken cancellationToken = default)
    {
        // Startup self-heal: every persisted, enabled, non-deleted definition re-adds its Quartz JobDetail with
        // replace=true so a stale JOB_CLASS_NAME (e.g. written before the dispatch job moved namespaces) refreshes to the
        // current typeof(...) value. This covers recurring jobs that are never manually triggered. It NEVER changes a
        // trigger's schedule and NEVER fires a job: AddJob with a durable, trigger-less detail only rewrites the stored
        // detail, leaving any existing trigger intact. Definitions whose template is no longer registered are skipped
        // (they cannot be rebuilt) rather than faulting the whole sweep.
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var definitions = await _definitionStore.ListAsync(includeDeleted: false, cancellationToken).ConfigureAwait(false);

        var healedCount = 0;
        foreach (var definition in definitions)
        {
            if (!definition.Enabled || definition.DeletedAtUtc is not null)
            {
                continue;
            }

            if (_templateRegistry.GetTemplate(definition.TemplateId) is null)
            {
                _logger.LogWarning("Skipped JobDetail reconciliation for scheduled job {ScheduledJobId}: template {TemplateId} is not registered.",
                    definition.Id,
                    definition.TemplateId);
                continue;
            }

            var jobKey = BuildJobKey(definition.Id);
            if (!await scheduler.CheckExists(jobKey, cancellationToken).ConfigureAwait(false))
            {
                // No persisted JobDetail yet (e.g. the scheduler is enabling for the first time after the definition was
                // stored). Leave creation to CreateJobAsync/SetEnabledAsync — reconciliation only refreshes existing rows.
                continue;
            }

            await scheduler.AddJob(BuildJobDetail(definition), replace: true, cancellationToken).ConfigureAwait(false);
            healedCount++;
        }

        _logger.LogInformation("Reconciled {HealedCount} durable scheduler job detail(s) at startup.", healedCount);

        return healedCount;
    }

    public Task<IReadOnlyList<ScheduledJobRunRecord>> ListRunsAsync(ScheduledRunStatus? status = null,
        long? fromUtc = null,
        long? toUtc = null,
        Guid? scheduledJobId = null,
        CancellationToken cancellationToken = default)
    {
        return _runStore.ListAsync(status, fromUtc, toUtc, scheduledJobId, cancellationToken);
    }

    public Task<ScheduledJobRunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        return _runStore.GetByIdAsync(runId, cancellationToken);
    }

    public async Task<RunCancellationOutcome> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _runStore.GetByIdAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return RunCancellationOutcome.NotFound;
        }

        if (IsTerminal(run.Status))
        {
            return RunCancellationOutcome.AlreadyTerminal;
        }

        // Record intent first so the dispatcher can distinguish an operator cancel (→ Cancelled) from an auto-interrupt
        // timeout (→ TimedOut) when its handler's token trips.
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        _ = await _runStore.RequestCancellationAsync(runId, now, cancellationToken).ConfigureAwait(false);

        // No fire-instance id means the run never reached Quartz execution (or pre-dates it); it can only be reconciled.
        if (string.IsNullOrEmpty(run.QuartzFireInstanceId))
        {
            _logger.LogInformation("Cancellation requested for run {RunId}, which has no active Quartz fire instance.", runId);
            return RunCancellationOutcome.RequestedButNotRunning;
        }

        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);
        var wasInterrupted = await scheduler.Interrupt(run.QuartzFireInstanceId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Cancellation requested for run {RunId} (job {ScheduledJobId}); Quartz interrupt active={WasInterrupted}.",
            runId,
            run.ScheduledJobId,
            wasInterrupted);

        return wasInterrupted ? RunCancellationOutcome.Requested : RunCancellationOutcome.RequestedButNotRunning;
    }

    private static bool IsTerminal(ScheduledRunStatus status)
    {
        return status is ScheduledRunStatus.Succeeded
            or ScheduledRunStatus.Failed
            or ScheduledRunStatus.Cancelled
            or ScheduledRunStatus.TimedOut
            or ScheduledRunStatus.Skipped;
    }

    // ── realtime ─────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task SafePublishDefinitionAsync(Guid scheduledJobId, string action)
    {
        // Notifications are best-effort and never the source of truth — a publish failure must not fault a successful
        // definition mutation (React refetches authoritative state through TanStack Query after the event).
        try
        {
            var occurredAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            await _eventPublisher.PublishDefinitionAsync(new SchedulerDefinitionHubEvent(SchedulerHubEvents.JobDefinitionChanged, scheduledJobId, action, occurredAt),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Failed to publish scheduler definition-changed event ({Action}) for {ScheduledJobId}.",
                action,
                scheduledJobId);
        }
    }

    // ── validation ───────────────────────────────────────────────────────────────────────────────────────────────

    private ScheduledJobTemplateDescriptor Validate(ScheduledJobManagementInput input)
    {
        if (string.IsNullOrWhiteSpace(input.DisplayName))
        {
            throw new ScheduledJobValidationException("Display name is required.");
        }

        if (!Enum.IsDefined(input.ScheduleKind))
        {
            throw new ScheduledJobValidationException($"Schedule kind '{input.ScheduleKind}' is not valid.");
        }

        if (!Enum.IsDefined(input.MisfirePolicy))
        {
            throw new ScheduledJobValidationException($"Misfire policy '{input.MisfirePolicy}' is not valid.");
        }

        var descriptor = _templateRegistry.GetTemplate(input.TemplateId)
                         ?? throw new ScheduledJobValidationException($"Template '{input.TemplateId}' is not registered.");

        if (!descriptor.SupportedScheduleKinds.Contains(input.ScheduleKind))
        {
            throw new ScheduledJobValidationException($"Template '{input.TemplateId}' does not support the '{input.ScheduleKind}' schedule kind.");
        }

        ValidateScheduleFields(input);
        ValidateTimeZone(input.TimeZoneId);

        if (input.MaxRuntimeSeconds is <= 0)
        {
            throw new ScheduledJobValidationException("Maximum runtime, when set, must be greater than zero seconds.");
        }

        return descriptor;
    }

    private static void ValidateScheduleFields(ScheduledJobManagementInput input)
    {
        switch (input.ScheduleKind)
        {
            case ScheduleKind.Cron:
                if (string.IsNullOrWhiteSpace(input.CronExpression))
                {
                    throw new ScheduledJobValidationException("A cron expression is required for a cron schedule.");
                }

                if (!CronExpression.IsValidExpression(input.CronExpression))
                {
                    throw new ScheduledJobValidationException("The cron expression is not valid.");
                }

                break;

            case ScheduleKind.SimpleInterval:
                if (input.IntervalSeconds is null or <= 0)
                {
                    throw new ScheduledJobValidationException("A positive interval in seconds is required for a simple-interval schedule.");
                }

                if (input.RepeatCount is < 0)
                {
                    throw new ScheduledJobValidationException("Repeat count, when set, must be zero or greater (omit it to repeat forever).");
                }

                break;

            case ScheduleKind.OneShot:
                if (input.StartAtUtc is null)
                {
                    throw new ScheduledJobValidationException("A start time is required for a one-shot schedule.");
                }

                break;

            case ScheduleKind.Manual:
                // A Manual job is an on-demand durable job with no trigger — it requires none of the cron/interval/
                // repeat/start-at fields, so there is nothing to validate here.
                break;

            default:
                throw new ScheduledJobValidationException($"Schedule kind '{input.ScheduleKind}' is not supported.");
        }
    }

    private static void ValidateTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            throw new ScheduledJobValidationException("A time zone id is required.");
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ScheduledJobValidationException($"Time zone '{timeZoneId}' could not be resolved.", exception);
        }
    }

    // ── Quartz reconciliation ────────────────────────────────────────────────────────────────────────────────────

    private async Task ReconcileScheduleAsync(ScheduledJobDefinitionRecord record,
        ScheduledJobTemplateDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        _ = descriptor;
        var scheduler = await _schedulerFactory.GetScheduler(cancellationToken).ConfigureAwait(false);

        // Idempotent unschedule: clears any prior job/trigger so an update fully replaces the previous schedule.
        _ = await scheduler.DeleteJob(BuildJobKey(record.Id), cancellationToken).ConfigureAwait(false);

        if (record.Enabled)
        {
            await ScheduleAsync(scheduler, record, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ScheduleAsync(IScheduler scheduler,
        ScheduledJobDefinitionRecord record,
        CancellationToken cancellationToken)
    {
        // Ensure no stale job/trigger remains before (re)scheduling.
        _ = await scheduler.DeleteJob(BuildJobKey(record.Id), cancellationToken).ConfigureAwait(false);

        var jobDetail = BuildJobDetail(record);

        if (record.ScheduleKind == ScheduleKind.Manual)
        {
            // A Manual job is a durable on-demand job with NO trigger — it never auto-fires, only TriggerNowAsync fires
            // it. AddJob requires the detail to be durable (BuildJobDetail already calls StoreDurably), so it registers
            // a trigger-less job. Do not build a trigger for Manual.
            await scheduler.AddJob(jobDetail, replace: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        var trigger = BuildTrigger(record, _timeProvider); // pass TimeProvider for SimpleInterval StartAt default

        _ = await scheduler.ScheduleJob(jobDetail, trigger, cancellationToken).ConfigureAwait(false);
    }

    private static JobKey BuildJobKey(Guid definitionId)
    {
        return new JobKey(definitionId.ToString("N"), SchedulerJobKeys.Group);
    }

    private static TriggerKey BuildTriggerKey(Guid definitionId)
    {
        return new TriggerKey(definitionId.ToString("N"), SchedulerJobKeys.Group);
    }

    private static IJobDetail BuildJobDetail(ScheduledJobDefinitionRecord record)
    {
        var jobType = record.PreventOverlap
            ? typeof(NonOverlappingSchedulerDispatchJob)
            : typeof(SchedulerDispatchJob);

        var builder = JobBuilder.Create(jobType)
                                .WithIdentity(BuildJobKey(record.Id))
                                .UsingJobData(SchedulerJobKeys.ScheduledJobIdKey, record.Id.ToString())
                                // Opt this job into the auto-interrupt monitor (UseJobAutoInterrupt). Stored as the
                                // string "true" because UseProperties=true persists only strings; the plugin reads it
                                // via Convert.ToBoolean. Without this key the global DefaultMaxRunTime never applies.
                                .UsingJobData(JobInterruptMonitorPlugin.JobDataMapKeyAutoInterruptable, "true")
                                .StoreDurably();

        // Per-job max-runtime override: the plugin parses MaxRunTime as a millisecond long from its string form
        // (TryGetLongValueFromString → TimeSpan.FromMilliseconds). Falls back to the global default when unset.
        if (record.MaxRuntimeSeconds is > 0)
        {
            builder = builder.UsingJobData(JobInterruptMonitorPlugin.JobDataMapKeyMaxRunTime,
                (record.MaxRuntimeSeconds.Value * 1000L).ToString(CultureInfo.InvariantCulture));
        }

        return builder.Build();
    }

    private static ITrigger BuildTrigger(ScheduledJobDefinitionRecord record, TimeProvider timeProvider)
    {
        var jobKey = BuildJobKey(record.Id);
        var builder = TriggerBuilder.Create()
                                    .WithIdentity(BuildTriggerKey(record.Id))
                                    .ForJob(jobKey);

        builder = record.ScheduleKind switch
        {
            ScheduleKind.Cron => ApplyCronSchedule(builder, record),
            ScheduleKind.SimpleInterval => ApplySimpleSchedule(builder, record, timeProvider),
            ScheduleKind.OneShot => ApplyOneShotSchedule(builder, record),
            _ => throw new ScheduledJobValidationException($"Schedule kind '{record.ScheduleKind}' is not supported.")
        };

        return builder.Build();
    }

    private static TriggerBuilder ApplyCronSchedule(TriggerBuilder builder, ScheduledJobDefinitionRecord record)
    {
        var cron = record.CronExpression
                   ?? throw new ScheduledJobValidationException("A cron expression is required for a cron schedule.");
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(record.TimeZoneId);

        builder = builder.WithCronSchedule(cron, x =>
        {
            _ = x.InTimeZone(timeZone);
            ApplyCronMisfire(x, record.MisfirePolicy);
        });

        if (record.StartAtUtc is { } startAt)
        {
            builder = builder.StartAt(DateTimeOffset.FromUnixTimeMilliseconds(startAt));
        }

        if (record.EndAtUtc is { } endAt)
        {
            builder = builder.EndAt(DateTimeOffset.FromUnixTimeMilliseconds(endAt));
        }

        return builder;
    }

    private static TriggerBuilder ApplySimpleSchedule(TriggerBuilder builder, ScheduledJobDefinitionRecord record, TimeProvider timeProvider)
    {
        var intervalSeconds = record.IntervalSeconds
                              ?? throw new ScheduledJobValidationException("An interval is required for a simple-interval schedule.");

        builder = builder.WithSimpleSchedule(x =>
        {
            _ = x.WithInterval(TimeSpan.FromSeconds(intervalSeconds));
            if (record.RepeatCount is { } repeatCount)
            {
                _ = x.WithRepeatCount(repeatCount);
            }
            else
            {
                _ = x.RepeatForever();
            }

            ApplySimpleMisfire(x, record.MisfirePolicy);
        });

        var startAt = record.StartAtUtc is { } start
            ? DateTimeOffset.FromUnixTimeMilliseconds(start)
            : timeProvider.GetUtcNow();
        builder = builder.StartAt(startAt);

        if (record.EndAtUtc is { } endAt)
        {
            builder = builder.EndAt(DateTimeOffset.FromUnixTimeMilliseconds(endAt));
        }

        return builder;
    }

    private static TriggerBuilder ApplyOneShotSchedule(TriggerBuilder builder, ScheduledJobDefinitionRecord record)
    {
        var startAt = record.StartAtUtc
                      ?? throw new ScheduledJobValidationException("A start time is required for a one-shot schedule.");

        // No repeat schedule — the simple schedule fires once (0 repeats) at StartAt; only the misfire policy is applied.
        return builder
               .WithSimpleSchedule(x => ApplySimpleMisfire(x, record.MisfirePolicy))
               .StartAt(DateTimeOffset.FromUnixTimeMilliseconds(startAt));
    }

    private static void ApplyCronMisfire(CronScheduleBuilder builder, SchedulerMisfirePolicy policy)
    {
        switch (policy)
        {
            case SchedulerMisfirePolicy.SkipMissed:
                _ = builder.WithMisfireHandlingInstructionDoNothing();
                break;

            case SchedulerMisfirePolicy.FireOnceNow:
                _ = builder.WithMisfireHandlingInstructionFireAndProceed();
                break;

            case SchedulerMisfirePolicy.Smart:
            default:
                // SmartPolicy: leave the builder untouched (Quartz default).
                break;
        }
    }

    private static void ApplySimpleMisfire(SimpleScheduleBuilder builder, SchedulerMisfirePolicy policy)
    {
        switch (policy)
        {
            case SchedulerMisfirePolicy.SkipMissed:
                _ = builder.WithMisfireHandlingInstructionNextWithRemainingCount();
                break;

            case SchedulerMisfirePolicy.FireOnceNow:
                _ = builder.WithMisfireHandlingInstructionFireNow();
                break;

            case SchedulerMisfirePolicy.Smart:
            default:
                // SmartPolicy: leave the builder untouched (Quartz default).
                break;
        }
    }

    private static ScheduledJobDefinitionInput ToStoreInput(ScheduledJobManagementInput input,
        bool enabled,
        ScheduledJobCreator createdBy)
    {
        return new ScheduledJobDefinitionInput(input.TemplateId,
            input.DisplayName,
            input.Description,
            enabled,
            input.ScheduleKind,
            input.CronExpression,
            input.IntervalSeconds,
            input.RepeatCount,
            input.StartAtUtc,
            input.EndAtUtc,
            input.TimeZoneId,
            input.MisfirePolicy,
            input.PreventOverlap,
            input.MaxRuntimeSeconds,
            input.Parameters,
            createdBy);
    }
}
