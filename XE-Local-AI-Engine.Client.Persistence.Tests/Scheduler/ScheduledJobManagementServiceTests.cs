namespace XE_Local_AI_Engine.Client.Persistence.Tests.Scheduler;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Quartz.Plugin.Interrupt;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Integration tests for <see cref="ScheduledJobManagementService" />.
///     Uses a fully-migrated temporary SQLite database + the real Quartz ADO.NET store so both store state
///     and Quartz job/trigger state are observable in the same process.
/// </summary>
public sealed class ScheduledJobManagementServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "xe-sched-mgmt-" + Guid.NewGuid().ToString("N"));

    private readonly NullNodeSqliteKeyHolder _keyHolder = new();

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Validation — rejects bad input before touching the store
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task CreateJobAsync_WithUnknownTemplateId_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-unknown-template.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = ValidCronInput("does-not-exist");

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithScheduleKindNotInTemplate_ThrowsValidation()
    {
        // TestEchoScheduledJobHandler only supports OneShot and Cron — SimpleInterval is not allowed.
        var dbPath = GetDatabasePath("val-bad-kind.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = new ScheduledJobManagementInput(
            TemplateId: TestEchoScheduledJobHandler.Id,
            DisplayName: "Bad kind",
            Description: null,
            ScheduleKind: ScheduleKind.SimpleInterval,   // not in SupportedScheduleKinds
            CronExpression: null,
            IntervalSeconds: 60,
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            TimeZoneId: "UTC",
            MisfirePolicy: SchedulerMisfirePolicy.Smart,
            PreventOverlap: false,
            MaxRuntimeSeconds: null,
            Parameters: null);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithEmptyCronExpression_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-empty-cron.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = ValidCronInput(cronExpression: "");

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithInvalidCronExpression_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-bad-cron.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = ValidCronInput(cronExpression: "not-a-valid-cron");

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithNonPositiveIntervalSeconds_ThrowsValidation()
    {
        // Need a handler that supports SimpleInterval — build one ad-hoc.
        var dbPath = GetDatabasePath("val-bad-interval.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath, extraHandler: new SimpleIntervalOnlyHandler());
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = new ScheduledJobManagementInput(
            TemplateId: SimpleIntervalOnlyHandler.Id,
            DisplayName: "Bad interval",
            Description: null,
            ScheduleKind: ScheduleKind.SimpleInterval,
            CronExpression: null,
            IntervalSeconds: 0,   // must be > 0
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            TimeZoneId: "UTC",
            MisfirePolicy: SchedulerMisfirePolicy.Smart,
            PreventOverlap: false,
            MaxRuntimeSeconds: null,
            Parameters: null);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithMissingStartAtForOneShot_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-oneshot-no-start.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = new ScheduledJobManagementInput(
            TemplateId: TestEchoScheduledJobHandler.Id,
            DisplayName: "One-shot no start",
            Description: null,
            ScheduleKind: ScheduleKind.OneShot,
            CronExpression: null,
            IntervalSeconds: null,
            RepeatCount: null,
            StartAtUtc: null,   // required for OneShot
            EndAtUtc: null,
            TimeZoneId: "UTC",
            MisfirePolicy: SchedulerMisfirePolicy.Smart,
            PreventOverlap: false,
            MaxRuntimeSeconds: null,
            Parameters: null);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithUnresolvableTimeZone_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-bad-tz.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = ValidCronInput(timeZoneId: "Not/A/Real/Zone");

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithNonPositiveMaxRuntimeSeconds_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-bad-maxruntime.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = ValidCronInput(maxRuntimeSeconds: 0);   // must be > 0 or null

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithBlankDisplayName_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-blank-name.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = ValidCronInput(displayName: "   ");

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Create — persists definition AND schedules Quartz job+trigger
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task CreateJobAsync_ValidCronInput_PersistsDefinitionAndSchedulesQuartzJob()
    {
        var dbPath = GetDatabasePath("create-cron.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var input = ValidCronInput();
        var record = await service.CreateJobAsync(input).ConfigureAwait(false);

        // Store: definition persisted with Enabled=true and CreatedBy=User.
        AssertEx.Equal(TestEchoScheduledJobHandler.Id, record.TemplateId);
        AssertEx.Equal(true, record.Enabled);
        AssertEx.Equal(ScheduledJobCreator.User, record.CreatedBy);
        AssertEx.Null(record.DisabledAtUtc, "New job must not have a DisabledAtUtc stamp.");
        AssertEx.Null(record.DeletedAtUtc, "New job must not have a DeletedAtUtc stamp.");

        // Quartz: job exists in the ADO store.
        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);
        AssertEx.True(
            await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Quartz job must be scheduled after CreateJobAsync.");

        // Quartz: at least one trigger attached.
        var triggers = await scheduler.GetTriggersOfJob(jobKey, CancellationToken.None).ConfigureAwait(false);
        AssertEx.True(triggers.Count > 0, "At least one trigger must be created.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithPreventOverlap_JobDetailStoredWithCorrectDataMapKey()
    {
        // NonOverlappingSchedulerDispatchJob and SchedulerDispatchJob are internal to the Application assembly;
        // we verify the PreventOverlap flag is persisted on the record and the Quartz job carries the
        // ScheduledJobIdKey data-map entry, which is the observable contract from this test assembly.
        var dbPath = GetDatabasePath("create-no-overlap.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var input = ValidCronInput(preventOverlap: true);
        var record = await service.CreateJobAsync(input).ConfigureAwait(false);

        // Store: PreventOverlap persisted.
        AssertEx.Equal(true, record.PreventOverlap, "PreventOverlap must be persisted as true.");

        // Quartz: job exists and carries the ScheduledJobIdKey data map entry.
        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);
        var jobDetail = await scheduler.GetJobDetail(jobKey, CancellationToken.None).ConfigureAwait(false);
        AssertEx.NotNull(jobDetail, "Job detail must be retrievable.");
        AssertEx.Equal(
            record.Id.ToString(),
            jobDetail!.JobDataMap.GetString(SchedulerJobKeys.ScheduledJobIdKey),
            "Job data map must carry the definition id.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithPreventOverlapFalse_JobDetailStoredWithCorrectDataMapKey()
    {
        var dbPath = GetDatabasePath("create-allow-overlap.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var input = ValidCronInput(preventOverlap: false);
        var record = await service.CreateJobAsync(input).ConfigureAwait(false);

        // Store: PreventOverlap persisted as false.
        AssertEx.Equal(false, record.PreventOverlap, "PreventOverlap must be persisted as false.");

        // Quartz: job exists.
        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);
        AssertEx.True(
            await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Quartz job must be scheduled for a non-overlapping job.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Update — re-maps schedule; definition updated in store + Quartz
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task UpdateJobAsync_ChangesDisplayNameAndReschedulesQuartzJob()
    {
        var dbPath = GetDatabasePath("update-remap.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var created = await service.CreateJobAsync(ValidCronInput("0 0 6 * * ?")).ConfigureAwait(false);
        var jobKey = new JobKey(created.Id.ToString("N"), SchedulerJobKeys.Group);

        // Update: change cron expression.
        var updatedInput = ValidCronInput("0 0 12 * * ?", displayName: "Updated Job");
        var updated = await service.UpdateJobAsync(created.Id, updatedInput).ConfigureAwait(false);
        AssertEx.NotNull(updated, "UpdateJobAsync must return the updated record.");
        AssertEx.Equal("Updated Job", updated!.DisplayName);

        // Quartz: job still exists (rescheduled, not removed).
        AssertEx.True(
            await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Quartz job must still exist after update.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task UpdateJobAsync_PreservesEnabledState()
    {
        var dbPath = GetDatabasePath("update-preserves-enabled.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var created = await service.CreateJobAsync(ValidCronInput()).ConfigureAwait(false);

        // Disable then update; enabled state must stay false.
        await service.SetEnabledAsync(created.Id, false).ConfigureAwait(false);
        var updated = await service.UpdateJobAsync(created.Id, ValidCronInput()).ConfigureAwait(false);
        AssertEx.NotNull(updated, "UpdateJobAsync must return the updated record.");
        AssertEx.Equal(false, updated!.Enabled, "Update must preserve the disabled state.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    // SetEnabled — toggles Quartz schedule and stamps DisabledAtUtc
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task SetEnabledAsync_WhenDisabling_UnschedulesQuartzJobAndStampsDisabledAtUtc()
    {
        var dbPath = GetDatabasePath("disable-unschedule.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var created = await service.CreateJobAsync(ValidCronInput()).ConfigureAwait(false);
        var jobKey = new JobKey(created.Id.ToString("N"), SchedulerJobKeys.Group);

        // Verify scheduled initially.
        AssertEx.True(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Job must be scheduled before disabling.");

        var disabled = await service.SetEnabledAsync(created.Id, false).ConfigureAwait(false);
        AssertEx.NotNull(disabled, "SetEnabledAsync must return the updated record.");
        AssertEx.Equal(false, disabled!.Enabled, "Disabled record must have Enabled=false.");
        AssertEx.True(disabled.DisabledAtUtc.HasValue, "DisabledAtUtc must be stamped on disable.");

        // Quartz: job must be removed.
        AssertEx.False(
            await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Quartz job must be unscheduled after SetEnabled(false).");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task SetEnabledAsync_WhenEnabling_ReschedulesQuartzJob()
    {
        var dbPath = GetDatabasePath("enable-reschedule.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var created = await service.CreateJobAsync(ValidCronInput()).ConfigureAwait(false);
        var jobKey = new JobKey(created.Id.ToString("N"), SchedulerJobKeys.Group);

        await service.SetEnabledAsync(created.Id, false).ConfigureAwait(false);
        AssertEx.False(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Job must be unscheduled after disable.");

        var enabled = await service.SetEnabledAsync(created.Id, true).ConfigureAwait(false);
        AssertEx.NotNull(enabled, "SetEnabledAsync must return the updated record.");
        AssertEx.Equal(true, enabled!.Enabled);
        AssertEx.Null(enabled.DisabledAtUtc, "DisabledAtUtc must be cleared on re-enable.");

        AssertEx.True(
            await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Quartz job must be rescheduled after SetEnabled(true).");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Delete — unschedules + soft-deletes; runs are preserved
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task DeleteJobAsync_UnschedulesAndSoftDeletesDefinition()
    {
        var dbPath = GetDatabasePath("delete-soft.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var created = await service.CreateJobAsync(ValidCronInput()).ConfigureAwait(false);
        var jobKey = new JobKey(created.Id.ToString("N"), SchedulerJobKeys.Group);

        AssertEx.True(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Job must be scheduled before delete.");

        await service.DeleteJobAsync(created.Id).ConfigureAwait(false);

        // Quartz: job removed.
        AssertEx.False(
            await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Quartz job must be removed after DeleteJobAsync.");

        // Store: definition excluded from default list (soft-deleted).
        var jobs = await service.ListJobsAsync(includeDeleted: false).ConfigureAwait(false);
        AssertEx.False(jobs.Any(j => j.Id == created.Id),
            "Soft-deleted definition must not appear in default ListJobsAsync.");

        // Store: definition visible when includeDeleted=true.
        var allJobs = await service.ListJobsAsync(includeDeleted: true).ConfigureAwait(false);
        var deletedRecord = allJobs.FirstOrDefault(j => j.Id == created.Id);
        AssertEx.NotNull(deletedRecord, "Soft-deleted definition must appear with includeDeleted=true.");
        AssertEx.True(deletedRecord!.DeletedAtUtc.HasValue, "DeletedAtUtc must be stamped.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    // TriggerNow — guards disabled / not-scheduled states
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TriggerNowAsync_WhenJobIsDisabled_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("trigger-disabled.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var created = await service.CreateJobAsync(ValidCronInput()).ConfigureAwait(false);
        await service.SetEnabledAsync(created.Id, false).ConfigureAwait(false);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => service.TriggerNowAsync(created.Id)).ConfigureAwait(false);

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task TriggerNowAsync_WhenJobNotFound_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("trigger-notfound.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(
            () => service.TriggerNowAsync(Guid.NewGuid())).ConfigureAwait(false);

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    // ListTemplatesAsync — returns registered templates
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ListTemplatesAsync_ReturnsRegisteredTemplates()
    {
        var dbPath = GetDatabasePath("list-templates.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var templates = service.ListTemplatesAsync();

        AssertEx.True(templates.Count > 0, "At least one template must be registered.");
        AssertEx.True(templates.Any(t => t.TemplateId == TestEchoScheduledJobHandler.Id),
            "The test.echo template must be in the registry.");
    }

    // ──────────────────────────────────────────────────────────────────────
    // Auto-interrupt opt-in — Marker 4 makes UseJobAutoInterrupt actually apply
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task CreateJobAsync_OptsJobIntoAutoInterrupt()
    {
        var dbPath = GetDatabasePath("create-autointerrupt.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var record = await service.CreateJobAsync(ValidCronInput()).ConfigureAwait(false);

        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);
        var jobDetail = AssertEx.NotNull(await scheduler.GetJobDetail(jobKey, CancellationToken.None).ConfigureAwait(false));
        AssertEx.Equal(
            "true",
            jobDetail.JobDataMap.GetString(JobInterruptMonitorPlugin.JobDataMapKeyAutoInterruptable),
            "Every dispatch job must opt into the auto-interrupt monitor, else max-runtime enforcement is a no-op.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithMaxRuntimeSeconds_StoresPerJobMaxRunTimeInMilliseconds()
    {
        var dbPath = GetDatabasePath("create-maxruntime.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var record = await service.CreateJobAsync(ValidCronInput(maxRuntimeSeconds: 120)).ConfigureAwait(false);

        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);
        var jobDetail = AssertEx.NotNull(await scheduler.GetJobDetail(jobKey, CancellationToken.None).ConfigureAwait(false));
        // The plugin parses MaxRunTime as a millisecond long from its string form: 120 s → "120000".
        AssertEx.Equal(
            "120000",
            jobDetail.JobDataMap.GetString(JobInterruptMonitorPlugin.JobDataMapKeyMaxRunTime),
            "Per-job max runtime must be persisted as milliseconds.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    // CancelRunAsync — best-effort cancellation outcomes
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task CancelRunAsync_WhenRunNotFound_ReturnsNotFound()
    {
        var dbPath = GetDatabasePath("cancel-notfound.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var outcome = await service.CancelRunAsync(Guid.NewGuid()).ConfigureAwait(false);

        AssertEx.Equal(RunCancellationOutcome.NotFound, outcome);
    }

    [Test]
    public async Task CancelRunAsync_WhenRunAlreadyTerminal_ReturnsAlreadyTerminal()
    {
        var dbPath = GetDatabasePath("cancel-terminal.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var runStore = provider.GetRequiredService<IScheduledJobRunStore>();

        var run = await runStore.AddAsync(new ScheduledJobRunInput(
            ScheduledJobId: Guid.NewGuid(),
            TemplateId: TestEchoScheduledJobHandler.Id,
            QuartzFireInstanceId: "fire-terminal",
            TriggeredBy: ScheduledRunTrigger.Schedule,
            Status: ScheduledRunStatus.Succeeded,
            ScheduledFireTimeUtc: null,
            ActualFireTimeUtc: null)).ConfigureAwait(false);

        var outcome = await service.CancelRunAsync(run.Id).ConfigureAwait(false);

        AssertEx.Equal(RunCancellationOutcome.AlreadyTerminal, outcome);
    }

    [Test]
    public async Task CancelRunAsync_WhenRunActiveButNotExecutingInQuartz_StampsRequestAndReportsNotRunning()
    {
        var dbPath = GetDatabasePath("cancel-not-running.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var runStore = provider.GetRequiredService<IScheduledJobRunStore>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        // A Running row whose fire instance is not actually executing — Quartz.Interrupt finds nothing to interrupt.
        var run = await runStore.AddAsync(new ScheduledJobRunInput(
            ScheduledJobId: Guid.NewGuid(),
            TemplateId: TestEchoScheduledJobHandler.Id,
            QuartzFireInstanceId: "fire-not-active",
            TriggeredBy: ScheduledRunTrigger.Schedule,
            Status: ScheduledRunStatus.Running,
            ScheduledFireTimeUtc: null,
            ActualFireTimeUtc: null)).ConfigureAwait(false);

        var outcome = await service.CancelRunAsync(run.Id).ConfigureAwait(false);

        AssertEx.Equal(RunCancellationOutcome.RequestedButNotRunning, outcome);

        // The request must still be recorded even when there was no active fire to interrupt.
        var reread = AssertEx.NotNull(await runStore.GetByIdAsync(run.Id).ConfigureAwait(false));
        AssertEx.True(reread.CancellationRequestedAtUtc.HasValue,
            "CancellationRequestedAtUtc must be stamped even when the run is not actively executing.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Realtime — definition mutations publish jobDefinitionChanged
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task CreateJobAsync_PublishesJobDefinitionChanged()
    {
        var dbPath = GetDatabasePath("publish-created.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        var publisher = Substitute.For<ISchedulerEventPublisher>();
        await using var provider = BuildEnabledProvider(dbPath, eventPublisher: publisher);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var record = await service.CreateJobAsync(ValidCronInput()).ConfigureAwait(false);

        await publisher.Received(1).PublishDefinitionAsync(
            Arg.Is<SchedulerDefinitionHubEvent>(e =>
                e.EventType == SchedulerHubEvents.JobDefinitionChanged &&
                e.ScheduledJobId == record.Id &&
                e.Action == "created"),
            Arg.Any<CancellationToken>());
    }

    // ──────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private async Task MigrateAsync(string dbPath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(dbPath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private static ServiceProvider BuildEnabledProvider(
        string dbPath,
        IScheduledJobHandler? extraHandler = null,
        ISchedulerEventPublisher? eventPublisher = null)
    {
        var services = new ServiceCollection();
        // Use NullLoggerFactory (static singleton) so Quartz's static LogProvider does not cache a reference to a
        // disposable LoggerFactory that gets torn down when a per-test ServiceProvider is disposed (which surfaces as
        // ObjectDisposedException in a later test). Mirrors NodeSchedulerRegistrationTests.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        services.AddSingleton<IScheduledJobHandler, TestEchoScheduledJobHandler>();
        if (extraHandler is not null)
        {
            services.AddSingleton<IScheduledJobHandler>(_ => extraHandler);
        }

        // Real stores backed by the migrated SQLite DB with encryption interceptors, mirroring the
        // production NodeApplicationServiceCollectionExtensions registration.
        services.AddSingleton<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddSingleton<NodeEncryptionSaveChangesInterceptor>();
        services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        services.AddDbContext<NodeChatDbContext>((sp, options) =>
        {
            options.UseSqlite($"Data Source={dbPath}")
                   .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                   .AddInterceptors(
                       sp.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                       sp.GetRequiredService<NodeEncryptionMaterializationInterceptor>());
        });

        services.AddScoped<IScheduledJobDefinitionStore, ScheduledJobDefinitionStore>();
        services.AddScoped<IScheduledJobRunStore, ScheduledJobRunStore>();

        // TimeProvider is required by ScheduledJobManagementService.
        services.AddSingleton(TimeProvider.System);

        var config = BuildConfig(connectionString: $"Data Source={dbPath}");
        services.AddSingleton<IConfiguration>(config);
        new MinimalHostApplicationBuilder(services).AddNodeScheduler(config);

        // Override the no-op publisher registered by AddNodeScheduler (last registration wins) when a test supplies one.
        if (eventPublisher is not null)
        {
            services.AddSingleton(eventPublisher);
        }

        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfig(string connectionString) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Scheduler:Enabled"] = "true",
                ["Scheduler:MaxConcurrency"] = "2",
                ["Scheduler:DefaultMaxRuntimeMinutes"] = "5",
                ["Scheduler:QuartzTablePrefix"] = "QRTZ_",
                ["ConnectionStrings:node-sqlite"] = connectionString
            })
            .Build();

    // Valid cron input using the test.echo template (supports OneShot + Cron).
    private static ScheduledJobManagementInput ValidCronInput(
        string cronExpression = "0 0 * * * ?",
        string displayName = "Test Cron Job",
        string templateId = TestEchoScheduledJobHandler.Id,
        string timeZoneId = "UTC",
        bool preventOverlap = false,
        int? maxRuntimeSeconds = null) =>
        new(
            TemplateId: templateId,
            DisplayName: displayName,
            Description: null,
            ScheduleKind: ScheduleKind.Cron,
            CronExpression: cronExpression,
            IntervalSeconds: null,
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            TimeZoneId: timeZoneId,
            MisfirePolicy: SchedulerMisfirePolicy.Smart,
            PreventOverlap: preventOverlap,
            MaxRuntimeSeconds: maxRuntimeSeconds,
            Parameters: null);

    /// <summary>
    ///     Minimal <see cref="IHostApplicationBuilder" /> shim that satisfies the extension method's
    ///     <c>builder.Services</c> and <c>builder.Configuration</c> needs without spinning up a full host.
    /// </summary>
    private sealed class MinimalHostApplicationBuilder(IServiceCollection services) : IHostApplicationBuilder
    {
        public IServiceCollection Services { get; } = services;
        public IConfigurationManager Configuration { get; } = new ConfigurationManager();
        public IHostEnvironment Environment { get; } = Substitute.For<IHostEnvironment>();
        public ILoggingBuilder Logging { get; } = Substitute.For<ILoggingBuilder>();
        public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();
        public IMetricsBuilder Metrics { get; } = Substitute.For<IMetricsBuilder>();

        public void ConfigureContainer<TContainerBuilder>(
            IServiceProviderFactory<TContainerBuilder> factory,
            Action<TContainerBuilder>? configure = null) where TContainerBuilder : notnull { }
    }

    /// <summary>
    ///     Minimal handler that supports only SimpleInterval — used for schedule-kind validation tests.
    /// </summary>
    private sealed class SimpleIntervalOnlyHandler : IScheduledJobHandler
    {
        public const string Id = "test.simple-interval";

        public string TemplateId => Id;

        public ScheduledJobTemplateDescriptor Descriptor { get; } = new(
            TemplateId: Id,
            DisplayName: "Simple Interval (test)",
            Description: "Test handler that only supports SimpleInterval.",
            ParameterSchema: null,
            DefaultParameters: null,
            SupportedScheduleKinds: [ScheduleKind.SimpleInterval],
            DefaultScheduleKind: ScheduleKind.SimpleInterval,
            DefaultMisfirePolicy: SchedulerMisfirePolicy.Smart,
            DefaultMaxRuntimeSeconds: null,
            AllowManualTrigger: false,
            AllowAgentCreation: false);

        public Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
