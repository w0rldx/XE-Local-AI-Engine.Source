namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class ScheduledJobRunStoreTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // ScheduledJobRunStore — Add / GetById
    // -------------------------------------------------------------------------

    [Test]
    public async Task AddAsync_ThenGetById_RoundTripsAllFields()
    {
        var databasePath = GetDatabasePath("run-add-getbyid.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, TimeProvider.System);
        var input = CreateRunInput(jobId, ScheduledRunStatus.Queued);
        var added = await store.AddAsync(input);

        AssertEx.True(added.Id != Guid.Empty, "Add should assign a non-empty Id.");
        AssertEx.Equal(jobId, added.ScheduledJobId);
        AssertEx.Equal("tpl-test", added.TemplateId);
        AssertEx.Equal(ScheduledRunStatus.Queued, added.Status);
        AssertEx.Equal(ScheduledRunTrigger.Schedule, added.TriggeredBy);
        AssertEx.True(added.CreatedAtUtc > 0, "Add should stamp CreatedAtUtc.");

        var byId = AssertEx.NotNull(await store.GetByIdAsync(added.Id), "Run should be found by id.");
        AssertEx.Equal(added.Id, byId.Id);
        AssertEx.Equal(added.ScheduledJobId, byId.ScheduledJobId);
        AssertEx.Equal(added.CreatedAtUtc, byId.CreatedAtUtc);
    }

    [Test]
    public async Task GetByIdAsync_WhenIdUnknown_ReturnsNull()
    {
        var databasePath = GetDatabasePath("run-getbyid-unknown.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobRunStore(context, TimeProvider.System);

        var result = await store.GetByIdAsync(Guid.NewGuid());

        AssertEx.Null(result, "Unknown id should return null.");
    }

    // -------------------------------------------------------------------------
    // ListByJobAsync — ordering
    // -------------------------------------------------------------------------

    [Test]
    public async Task ListByJobAsync_OrdersByActualFireTimeDescending()
    {
        var databasePath = GetDatabasePath("run-list-by-job.sqlite");
        using var keyHolder = CreateKeyHolder();
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var otherJobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, clock);

        clock.Advance(100);
        var first = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded) with
        {
            ActualFireTimeUtc = 100
        });
        clock.Advance(100);
        var second = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded) with
        {
            ActualFireTimeUtc = 200
        });
        // A run for a different job must be excluded.
        _ = await store.AddAsync(CreateRunInput(otherJobId, ScheduledRunStatus.Succeeded) with
        {
            ActualFireTimeUtc = 300
        });

        var runs = await store.ListByJobAsync(jobId);

        AssertEx.Equal(expected: 2, runs.Count);
        // Most recent first.
        AssertEx.Equal(second.Id, runs[0].Id);
        AssertEx.Equal(first.Id, runs[1].Id);
    }

    // -------------------------------------------------------------------------
    // ListAsync — status / date / job filters
    // -------------------------------------------------------------------------

    [Test]
    public async Task ListAsync_FiltersByStatus()
    {
        var databasePath = GetDatabasePath("run-list-status.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, TimeProvider.System);

        var succeeded = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded));
        _ = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Failed));

        var result = await store.ListAsync(ScheduledRunStatus.Succeeded);

        AssertEx.Equal(expected: 1, result.Count);
        AssertEx.Equal(succeeded.Id, result[0].Id);
    }

    [Test]
    public async Task ListAsync_FiltersByFromAndToUtcOnActualFireTime()
    {
        var databasePath = GetDatabasePath("run-list-dates.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, TimeProvider.System);

        var early = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded) with
        {
            ActualFireTimeUtc = 1_000
        });
        var mid = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded) with
        {
            ActualFireTimeUtc = 2_000
        });
        _ = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded) with
        {
            ActualFireTimeUtc = 3_000
        });

        var result = await store.ListAsync(fromUtc: 1_000, toUtc: 2_000);

        AssertEx.Equal(expected: 2, result.Count);
        AssertEx.True(result.Any(r => r.Id == early.Id), "Lower bound match should be included.");
        AssertEx.True(result.Any(r => r.Id == mid.Id), "Upper bound match should be included.");
    }

    [Test]
    public async Task ListAsync_FiltersByScheduledJobId()
    {
        var databasePath = GetDatabasePath("run-list-jobid.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var otherJobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, TimeProvider.System);

        var owned = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded));
        _ = await store.AddAsync(CreateRunInput(otherJobId, ScheduledRunStatus.Succeeded));

        var result = await store.ListAsync(scheduledJobId: jobId);

        AssertEx.Equal(expected: 1, result.Count);
        AssertEx.Equal(owned.Id, result[0].Id);
    }

    // -------------------------------------------------------------------------
    // UpsertByFireInstanceAsync — idempotency
    // -------------------------------------------------------------------------

    [Test]
    public async Task UpsertByFireInstanceAsync_WhenCalledTwiceWithSameFireInstanceId_DoesNotDuplicate()
    {
        var databasePath = GetDatabasePath("run-upsert-idempotent.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, TimeProvider.System);
        var input = CreateRunInput(jobId, ScheduledRunStatus.Queued) with
        {
            QuartzFireInstanceId = "fire-123"
        };

        var first = await store.UpsertByFireInstanceAsync(input);
        var second = await store.UpsertByFireInstanceAsync(input);

        AssertEx.Equal(first.Id, second.Id);

        var allRuns = await store.ListByJobAsync(jobId);
        AssertEx.Equal(expected: 1, allRuns.Count);
    }

    [Test]
    public async Task UpsertByFireInstanceAsync_WhenFireInstanceIdIsNull_AlwaysInserts()
    {
        var databasePath = GetDatabasePath("run-upsert-null-fire.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, TimeProvider.System);
        var input = CreateRunInput(jobId, ScheduledRunStatus.Queued) with
        {
            QuartzFireInstanceId = null
        };

        var first = await store.UpsertByFireInstanceAsync(input);
        var second = await store.UpsertByFireInstanceAsync(input);

        AssertEx.True(first.Id != second.Id, "Without a fire instance id, two upserts should produce two distinct runs.");

        var allRuns = await store.ListByJobAsync(jobId);
        AssertEx.Equal(expected: 2, allRuns.Count);
    }

    // -------------------------------------------------------------------------
    // UpdateLifecycleAsync — partial update
    // -------------------------------------------------------------------------

    [Test]
    public async Task UpdateLifecycleAsync_SetsStatusAndStampedFields_LeavesUnspecifiedFieldsIntact()
    {
        var databasePath = GetDatabasePath("run-update-lifecycle.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, TimeProvider.System);
        var added = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Queued) with
        {
            Summary = "initial-summary",
            ErrorMessage = null
        });

        var updated = AssertEx.NotNull(await store.UpdateLifecycleAsync(added.Id,
                ScheduledRunStatus.Running),
            "UpdateLifecycle should return the updated record.");

        AssertEx.Equal(ScheduledRunStatus.Running, updated.Status);
        AssertEx.Equal("initial-summary", updated.Summary);
        AssertEx.Null(updated.CompletedAtUtc);
        AssertEx.Null(updated.DurationMs);
    }

    [Test]
    public async Task UpdateLifecycleAsync_WhenAllFieldsSupplied_AppliesAllChanges()
    {
        var databasePath = GetDatabasePath("run-update-lifecycle-full.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, TimeProvider.System);
        var added = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Running));

        var updated = AssertEx.NotNull(await store.UpdateLifecycleAsync(added.Id,
                ScheduledRunStatus.Succeeded,
                completedAtUtc: 9_999,
                durationMs: 1_234,
                "all done",
                detailsJson: """{"output":"ok"}"""),
            "UpdateLifecycle should return the updated record.");

        AssertEx.Equal(ScheduledRunStatus.Succeeded, updated.Status);
        AssertEx.Equal(expected: 9_999L, updated.CompletedAtUtc);
        AssertEx.Equal(expected: 1_234L, updated.DurationMs);
        AssertEx.Equal("all done", updated.Summary);
        AssertEx.Equal(expected: """{"output":"ok"}""", updated.DetailsJson);
    }

    [Test]
    public async Task UpdateLifecycleAsync_WhenIdUnknown_ReturnsNull()
    {
        var databasePath = GetDatabasePath("run-update-unknown.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobRunStore(context, TimeProvider.System);

        var result = await store.UpdateLifecycleAsync(Guid.NewGuid(), ScheduledRunStatus.Failed);

        AssertEx.Null(result, "UpdateLifecycle on unknown id should return null.");
    }

    // -------------------------------------------------------------------------
    // RequestCancellationAsync — stamps timestamp, leaves status
    // -------------------------------------------------------------------------

    [Test]
    public async Task RequestCancellationAsync_StampsCancellationRequestedAtUtc_WithoutChangingStatus()
    {
        var databasePath = GetDatabasePath("run-request-cancel.sqlite");
        using var keyHolder = CreateKeyHolder();
        var clock = new MutableTimeProvider(7_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, clock);
        var added = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Running));

        var updated = AssertEx.NotNull(await store.RequestCancellationAsync(added.Id, requestedAtUtc: 7_500),
            "RequestCancellation should return the updated record.");

        AssertEx.Equal(expected: 7_500L, updated.CancellationRequestedAtUtc);
        AssertEx.Equal(ScheduledRunStatus.Running, updated.Status,
            "Requesting cancellation must not change the run status (the handler moves it terminal).");

        var reread = AssertEx.NotNull(await store.GetByIdAsync(added.Id));
        AssertEx.Equal(expected: 7_500L, reread.CancellationRequestedAtUtc);
    }

    [Test]
    public async Task RequestCancellationAsync_WhenIdUnknown_ReturnsNull()
    {
        var databasePath = GetDatabasePath("run-request-cancel-unknown.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var store = new ScheduledJobRunStore(context, TimeProvider.System);

        var result = await store.RequestCancellationAsync(Guid.NewGuid(), requestedAtUtc: 1_000);

        AssertEx.Null(result, "RequestCancellation on unknown id should return null.");
    }

    // -------------------------------------------------------------------------
    // MarkStaleActiveRunsAsync
    // -------------------------------------------------------------------------

    [Test]
    public async Task MarkStaleActiveRunsAsync_TransitionsQueuedAndRunningToTerminal()
    {
        var databasePath = GetDatabasePath("run-stale-active.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, TimeProvider.System);

        var queued = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Queued));
        var running = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Running));
        var succeeded = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded));

        var reconciled = await store.MarkStaleActiveRunsAsync(ScheduledRunStatus.Cancelled, "node restart");

        AssertEx.Equal(expected: 2, reconciled);

        var queuedAfter = AssertEx.NotNull(await store.GetByIdAsync(queued.Id));
        AssertEx.Equal(ScheduledRunStatus.Cancelled, queuedAfter.Status);
        AssertEx.Equal("node restart", queuedAfter.ErrorMessage);
        AssertEx.True(queuedAfter.CompletedAtUtc.HasValue, "CompletedAtUtc should be stamped on stale reconciliation.");

        var runningAfter = AssertEx.NotNull(await store.GetByIdAsync(running.Id));
        AssertEx.Equal(ScheduledRunStatus.Cancelled, runningAfter.Status);

        var succeededAfter = AssertEx.NotNull(await store.GetByIdAsync(succeeded.Id));
        AssertEx.Equal(ScheduledRunStatus.Succeeded, succeededAfter.Status);
    }

    [Test]
    public async Task MarkStaleActiveRunsAsync_WhenNoneActive_ReturnsZero()
    {
        var databasePath = GetDatabasePath("run-stale-none.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, TimeProvider.System);
        _ = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded));

        var reconciled = await store.MarkStaleActiveRunsAsync(ScheduledRunStatus.Cancelled, "no stale");

        AssertEx.Equal(expected: 0, reconciled);
    }

    // -------------------------------------------------------------------------
    // SweepOlderThanAsync — retention
    // -------------------------------------------------------------------------

    [Test]
    public async Task SweepOlderThanAsync_DeletesOldRunsAndTheirEvents_KeepsNewerOnes()
    {
        var databasePath = GetDatabasePath("run-sweep.sqlite");
        using var keyHolder = CreateKeyHolder();
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var runStore = new ScheduledJobRunStore(context, clock);
        var eventStore = new ScheduledJobRunEventStore(context, clock);

        // Old run — CreatedAtUtc = 1_000.
        var oldRun = await runStore.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded));
        _ = await eventStore.AddAsync(new ScheduledJobRunEventInput(oldRun.Id, Sequence: 1, ScheduledRunEventLevel.Info, "old-event"));

        // New run — CreatedAtUtc = 2_000.
        clock.Advance(1_000);
        var newRun = await runStore.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded));
        _ = await eventStore.AddAsync(new ScheduledJobRunEventInput(newRun.Id, Sequence: 1, ScheduledRunEventLevel.Info, "new-event"));

        // Cut off at 1_500 → old run (1_000) is expired; new run (2_000) survives.
        var deleted = await runStore.SweepOlderThanAsync(1_500);

        AssertEx.Equal(expected: 1, deleted);
        AssertEx.Null(await runStore.GetByIdAsync(oldRun.Id), "Old run should have been swept.");
        AssertEx.NotNull(await runStore.GetByIdAsync(newRun.Id), "New run should survive the sweep.");

        // Cascade: old run's events should also be gone.
        var oldEvents = await eventStore.ListByRunAsync(oldRun.Id);
        AssertEx.Equal(expected: 0, oldEvents.Count);

        var newEvents = await eventStore.ListByRunAsync(newRun.Id);
        AssertEx.Equal(expected: 1, newEvents.Count);
    }

    [Test]
    public async Task SweepOlderThanAsync_WhenNoneExpired_ReturnsZero()
    {
        var databasePath = GetDatabasePath("run-sweep-none.sqlite");
        using var keyHolder = CreateKeyHolder();
        var clock = new MutableTimeProvider(5_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var store = new ScheduledJobRunStore(context, clock);
        _ = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Succeeded));

        var deleted = await store.SweepOlderThanAsync(1_000);

        AssertEx.Equal(expected: 0, deleted);
    }

    // -------------------------------------------------------------------------
    // DetailsJson encryption round-trip
    // -------------------------------------------------------------------------

    [Test]
    public async Task AddAsync_WithDetailsJson_DecryptsOnRead()
    {
        var databasePath = GetDatabasePath("run-details-decrypt.sqlite");
        using var keyHolder = CreateKeyHolder();
        const string detailsJson = """{"result":"success","items":42}""";
        var jobId = Guid.NewGuid();

        Guid runId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new ScheduledJobRunStore(writeContext, TimeProvider.System);
            var added = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Queued) with
            {
                DetailsJson = detailsJson
            });
            AssertEx.Equal(detailsJson, added.DetailsJson);
            runId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new ScheduledJobRunStore(readContext, TimeProvider.System);

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(runId));
        AssertEx.Equal(detailsJson, byId.DetailsJson);
    }

    [Test]
    public async Task AddAsync_WithDetailsJson_StoredAsCiphertext()
    {
        var databasePath = GetDatabasePath("run-details-ciphertext.sqlite");
        using var keyHolder = CreateKeyHolder();
        var detailsJson = "SECRET-DETAILS-" + Guid.NewGuid().ToString("N");
        var jobId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();

            var store = new ScheduledJobRunStore(context, TimeProvider.System);
            _ = await store.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Queued) with
            {
                DetailsJson = detailsJson
            });
        }

        var rawBytes = await ReadRawDetailsJsonAsync(databasePath);
        var plaintextBytes = Encoding.UTF8.GetBytes(detailsJson);

        AssertEx.True(rawBytes.Length > 0, "Encrypted column should have non-empty BLOB data.");
        AssertEx.False(rawBytes.AsSpan().SequenceEqual(plaintextBytes),
            "details_json column should be encrypted at rest, not plaintext.");
    }

    // -------------------------------------------------------------------------
    // ScheduledJobRunEventStore — Add / ListByRun / DataJson encryption
    // -------------------------------------------------------------------------

    [Test]
    public async Task EventStore_AddAsync_ThenListByRun_RoundTripsAndOrdersBySequence()
    {
        var databasePath = GetDatabasePath("event-add-list.sqlite");
        using var keyHolder = CreateKeyHolder();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var jobId = Guid.NewGuid();
        var runStore = new ScheduledJobRunStore(context, TimeProvider.System);
        var eventStore = new ScheduledJobRunEventStore(context, TimeProvider.System);

        var run = await runStore.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Running));
        var otherRun = await runStore.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Running));

        var e1 = await eventStore.AddAsync(new ScheduledJobRunEventInput(run.Id, Sequence: 2, ScheduledRunEventLevel.Info, "second"));
        var e2 = await eventStore.AddAsync(new ScheduledJobRunEventInput(run.Id, Sequence: 1, ScheduledRunEventLevel.Warning, "first"));
        // Event on a different run — must be excluded.
        _ = await eventStore.AddAsync(new ScheduledJobRunEventInput(otherRun.Id, Sequence: 1, ScheduledRunEventLevel.Info, "other-run"));

        var events = await eventStore.ListByRunAsync(run.Id);

        AssertEx.Equal(expected: 2, events.Count);
        AssertEx.Equal(e2.Id, events[0].Id); // sequence 1 first
        AssertEx.Equal(e1.Id, events[1].Id); // sequence 2 second
        AssertEx.Equal(ScheduledRunEventLevel.Warning, events[0].Level);
        AssertEx.Equal("first", events[0].Message);
        AssertEx.True(events[0].OccurredAtUtc > 0, "OccurredAtUtc should be stamped.");
    }

    [Test]
    public async Task EventStore_AddAsync_WithDataJson_DecryptsOnRead()
    {
        var databasePath = GetDatabasePath("event-data-decrypt.sqlite");
        using var keyHolder = CreateKeyHolder();
        const string dataJson = """{"progress":75,"step":"indexing"}""";

        var jobId = Guid.NewGuid();
        Guid eventId;

        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var runStore = new ScheduledJobRunStore(writeContext, TimeProvider.System);
            var eventStore = new ScheduledJobRunEventStore(writeContext, TimeProvider.System);

            var run = await runStore.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Running));
            var added = await eventStore.AddAsync(new ScheduledJobRunEventInput(run.Id, Sequence: 1, ScheduledRunEventLevel.Progress, "progress", dataJson));

            AssertEx.Equal(dataJson, added.DataJson);
            eventId = added.Id;
        }

        // Retrieve indirectly via ListByRunAsync which is the only list method.
        await using var runReadContext = CreateContext(databasePath, keyHolder);
        var runReadStore = new ScheduledJobRunStore(runReadContext, TimeProvider.System);
        var allRuns = await runReadStore.ListByJobAsync(jobId);
        var runId = allRuns[0].Id;

        await using var eventReadContext = CreateContext(databasePath, keyHolder);
        var eventReadStore = new ScheduledJobRunEventStore(eventReadContext, TimeProvider.System);
        var events = await eventReadStore.ListByRunAsync(runId);

        var evt = AssertEx.NotNull(events.FirstOrDefault(e => e.Id == eventId));
        AssertEx.Equal(dataJson, evt.DataJson);
    }

    [Test]
    public async Task EventStore_AddAsync_WithDataJson_StoredAsCiphertext()
    {
        var databasePath = GetDatabasePath("event-data-ciphertext.sqlite");
        using var keyHolder = CreateKeyHolder();
        var dataJson = "SECRET-DATA-" + Guid.NewGuid().ToString("N");
        var jobId = Guid.NewGuid();

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();

            var runStore = new ScheduledJobRunStore(context, TimeProvider.System);
            var eventStore = new ScheduledJobRunEventStore(context, TimeProvider.System);

            var run = await runStore.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Running));
            _ = await eventStore.AddAsync(new ScheduledJobRunEventInput(run.Id, Sequence: 1, ScheduledRunEventLevel.Info, "msg", dataJson));
        }

        var rawBytes = await ReadRawEventDataJsonAsync(databasePath);
        var plaintextBytes = Encoding.UTF8.GetBytes(dataJson);

        AssertEx.True(rawBytes.Length > 0, "Encrypted column should have non-empty BLOB data.");
        AssertEx.False(rawBytes.AsSpan().SequenceEqual(plaintextBytes),
            "data_json column should be encrypted at rest, not plaintext.");
    }

    [Test]
    public async Task EventStore_AddAsync_WithNullDataJson_RoundTripsNull()
    {
        var databasePath = GetDatabasePath("event-data-null.sqlite");
        using var keyHolder = CreateKeyHolder();
        var jobId = Guid.NewGuid();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        var runStore = new ScheduledJobRunStore(context, TimeProvider.System);
        var eventStore = new ScheduledJobRunEventStore(context, TimeProvider.System);

        var run = await runStore.AddAsync(CreateRunInput(jobId, ScheduledRunStatus.Running));
        var added = await eventStore.AddAsync(new ScheduledJobRunEventInput(run.Id, Sequence: 1, ScheduledRunEventLevel.Info, "msg"));

        AssertEx.Null(added.DataJson, "Null DataJson should round-trip as null.");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task<byte[]> ReadRawDetailsJsonAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT details_json FROM scheduled_job_runs LIMIT 1;";
        var value = await command.ExecuteScalarAsync();
        return value as byte[] ?? throw new AssertionException("Expected a non-null encrypted BLOB in details_json.");
    }

    private static async Task<byte[]> ReadRawEventDataJsonAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT data_json FROM scheduled_job_run_events LIMIT 1;";
        var value = await command.ExecuteScalarAsync();
        return value as byte[] ?? throw new AssertionException("Expected a non-null encrypted BLOB in data_json.");
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        return AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static INodeSqliteKeyHolder CreateKeyHolder()
    {
        var key = Enumerable.Range(start: 0, count: 32).Select(static v => (byte)(v + 1)).ToArray();
        return new FixedNodeSqliteKeyHolder(key);
    }

    private static ScheduledJobRunInput CreateRunInput(Guid scheduledJobId, ScheduledRunStatus status)
    {
        return new ScheduledJobRunInput(scheduledJobId,
            "tpl-test",
            QuartzFireInstanceId: null,
            ScheduledRunTrigger.Schedule,
            status,
            ScheduledFireTimeUtc: null,
            ActualFireTimeUtc: null);
    }

    private sealed class MutableTimeProvider(long initialMilliseconds) : TimeProvider
    {
        private long _milliseconds = initialMilliseconds;

        public void Advance(long milliseconds)
        {
            _milliseconds += milliseconds;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(_milliseconds);
        }
    }

    private sealed class FixedNodeSqliteKeyHolder(byte[] key) : INodeSqliteKeyHolder
    {
        private byte[]? _key = key;

        public ReadOnlyMemory<byte> Key
        {
            get
            {
                ObjectDisposedException.ThrowIf(_key is null, this);
                return _key;
            }
        }

        public void Dispose()
        {
            if (_key is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
