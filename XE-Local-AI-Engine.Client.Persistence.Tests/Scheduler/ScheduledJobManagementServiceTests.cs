namespace XE_Local_AI_Engine.Client.Persistence.Tests.Scheduler;

using Microsoft.Data.Sqlite;
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
using XE_Local_AI_Engine.Client.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Handlers;

/// <summary>
///     Integration tests for <see cref="ScheduledJobManagementService" />.
///     Uses a fully-migrated temporary SQLite database + the real Quartz ADO.NET store so both store state
///     and Quartz job/trigger state are observable in the same process.
/// </summary>
public sealed class ScheduledJobManagementServiceTests : IDisposable
{
    private readonly NullNodeSqliteKeyHolder _keyHolder = new();

    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "xe-sched-mgmt-" + Guid.NewGuid().ToString("N"));

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

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithScheduleKindNotInTemplate_ThrowsValidation()
    {
        // TestEchoScheduledJobHandler only supports OneShot and Cron — SimpleInterval is not allowed.
        var dbPath = GetDatabasePath("val-bad-kind.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = new ScheduledJobManagementInput(TestEchoScheduledJobHandler.Id,
            "Bad kind",
            Description: null,
            ScheduleKind.SimpleInterval, // not in SupportedScheduleKinds
            CronExpression: null,
            IntervalSeconds: 60,
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            "UTC",
            SchedulerMisfirePolicy.Smart,
            PreventOverlap: false,
            MaxRuntimeSeconds: null,
            Parameters: null);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithEmptyCronExpression_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-empty-cron.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = ValidCronInput("");

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithInvalidCronExpression_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-bad-cron.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = ValidCronInput("not-a-valid-cron");

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithNonPositiveIntervalSeconds_ThrowsValidation()
    {
        // Need a handler that supports SimpleInterval — build one ad-hoc.
        var dbPath = GetDatabasePath("val-bad-interval.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath, new SimpleIntervalOnlyHandler());
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = new ScheduledJobManagementInput(SimpleIntervalOnlyHandler.Id,
            "Bad interval",
            Description: null,
            ScheduleKind.SimpleInterval,
            CronExpression: null,
            IntervalSeconds: 0, // must be > 0
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            "UTC",
            SchedulerMisfirePolicy.Smart,
            PreventOverlap: false,
            MaxRuntimeSeconds: null,
            Parameters: null);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithMissingStartAtForOneShot_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-oneshot-no-start.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = new ScheduledJobManagementInput(TestEchoScheduledJobHandler.Id,
            "One-shot no start",
            Description: null,
            ScheduleKind.OneShot,
            CronExpression: null,
            IntervalSeconds: null,
            RepeatCount: null,
            StartAtUtc: null, // required for OneShot
            EndAtUtc: null,
            "UTC",
            SchedulerMisfirePolicy.Smart,
            PreventOverlap: false,
            MaxRuntimeSeconds: null,
            Parameters: null);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithUnresolvableTimeZone_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-bad-tz.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = ValidCronInput(timeZoneId: "Not/A/Real/Zone");

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithNonPositiveMaxRuntimeSeconds_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-bad-maxruntime.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = ValidCronInput(maxRuntimeSeconds: 0); // must be > 0 or null

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.CreateJobAsync(input)).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_WithBlankDisplayName_ThrowsValidation()
    {
        var dbPath = GetDatabasePath("val-blank-name.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();

        var input = ValidCronInput(displayName: "   ");

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.CreateJobAsync(input)).ConfigureAwait(false);
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
        AssertEx.Equal(expected: true, record.Enabled);
        AssertEx.Equal(ScheduledJobCreator.User, record.CreatedBy);
        AssertEx.Null(record.DisabledAtUtc, "New job must not have a DisabledAtUtc stamp.");
        AssertEx.Null(record.DeletedAtUtc, "New job must not have a DeletedAtUtc stamp.");

        // Quartz: job exists in the ADO store.
        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);
        AssertEx.True(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
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
        AssertEx.Equal(expected: true, record.PreventOverlap, "PreventOverlap must be persisted as true.");

        // Quartz: job exists and carries the ScheduledJobIdKey data map entry.
        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);
        var jobDetail = await scheduler.GetJobDetail(jobKey, CancellationToken.None).ConfigureAwait(false);
        AssertEx.NotNull(jobDetail, "Job detail must be retrievable.");
        AssertEx.Equal(record.Id.ToString(),
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
        AssertEx.Equal(expected: false, record.PreventOverlap, "PreventOverlap must be persisted as false.");

        // Quartz: job exists.
        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);
        AssertEx.True(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Quartz job must be scheduled for a non-overlapping job.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Manual — durable on-demand job with NO trigger; fired only by TriggerNow
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task ValidateScheduleFields_AcceptsManualWithNoScheduleFields()
    {
        // A Manual definition must validate with null cron/interval/repeat/start — CreateJobAsync runs validation
        // first, so a successful create proves ValidateScheduleFields accepted Manual.
        var dbPath = GetDatabasePath("manual-validate.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var record = await service.CreateJobAsync(ManualInput()).ConfigureAwait(false);

        AssertEx.Equal(ScheduleKind.Manual, record.ScheduleKind);
        AssertEx.Null(record.CronExpression, "A Manual job has no cron expression.");
        AssertEx.Null(record.IntervalSeconds, "A Manual job has no interval.");
        AssertEx.Null(record.StartAtUtc, "A Manual job has no start time.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_ManualInput_RegistersDurableJobWithNoTrigger()
    {
        var dbPath = GetDatabasePath("manual-durable.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var record = await service.CreateJobAsync(ManualInput()).ConfigureAwait(false);
        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);

        // The durable job exists ...
        AssertEx.True(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "A Manual job must be registered as a durable Quartz job.");

        // ... but it has NO trigger (a Manual job never auto-fires).
        var triggers = await scheduler.GetTriggersOfJob(jobKey, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, triggers.Count, "A Manual job must have no trigger — it never auto-fires.");

        // The job detail is durable, which is what AddJob requires.
        var jobDetail = AssertEx.NotNull(await scheduler.GetJobDetail(jobKey, CancellationToken.None).ConfigureAwait(false));
        AssertEx.True(jobDetail.Durable, "A Manual job detail must be stored durably.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task TriggerNowAsync_OnManualJob_Succeeds()
    {
        var dbPath = GetDatabasePath("manual-trigger.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);

        var record = await service.CreateJobAsync(ManualInput()).ConfigureAwait(false);
        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);

        // TriggerNowAsync must SUCCEED for a durable Manual job that has no trigger: it passes the CheckExists guard and
        // calls Quartz TriggerJob without throwing. We intentionally do NOT start a firing worker here — firing the real
        // dispatch job would write run history through EF to the SAME SQLite file the Quartz ADO job store uses, and the
        // single-writer contention between the Quartz worker thread and the store deadlocks the test. The end-to-end fire
        // (dispatcher → handler → snapshot) is covered deterministically by ModelRecommendationCheckSchedulerPathTests
        // Here we assert the manual-trigger path succeeds and the durable job stays registered/triggerable.
        await service.TriggerNowAsync(record.Id).ConfigureAwait(false);

        AssertEx.True(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "The durable Manual job must remain registered and triggerable after TriggerNowAsync.");
    }

    [Test]
    public async Task SetEnabledAsync_WhenDisablingManualJob_RemovesDurableJob()
    {
        var dbPath = GetDatabasePath("manual-disable.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var record = await service.CreateJobAsync(ManualInput()).ConfigureAwait(false);
        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);

        AssertEx.True(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Manual job must be registered before disabling.");

        var disabled = await service.SetEnabledAsync(record.Id, enabled: false).ConfigureAwait(false);
        AssertEx.NotNull(disabled, "SetEnabledAsync must return the updated record.");
        AssertEx.Equal(expected: false, disabled!.Enabled);

        AssertEx.False(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Disabling a Manual job must remove the durable Quartz job.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Self-heal — a persisted JobDetail whose class name no longer resolves is
    // re-stamped with the current dispatch-job type so it loads/fires again.
    // ──────────────────────────────────────────────────────────────────────

    [Test]
    public async Task TriggerNowAsync_WhenPersistedJobClassNameIsStale_HealsAndSucceeds()
    {
        var dbPath = GetDatabasePath("heal-trigger-stale-class.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var record = await service.CreateJobAsync(ManualInput()).ConfigureAwait(false);
        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);

        // Simulate the namespace-move regression: rewrite the persisted JOB_CLASS_NAME to a type that no longer exists,
        // exactly as an install upgraded across the dispatch-job move would have on disk.
        await CorruptJobClassNameAsync(dbPath, record.Id,
                "XE_Local_AI_Engine.Client.Services.Scheduler.NonOverlappingSchedulerDispatchJob, XE-Local-AI-Engine.Client.Application")
            .ConfigureAwait(false);

        // Before the heal, Quartz cannot materialize the job detail because its stored type does not resolve.
        _ = await AssertEx.ThrowsAsync<JobPersistenceException>(() =>
            scheduler.GetJobDetail(jobKey, CancellationToken.None)).ConfigureAwait(false);

        // TriggerNowAsync re-adds the durable detail with the current type, then fires — it must NOT throw a type-load error.
        await service.TriggerNowAsync(record.Id).ConfigureAwait(false);

        // After the heal the detail resolves again and the durable job remains registered.
        var healedDetail = AssertEx.NotNull(await scheduler.GetJobDetail(jobKey, CancellationToken.None).ConfigureAwait(false));
        AssertEx.True(healedDetail.Durable, "The healed Manual job detail must remain durable.");
        AssertEx.True(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "The job must remain registered after healing.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task ReconcileDurableJobsAsync_RefreshesStaleClassNameWithoutFiring()
    {
        var dbPath = GetDatabasePath("heal-reconcile-stale-class.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var record = await service.CreateJobAsync(ValidCronInput()).ConfigureAwait(false);
        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);

        await CorruptJobClassNameAsync(dbPath, record.Id,
                "XE_Local_AI_Engine.Client.Services.Scheduler.SchedulerDispatchJob, XE-Local-AI-Engine.Client.Application")
            .ConfigureAwait(false);

        // Stale detail does not resolve before reconciliation.
        _ = await AssertEx.ThrowsAsync<JobPersistenceException>(() =>
            scheduler.GetJobDetail(jobKey, CancellationToken.None)).ConfigureAwait(false);

        var healed = await service.ReconcileDurableJobsAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 1, healed, "Exactly one stale durable job detail must be reconciled.");

        // The detail resolves again, the job is still registered, and its trigger schedule is untouched (still present).
        var healedDetail = AssertEx.NotNull(await scheduler.GetJobDetail(jobKey, CancellationToken.None).ConfigureAwait(false));
        AssertEx.True(healedDetail.Durable, "The healed job detail must be durable.");

        var triggers = await scheduler.GetTriggersOfJob(jobKey, CancellationToken.None).ConfigureAwait(false);
        AssertEx.True(triggers.Count > 0, "Reconciliation must not remove the existing trigger.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task ReconcileDurableJobsAsync_SkipsDisabledDefinitions()
    {
        var dbPath = GetDatabasePath("heal-reconcile-skips-disabled.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var record = await service.CreateJobAsync(ValidCronInput()).ConfigureAwait(false);
        // Disabling removes the Quartz job entirely, so reconciliation has nothing to refresh for it.
        await service.SetEnabledAsync(record.Id, enabled: false).ConfigureAwait(false);

        var healed = await service.ReconcileDurableJobsAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 0, healed, "A disabled definition has no durable Quartz job and must not be reconciled.");

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
        var updatedInput = ValidCronInput("0 0 12 * * ?", "Updated Job");
        var updated = await service.UpdateJobAsync(created.Id, updatedInput).ConfigureAwait(false);
        AssertEx.NotNull(updated, "UpdateJobAsync must return the updated record.");
        AssertEx.Equal("Updated Job", updated!.DisplayName);

        // Quartz: job still exists (rescheduled, not removed).
        AssertEx.True(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
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
        await service.SetEnabledAsync(created.Id, enabled: false).ConfigureAwait(false);
        var updated = await service.UpdateJobAsync(created.Id, ValidCronInput()).ConfigureAwait(false);
        AssertEx.NotNull(updated, "UpdateJobAsync must return the updated record.");
        AssertEx.Equal(expected: false, updated!.Enabled, "Update must preserve the disabled state.");

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

        var disabled = await service.SetEnabledAsync(created.Id, enabled: false).ConfigureAwait(false);
        AssertEx.NotNull(disabled, "SetEnabledAsync must return the updated record.");
        AssertEx.Equal(expected: false, disabled!.Enabled, "Disabled record must have Enabled=false.");
        AssertEx.True(disabled.DisabledAtUtc.HasValue, "DisabledAtUtc must be stamped on disable.");

        // Quartz: job must be removed.
        AssertEx.False(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
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

        await service.SetEnabledAsync(created.Id, enabled: false).ConfigureAwait(false);
        AssertEx.False(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Job must be unscheduled after disable.");

        var enabled = await service.SetEnabledAsync(created.Id, enabled: true).ConfigureAwait(false);
        AssertEx.NotNull(enabled, "SetEnabledAsync must return the updated record.");
        AssertEx.Equal(expected: true, enabled!.Enabled);
        AssertEx.Null(enabled.DisabledAtUtc, "DisabledAtUtc must be cleared on re-enable.");

        AssertEx.True(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Quartz job must be rescheduled after SetEnabled(true).");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task SetEnabledAsync_WhenTemplateNotRegistered_ThrowsAndLeavesJobDisabledAndUnscheduled()
    {
        // Regression: the template check used to run AFTER the durable Enabled flag was flipped, leaving a job that
        // read as enabled but was never scheduled. The definition is written straight to the store (CreateJobAsync
        // would reject the unknown template) so only SetEnabledAsync is under test.
        var dbPath = GetDatabasePath("enable-unknown-template.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var store = provider.GetRequiredService<IScheduledJobDefinitionStore>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var stored = await store.AddAsync(new ScheduledJobDefinitionInput("does-not-exist",
                                              "Orphaned template job",
                                              Description: null,
                                              Enabled: false,
                                              ScheduleKind.Cron,
                                              "0 0 * * * ?",
                                              IntervalSeconds: null,
                                              RepeatCount: null,
                                              StartAtUtc: null,
                                              EndAtUtc: null,
                                              "UTC",
                                              SchedulerMisfirePolicy.Smart,
                                              PreventOverlap: false,
                                              MaxRuntimeSeconds: null,
                                              ParameterJson: null,
                                              ScheduledJobCreator.User))
                                 .ConfigureAwait(false);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.SetEnabledAsync(stored.Id, enabled: true))
                      .ConfigureAwait(false);

        var reloaded = await store.GetByIdAsync(stored.Id).ConfigureAwait(false);
        AssertEx.NotNull(reloaded, "The definition must still exist after a rejected enable.");
        AssertEx.Equal(expected: false, reloaded!.Enabled, "A rejected enable must not persist Enabled=true.");

        AssertEx.False(await scheduler.CheckExists(new JobKey(stored.Id.ToString("N"), SchedulerJobKeys.Group), CancellationToken.None).ConfigureAwait(false),
            "A rejected enable must not schedule anything in Quartz.");

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
        AssertEx.False(await scheduler.CheckExists(jobKey, CancellationToken.None).ConfigureAwait(false),
            "Quartz job must be removed after DeleteJobAsync.");

        // Store: definition excluded from default list (soft-deleted).
        var jobs = await service.ListJobsAsync().ConfigureAwait(false);
        AssertEx.False(jobs.Any(j => j.Id == created.Id),
            "Soft-deleted definition must not appear in default ListJobsAsync.");

        // Store: definition visible when includeDeleted=true.
        var allJobs = await service.ListJobsAsync(true).ConfigureAwait(false);
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
        await service.SetEnabledAsync(created.Id, enabled: false).ConfigureAwait(false);

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.TriggerNowAsync(created.Id)).ConfigureAwait(false);

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

        await AssertEx.ThrowsAsync<ScheduledJobValidationException>(() => service.TriggerNowAsync(Guid.NewGuid())).ConfigureAwait(false);

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
    // Auto-interrupt opt-in — UseJobAutoInterrupt actually applies
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
        AssertEx.Equal("true",
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
        AssertEx.Equal("120000",
            jobDetail.JobDataMap.GetString(JobInterruptMonitorPlugin.JobDataMapKeyMaxRunTime),
            "Per-job max runtime must be persisted as milliseconds.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_ForRunAgentWithoutMaxRuntime_DerivesTheCeilingFromTheNodeMessageTimeout()
    {
        var dbPath = GetDatabasePath("create-runagent-derived-maxruntime.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var record = await service.CreateJobAsync(ValidCronInput(templateId: RunSavedAgentHandler.TemplateIdValue)).ConfigureAwait(false);

        var jobKey = new JobKey(record.Id.ToString("N"), SchedulerJobKeys.Group);
        var jobDetail = AssertEx.NotNull(await scheduler.GetJobDetail(jobKey, CancellationToken.None).ConfigureAwait(false));
        // 600 s node timeout + 300 s slack → 900 000 ms. Without this the job fell back to the global 5-minute default
        // and the auto-interrupt killed an unattended run long before its own invocation deadline.
        AssertEx.Equal("900000",
            jobDetail.JobDataMap.GetString(JobInterruptMonitorPlugin.JobDataMapKeyMaxRunTime),
            "A run-agent schedule with no operator ceiling must derive one from the node message timeout.");

        await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateJobAsync_ForRunAgent_RaisedNodeTimeoutRaisesTheCeiling_AndAnOperatorValueStillWins()
    {
        var dbPath = GetDatabasePath("create-runagent-raised-maxruntime.sqlite");
        await MigrateAsync(dbPath).ConfigureAwait(false);

        await using var provider = BuildEnabledProvider(dbPath, maxMessageRequestTimeoutSeconds: 1800);
        var service = provider.GetRequiredService<IScheduledJobManagementService>();
        var schedulerFactory = provider.GetRequiredService<ISchedulerFactory>();
        var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None).ConfigureAwait(false);
        await scheduler.Start(CancellationToken.None).ConfigureAwait(false);

        var derived = await service.CreateJobAsync(ValidCronInput(templateId: RunSavedAgentHandler.TemplateIdValue,
                                        displayName: "Derived ceiling"))
                                   .ConfigureAwait(false);
        var explicitCeiling = await service.CreateJobAsync(ValidCronInput(templateId: RunSavedAgentHandler.TemplateIdValue,
                                               displayName: "Operator ceiling",
                                               maxRuntimeSeconds: 90))
                                          .ConfigureAwait(false);

        var derivedDetail = AssertEx.NotNull(await scheduler.GetJobDetail(new JobKey(derived.Id.ToString("N"), SchedulerJobKeys.Group), CancellationToken.None)
                                                            .ConfigureAwait(false));
        AssertEx.Equal("2100000",
            derivedDetail.JobDataMap.GetString(JobInterruptMonitorPlugin.JobDataMapKeyMaxRunTime),
            "Raising the node message timeout must raise the run-agent ceiling with it (1800 s + 300 s slack).");

        var explicitDetail = AssertEx.NotNull(await scheduler.GetJobDetail(new JobKey(explicitCeiling.Id.ToString("N"), SchedulerJobKeys.Group), CancellationToken.None)
                                                             .ConfigureAwait(false));
        AssertEx.Equal("90000",
            explicitDetail.JobDataMap.GetString(JobInterruptMonitorPlugin.JobDataMapKeyMaxRunTime),
            "An operator-set per-schedule ceiling is authoritative and is never widened by the node setting.");

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

        var run = await runStore.AddAsync(new ScheduledJobRunInput(Guid.NewGuid(),
            TestEchoScheduledJobHandler.Id,
            "fire-terminal",
            ScheduledRunTrigger.Schedule,
            ScheduledRunStatus.Succeeded,
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
        var run = await runStore.AddAsync(new ScheduledJobRunInput(Guid.NewGuid(),
            TestEchoScheduledJobHandler.Id,
            "fire-not-active",
            ScheduledRunTrigger.Schedule,
            ScheduledRunStatus.Running,
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

        await publisher.Received(1).PublishDefinitionAsync(Arg.Is<SchedulerDefinitionHubEvent>(e =>
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

    // Rewrites the persisted Quartz JOB_CLASS_NAME for a definition to an arbitrary (here: non-resolvable) type name,
    // reproducing the on-disk state of a node that stored the job before the dispatch job moved namespaces.
    private static async Task CorruptJobClassNameAsync(string dbPath, Guid definitionId, string staleClassName)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE QRTZ_JOB_DETAILS SET JOB_CLASS_NAME = $className WHERE JOB_NAME = $jobName AND JOB_GROUP = $jobGroup;";
        command.Parameters.AddWithValue("$className", staleClassName);
        command.Parameters.AddWithValue("$jobName", definitionId.ToString("N"));
        command.Parameters.AddWithValue("$jobGroup", SchedulerJobKeys.Group);

        var affected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 1, affected, "The job-detail row to corrupt must exist before the heal test.");
    }

    private async Task MigrateAsync(string dbPath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(dbPath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private static ServiceProvider BuildEnabledProvider(string dbPath,
        IScheduledJobHandler? extraHandler = null,
        ISchedulerEventPublisher? eventPublisher = null,
        TestEchoScheduledJobHandler? echoHandler = null,
        int maxMessageRequestTimeoutSeconds = StoredNodeSettings.DefaultMaxMessageRequestTimeoutSeconds)
    {
        var services = new ServiceCollection();
        // Use NullLoggerFactory (static singleton) so Quartz's static LogProvider does not cache a reference to a
        // disposable LoggerFactory that gets torn down when a per-test ServiceProvider is disposed (which surfaces as
        // ObjectDisposedException in a later test). Mirrors NodeSchedulerRegistrationTests.
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Register the test.echo handler. A test that observes invocations passes its own instance (registered as the
        // single test.echo handler); otherwise a fresh default instance is used. The registry keys handlers by template
        // id, so there must be exactly one test.echo handler.
        if (echoHandler is not null)
        {
            services.AddSingleton<IScheduledJobHandler>(echoHandler);
        }
        else
        {
            services.AddSingleton<IScheduledJobHandler, TestEchoScheduledJobHandler>();
        }

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
                   .AddInterceptors(sp.GetRequiredService<NodeEncryptionSaveChangesInterceptor>(),
                       sp.GetRequiredService<NodeEncryptionMaterializationInterceptor>());
        });

        services.AddScoped<IScheduledJobDefinitionStore, ScheduledJobDefinitionStore>();
        services.AddScoped<IScheduledJobRunStore, ScheduledJobRunStore>();

        // TimeProvider is required by ScheduledJobManagementService.
        services.AddSingleton(TimeProvider.System);

        // The management service reads the node "Maximum message request timeout" to derive the run-agent template's
        // implicit Quartz ceiling; AddNodeScheduler does not own that store, so the tests supply it.
        var storedNodeSettings = new StoredNodeSettings
        {
            MaxMessageRequestTimeoutSeconds = maxMessageRequestTimeoutSeconds
        };
        var nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(storedNodeSettings);
        nodeSettingsStore.Load(Arg.Any<CancellationToken>()).Returns(storedNodeSettings);
        services.AddSingleton(nodeSettingsStore);

        var config = BuildConfig($"Data Source={dbPath}");
        services.AddSingleton(config);
        new MinimalHostApplicationBuilder(services).AddNodeScheduler(config);

        // Override the no-op publisher registered by AddNodeScheduler (last registration wins) when a test supplies one.
        if (eventPublisher is not null)
        {
            services.AddSingleton(eventPublisher);
        }

        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildConfig(string connectionString)
    {
        return new ConfigurationBuilder()
               .AddInMemoryCollection(new Dictionary<string, string?>
               {
                   ["Scheduler:Enabled"] = "true",
                   ["Scheduler:MaxConcurrency"] = "2",
                   ["Scheduler:DefaultMaxRuntimeMinutes"] = "5",
                   ["Scheduler:QuartzTablePrefix"] = "QRTZ_",
                   ["ConnectionStrings:node-sqlite"] = connectionString
               })
               .Build();
    }

    // Valid cron input using the test.echo template (supports OneShot + Cron).
    private static ScheduledJobManagementInput ValidCronInput(string cronExpression = "0 0 * * * ?",
        string displayName = "Test Cron Job",
        string templateId = TestEchoScheduledJobHandler.Id,
        string timeZoneId = "UTC",
        bool preventOverlap = false,
        int? maxRuntimeSeconds = null)
    {
        return new ScheduledJobManagementInput(templateId,
            displayName,
            Description: null,
            ScheduleKind.Cron,
            cronExpression,
            IntervalSeconds: null,
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            timeZoneId,
            SchedulerMisfirePolicy.Smart,
            preventOverlap,
            maxRuntimeSeconds,
            Parameters: null);
    }

    // Valid Manual input using the test.echo template (supports Manual). A Manual job carries no schedule fields.
    private static ScheduledJobManagementInput ManualInput(string displayName = "Test Manual Job",
        bool preventOverlap = false)
    {
        return new ScheduledJobManagementInput(TestEchoScheduledJobHandler.Id,
            displayName,
            Description: null,
            ScheduleKind.Manual,
            CronExpression: null,
            IntervalSeconds: null,
            RepeatCount: null,
            StartAtUtc: null,
            EndAtUtc: null,
            "UTC",
            SchedulerMisfirePolicy.SkipMissed,
            preventOverlap,
            MaxRuntimeSeconds: null,
            Parameters: null);
    }

    // Polls a condition for up to ~5 s (Quartz fires off the trigger asynchronously). Returns true once it holds.
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

        public void ConfigureContainer<TContainerBuilder>(IServiceProviderFactory<TContainerBuilder> factory,
            Action<TContainerBuilder>? configure = null) where TContainerBuilder : notnull
        {
        }
    }

    /// <summary>
    ///     Minimal handler that supports only SimpleInterval — used for schedule-kind validation tests.
    /// </summary>
    private sealed class SimpleIntervalOnlyHandler : IScheduledJobHandler
    {
        public const string Id = "test.simple-interval";

        public string TemplateId => Id;

        public ScheduledJobTemplateDescriptor Descriptor { get; } = new(Id,
            "Simple Interval (test)",
            "Test handler that only supports SimpleInterval.",
            ParameterSchema: null,
            DefaultParameters: null,
            [ScheduleKind.SimpleInterval],
            ScheduleKind.SimpleInterval,
            SchedulerMisfirePolicy.Smart,
            DefaultMaxRuntimeSeconds: null,
            AllowManualTrigger: false);

        public Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
