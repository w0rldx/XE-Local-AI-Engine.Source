namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public sealed class BenchmarkStoreTests : IDisposable
{
    private const string PolicyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly byte[] PolicyBytes = Encoding.UTF8.GetBytes("{\"rubric\":\"v1\"}");
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task ProjectAndRunLifecycle_UsesFreezeCasScoreAndExplicitDelete()
    {
        var databasePath = GetDatabasePath("lifecycle.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(CreateProject());
        var run = await store.StartRunAsync(CreateRun(project));

        _ = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() => store.UpdateProjectAsync(project.Id, project.Version, CreateProject(project.Id)));
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(run.Id, claimed.RunId);
        AssertEx.Equal(BenchmarkWorkKind.Primary, claimed.Kind);
        var succeeded = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(run.Id, claimed.Run.Version,
            Encoding.UTF8.GetBytes("[{\"text\":\"answer\"}]"), 7, 4096, 100, 12, 120));
        var scored = await store.SetUserScoreAsync(run.Id, 5, succeeded.Version);
        AssertEx.Equal(expected: 5, scored.UserScore);

        await store.DeleteRunAsync(run.Id, scored.Version);
        var editable = AssertEx.NotNull(await store.GetProjectAsync(project.Id));
        AssertEx.False(editable.IsFrozen, "Deleting the last terminal run should derive an editable project.");
        var updated = await store.UpdateProjectAsync(project.Id, editable.Version, CreateProject(project.Id) with
        {
            Name = "Updated"
        });
        AssertEx.Equal("Updated", updated.Name);
    }

    [Test]
    public async Task ClaimNext_ConcurrentConsumers_ClaimsLowestSequenceOnce()
    {
        var databasePath = GetDatabasePath("fifo.sqlite");
        await using (var setup = CreateContext(databasePath))
        {
            await setup.Database.EnsureDeletedAsync();
            await setup.Database.EnsureCreatedAsync();
            var store = new BenchmarkStore(setup, TimeProvider.System);
            var project = await store.CreateProjectAsync(CreateProject());
            _ = await store.StartRunAsync(CreateRun(project));
            project = AssertEx.NotNull(await store.GetProjectAsync(project.Id));
            _ = await store.StartRunAsync(CreateRun(project));
        }

        await using var firstContext = CreateContext(databasePath);
        await using var secondContext = CreateContext(databasePath);
        var claims = await Task.WhenAll(new BenchmarkStore(firstContext, TimeProvider.System).ClaimNextAsync(),
            new BenchmarkStore(secondContext, TimeProvider.System).ClaimNextAsync());
        AssertEx.Equal(expected: 2, claims.Count(static claim => claim is not null));
        AssertEx.Equal(expected: 2, claims.Select(static claim => claim!.QueueSequence).Distinct().Count());
        AssertEx.True(claims[0]!.QueueSequence < claims[1]!.QueueSequence || claims[1]!.QueueSequence < claims[0]!.QueueSequence,
            "Concurrent claims must consume distinct durable sequence values.");
    }

    [Test]
    public async Task StartRun_ConcurrentWithProjectUpdate_ExactlyOneWins()
    {
        var databasePath = GetDatabasePath("start-run-vs-update-race.sqlite");
        BenchmarkProjectRecord project;
        await using (var setup = CreateContext(databasePath))
        {
            await setup.Database.EnsureDeletedAsync();
            await setup.Database.EnsureCreatedAsync();
            project = await new BenchmarkStore(setup, TimeProvider.System).CreateProjectAsync(CreateProject());
        }

        await using var startContext = CreateContext(databasePath);
        await using var updateContext = CreateContext(databasePath);
        var startStore = new BenchmarkStore(startContext, TimeProvider.System);
        var updateStore = new BenchmarkStore(updateContext, TimeProvider.System);

        var startTask = RaceAsync(() => startStore.StartRunAsync(CreateRun(project)));
        var updateTask = RaceAsync(() => updateStore.UpdateProjectAsync(project.Id, project.Version, CreateProject(project.Id) with
        {
            Name = "Updated"
        }));
        var results = await Task.WhenAll(startTask, updateTask).WaitAsync(TimeSpan.FromSeconds(30));
        var (startWon, startConflict) = results[0];
        var (updateWon, updateConflict) = results[1];

        AssertEx.True(startWon ^ updateWon, "Exactly one concurrent writer must win the project version race.");
        if (!startWon)
        {
            AssertEx.Equal("VersionConflict", AssertEx.NotNull(startConflict).Code);
        }

        if (!updateWon)
        {
            AssertEx.Equal("VersionConflict", AssertEx.NotNull(updateConflict).Code);
        }

        await using var verifyContext = CreateContext(databasePath);
        var verifyStore = new BenchmarkStore(verifyContext, TimeProvider.System);
        var runCount = await verifyStore.CountRunsAsync(project.Id);
        var finalProject = AssertEx.NotNull(await verifyStore.GetProjectAsync(project.Id));
        if (startWon)
        {
            AssertEx.Equal(expected: 1, runCount);
            AssertEx.NotNull(await verifyStore.ClaimNextAsync(), "The winning StartRun must leave claimable primary work.");
            AssertEx.Equal("Benchmark", finalProject.Name, "A losing project update must not persist.");
        }
        else
        {
            AssertEx.Equal(expected: 0, runCount);
            AssertEx.Null(await verifyStore.ClaimNextAsync(), "A losing StartRun must not insert a claimable work item.");
            AssertEx.Equal("Updated", finalProject.Name);
        }
    }

    [Test]
    public async Task RecoverOnStartup_FailsRunningPhasesAndKeepsQueuedWork()
    {
        var databasePath = GetDatabasePath("recovery.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var projectA = await store.CreateProjectAsync(CreateProject());
        var primaryRun = await store.StartRunAsync(CreateRun(projectA));
        var primaryClaim = AssertEx.NotNull(await store.ClaimNextAsync());

        var (projectB, revisionB) = await CreateJudgeProjectAsync(store);
        var judgeRun = await store.StartRunAsync(CreateRun(projectB));
        var judgePrimaryClaim = AssertEx.NotNull(await store.ClaimNextAsync());
        var primarySucceeded = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(judgeRun.Id, judgePrimaryClaim.Run.Version,
            Encoding.UTF8.GetBytes("[]"), 1, 4096, 10, null, null, JudgeSeed(revisionB)));
        var judgeClaim = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(BenchmarkWorkKind.Judge, judgeClaim.Kind);
        var queuedProject = await store.CreateProjectAsync(CreateProject());
        var queuedRun = await store.StartRunAsync(CreateRun(queuedProject));

        var recovered = await store.RecoverRunsOnStartupAsync();
        AssertEx.Equal(expected: 2, recovered.Count);
        var primaryAfter = AssertEx.NotNull(await store.GetRunAsync(primaryRun.Id));
        AssertEx.Equal(BenchmarkPrimaryStatus.Failed, primaryAfter.PrimaryStatus);
        AssertEx.Equal(BenchmarkRunJudgeStates.None, primaryAfter.Judge?.State);
        AssertEx.Equal(primaryRun.LastStreamSequence + 1, primaryAfter.LastStreamSequence);
        var judgeAfter = AssertEx.NotNull(await store.GetRunAsync(judgeRun.Id));
        AssertEx.Equal(BenchmarkPrimaryStatus.Succeeded, judgeAfter.PrimaryStatus);
        AssertEx.Equal(BenchmarkRunJudgeStates.Failed, judgeAfter.Judge?.State);
        AssertEx.Equal(primarySucceeded.LastStreamSequence + 1, judgeAfter.LastStreamSequence);
        AssertBytes(primarySucceeded.OutputPartsJson!.Value.Span, judgeAfter.OutputPartsJson!.Value.Span);
        AssertEx.Empty(await store.RecoverRunsOnStartupAsync());
        var queuedClaim = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(queuedRun.Id, queuedClaim.RunId);
        _ = primaryClaim;
    }

    [Test]
    public async Task RecoverOnStartup_CancelRequestedRunningPrimaryBecomesCancelled()
    {
        var databasePath = GetDatabasePath("recovery-cancel-requested.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(CreateProject());
        var run = await store.StartRunAsync(CreateRun(project));
        var claim = AssertEx.NotNull(await store.ClaimNextAsync());
        var requested = await store.CancelAsync(run.Id, claim.Run.Version);

        var recovered = await store.RecoverRunsOnStartupAsync();

        AssertEx.Equal(1, recovered.Count);
        var after = AssertEx.NotNull(await store.GetRunAsync(run.Id));
        AssertEx.Equal(BenchmarkPrimaryStatus.Cancelled, after.PrimaryStatus);
        AssertEx.Equal(BenchmarkRunJudgeStates.None, after.Judge?.State);
        AssertEx.Equal(requested.LastStreamSequence + 1, after.LastStreamSequence);
        AssertEx.Null(after.PrimaryErrorMessage);
        AssertEx.Null(await store.ClaimNextAsync());
    }

    [Test]
    public async Task CancelQueuedPrimary_TerminalizesWorkAndSkipsPendingJudge()
    {
        var databasePath = GetDatabasePath("cancel-queued.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(CreateProject());
        var run = await store.StartRunAsync(CreateRun(project));

        var cancelled = await store.CancelAsync(run.Id, run.Version);

        AssertEx.Equal(BenchmarkPrimaryStatus.Cancelled, cancelled.PrimaryStatus);
        AssertEx.Equal(BenchmarkRunJudgeStates.None, cancelled.Judge?.State);
        AssertEx.Null(await store.ClaimNextAsync(), "Cancelled primary work must not remain claimable and no judge work may be inserted.");
    }

    [Test]
    public async Task CancelJudge_WhenAlreadyCancelled_ReturnsCurrentRunWithoutVersionConflict()
    {
        var databasePath = GetDatabasePath("cancel-judge-idempotent.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var judge = await CreateRunningJudgeAsync(store);

        var cancelled = await store.CancelAsync(judge.RunId, judge.Run.Version);
        var repeated = await store.CancelAsync(judge.RunId, judge.Run.Version);

        AssertEx.Equal(BenchmarkRunJudgeStates.Cancelled, repeated.Judge?.State);
        AssertEx.Equal(cancelled.Version, repeated.Version);
    }

    [Test]
    public async Task JudgeTerminalization_PersistsLatestStreamSequenceForSuccessFailureAndCancellation()
    {
        var databasePath = GetDatabasePath("judge-cursors.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);

        var successful = await CreateRunningJudgeAsync(store);
        var success = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(successful.RunId,
            successful.Version,
            Encoding.UTF8.GetBytes("{}"),
            LastStreamSequence: 12));
        var failing = await CreateRunningJudgeAsync(store);
        var failure = await store.MarkJudgeFailedAsync(failing.RunId, failing.Version, "safe", lastStreamSequence: 22);
        var cancelling = await CreateRunningJudgeAsync(store);
        var cancelled = await store.MarkJudgeCancelledAsync(cancelling.RunId, cancelling.Version, lastStreamSequence: 32);

        AssertEx.Equal(12L, success.LastStreamSequence);
        AssertEx.Equal(22L, failure.LastStreamSequence);
        AssertEx.Equal(32L, cancelled.LastStreamSequence);
        AssertEx.Equal(BenchmarkRunJudgeStates.Succeeded, success.Judge?.State);
        AssertEx.Equal(BenchmarkRunJudgeStates.Failed, failure.Judge?.State);
        AssertEx.Equal(BenchmarkRunJudgeStates.Cancelled, cancelled.Judge?.State);
    }

    [Test]
    public async Task JudgeCompletion_AfterUserScoreUpdate_UsesWorkCasAndPreservesBothResults()
    {
        var databasePath = GetDatabasePath("judge-score-race.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var judge = await CreateRunningJudgeAsync(store);

        var scored = await store.SetUserScoreAsync(judge.RunId, 5, judge.Run.Version);
        var completed = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(judge.RunId,
            judge.Version,
            Encoding.UTF8.GetBytes("{\"score\":4}"),
            LastStreamSequence: 41));

        AssertEx.Equal(expected: 5, completed.UserScore);
        AssertEx.Equal(BenchmarkRunJudgeStates.Succeeded, completed.Judge?.State);
        AssertEx.True(completed.Version > scored.Version);
    }

    [Test]
    public async Task PrimaryCompletion_AfterCancellationRequest_ReconcilesToCancelled()
    {
        var databasePath = GetDatabasePath("primary-cancel-race.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(CreateProject());
        var run = await store.StartRunAsync(CreateRun(project));
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());

        _ = await store.CancelAsync(run.Id, primary.Run.Version);
        var completed = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(run.Id,
            primary.Version,
            Encoding.UTF8.GetBytes("[]"),
            9,
            4096,
            10,
            null,
            null));

        AssertEx.Equal(BenchmarkPrimaryStatus.Cancelled, completed.PrimaryStatus);
        AssertEx.Equal(BenchmarkRunJudgeStates.None, completed.Judge?.State);
        AssertEx.Null(completed.OutputPartsJson);
        AssertEx.Null(await store.ClaimNextAsync(), "Cancellation must not leave primary or judge work claimable.");
    }

    [Test]
    public async Task StartRun_FreezeCommitGuardRejectsInsideTransactionWithoutInsertingRows()
    {
        var databasePath = GetDatabasePath("freeze-guard.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(CreateProject());
        var guard = new RejectingFreezeCommitGuard();

        var exception = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() =>
            store.StartRunAsync(CreateRun(project) with
            {
                FreezeCommitGuard = guard
            }));

        AssertEx.Equal("FreezeDependencyChanged", exception.Code);
        AssertEx.True(guard.WasCalled, "The dependency guard must execute before persistence mutation.");
        AssertEx.Equal(expected: 0, await store.CountRunsAsync(project.Id));
        AssertEx.Null(await store.ClaimNextAsync(), "A rejected freeze must not leave primary work behind.");
    }

    [Test]
    public async Task EncryptedFields_RawSqlContainsCiphertextAndFreshContextRoundTrips()
    {
        var databasePath = GetDatabasePath("encrypted.sqlite");
        var task = Encoding.UTF8.GetBytes("secret-core-task");
        var snapshot = Encoding.UTF8.GetBytes("secret-runtime-snapshot");
        var output = Encoding.UTF8.GetBytes("secret-output-parts");
        var judge = Encoding.UTF8.GetBytes("secret-judge-result");
        Guid projectId;
        Guid runId;
        Guid attemptId;
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new BenchmarkStore(context, TimeProvider.System);
            var project = await store.CreateProjectAsync(CreateProject() with
            {
                CoreTaskJson = task
            });
            var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyBytes, PolicyHash);
            project = AssertEx.NotNull(await store.GetProjectAsync(project.Id));
            var run = await store.StartRunAsync(CreateRun(project) with
            {
                RuntimeSnapshotJson = snapshot
            });
            var primary = AssertEx.NotNull(await store.ClaimNextAsync());
            var primaryDone = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(run.Id, primary.Run.Version, output, 1, 4096, 4, 1, 250,
                JudgeSeed(activation.Revision)));
            var judgeWork = AssertEx.NotNull(await store.ClaimNextAsync());
            attemptId = judgeWork.JudgeAttemptId!.Value;
            _ = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, judgeWork.Version, judge, 5, 61));
            projectId = project.Id;
            runId = run.Id;
            _ = primaryDone;
        }

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            AssertCiphertext(await ReadProjectCoreTaskAsync(connection, projectId), task);
            AssertCiphertext(await ReadRunRuntimeSnapshotAsync(connection, runId), snapshot);
            AssertCiphertext(await ReadRunOutputAsync(connection, runId), output);
            AssertCiphertext(await ReadAttemptResultAsync(connection, attemptId), judge);
        }

        await using var fresh = CreateContext(databasePath);
        var freshStore = new BenchmarkStore(fresh, TimeProvider.System);
        var reloadedProject = AssertEx.NotNull(await freshStore.GetProjectAsync(projectId));
        AssertBytes(task, reloadedProject.CoreTaskJson.Span);
        var reloaded = AssertEx.NotNull(await freshStore.GetRunAsync(runId));
        AssertBytes(snapshot, reloaded.RuntimeSnapshotJson.Span);
        AssertBytes(output, reloaded.OutputPartsJson!.Value.Span);
        var attempt = AssertEx.NotNull(await freshStore.GetJudgeAttemptAsync(attemptId));
        AssertBytes(judge, attempt.ResultJson!.Value.Span);
        AssertEx.Equal<int?>(61, AssertEx.NotNull(reloaded.Judge).Score, "The run's judge view reads the attempt's score.");
    }

    [Test]
    public async Task ListRuns_PagesInTheDatabaseAndReturnsNoEncryptedPayloads()
    {
        var databasePath = GetDatabasePath("list-runs-paging.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(CreateProject());
        var created = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            project = AssertEx.NotNull(await store.GetProjectAsync(project.Id));
            created.Add((await store.StartRunAsync(CreateRun(project) with
            {
                PrimaryLaunchIntent = new BenchmarkRunLaunchIntent("cuda", "q8_0", "auto", null, "on", "intended", "manifest-sha")
            })).Id);
        }

        var firstPage = await store.ListRunsAsync(project.Id, skip: 0, take: 2);
        var secondPage = await store.ListRunsAsync(project.Id, skip: 2, take: 2);
        var lastPage = await store.ListRunsAsync(project.Id, skip: 4, take: 2);

        AssertEx.Equal(expected: 5, firstPage.TotalCount, "TotalCount counts the project, not the page.");
        AssertEx.Equal(expected: 2, firstPage.Items.Count);
        AssertEx.Equal(expected: 1, lastPage.Items.Count);
        AssertEx.Equal(expected: 5, await store.CountRunsAsync(project.Id));
        AssertEx.Empty(firstPage.Items.Select(static run => run.Id).Intersect(secondPage.Items.Select(static run => run.Id)));

        var row = firstPage.Items[0];
        AssertEx.True(row.RuntimeSnapshotJson.IsEmpty, "The list path must not read the encrypted snapshot column.");
        AssertEx.Null(row.OutputPartsJson);
        // The summary still carries everything the list view renders — the flat columns, not the payloads.
        AssertEx.Equal("q8_0", AssertEx.NotNull(row.PrimaryLaunchIntent).KvCacheType);
        AssertEx.Equal("model.gguf", row.PrimaryModelName);
        AssertEx.Null(row.PrimaryLaunchEvidence, "A run that never launched carries no evidence block.");
    }

    [Test]
    public async Task StartRun_PersistsTheFreezeLaunchIntentForBothPhases()
    {
        var databasePath = GetDatabasePath("launch-intent.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(CreateProject());
        var run = await store.StartRunAsync(CreateRun(project) with
        {
            PrimaryLaunchIntent = new BenchmarkRunLaunchIntent("cuda", "q8_0", "explicit", null, "on", "intended-primary", "manifest-sha")
        });

        var reloaded = AssertEx.NotNull(await store.GetRunAsync(run.Id));
        var primary = AssertEx.NotNull(reloaded.PrimaryLaunchIntent);
        AssertEx.Equal("q8_0", primary.KvCacheType);
        AssertEx.Equal("explicit", primary.KvCacheTypeSource);
        AssertEx.Null(primary.KvAutoReason);
        AssertEx.Equal("intended-primary", primary.IntendedLaunchIdentity);
        AssertEx.Null(reloaded.PrimaryLaunchEvidence, "A run that has not launched carries no evidence.");
    }

    [Test]
    public async Task StartRun_WithoutAFreezeIntent_LeavesTheLegacyColumnsNull()
    {
        var databasePath = GetDatabasePath("launch-intent-legacy.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(CreateProject());
        var run = await store.StartRunAsync(CreateRun(project));

        var reloaded = AssertEx.NotNull(await store.GetRunAsync(run.Id));
        AssertEx.Null(reloaded.PrimaryLaunchIntent);
        AssertEx.Null(reloaded.PrimaryLaunchEvidence);
    }

    [Test]
    public async Task MarkPrimaryLaunchReady_WritesOnceEncryptsThePayloadsAndSurvivesTerminalizationAndRecovery()
    {
        var databasePath = GetDatabasePath("launch-ready.sqlite");
        Guid runId;
        var receipt = Encoding.UTF8.GetBytes("{\"receiptVersion\":1}");
        var environment = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}");
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new BenchmarkStore(context, TimeProvider.System);
            var project = await store.CreateProjectAsync(CreateProject());
            var run = await store.StartRunAsync(CreateRun(project));
            runId = run.Id;
            var work = AssertEx.NotNull(await store.ClaimNextAsync());
            var versionBefore = AssertEx.NotNull(await store.GetRunAsync(runId)).Version;

            AssertEx.True(await store.MarkPrimaryLaunchReadyAsync(runId, work.QueueSequence, work.Version, Receipt(receipt, environment)),
                "The first checkpoint on a running work item must be accepted.");
            AssertEx.False(await store.MarkPrimaryLaunchReadyAsync(runId, work.QueueSequence, work.Version, Receipt(receipt, environment, "other-hash")),
                "The checkpoint is insert-if-null: a second call must not overwrite it.");

            var afterCheckpoint = AssertEx.NotNull(await store.GetRunAsync(runId));
            AssertEx.Equal(versionBefore, afterCheckpoint.Version, "Recording evidence must not move the run version.");
            AssertEx.Equal(BenchmarkPrimaryStatus.Running, afterCheckpoint.PrimaryStatus);
            var evidence = AssertEx.NotNull(afterCheckpoint.PrimaryLaunchEvidence);
            AssertEx.Equal("receipt-hash", evidence.ReceiptHash);
            AssertEx.Equal("cuda", evidence.EffectiveBackend);
            AssertEx.Equal<int?>(33, evidence.PlacementOffloaded);
            AssertEx.Equal("exe-sha", evidence.ExecutableSha256);
            AssertEx.Equal<bool?>(true, evidence.HasAuxAssets);
            AssertBytes(receipt, evidence.ReceiptJson!.Value.Span);
            AssertBytes(environment, evidence.EnvironmentFactsJson!.Value.Span);

            _ = await store.MarkPrimaryFailedAsync(runId, work.Version, "spawn failed", 5);
            AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(runId)).PrimaryLaunchEvidence, "Terminalizing must not clear the evidence.");
        }

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            AssertCiphertext(await ReadRunLaunchReceiptAsync(connection, runId), receipt);
            AssertCiphertext(await ReadRunEnvironmentFactsAsync(connection, runId), environment);
        }

        await using var fresh = CreateContext(databasePath);
        var freshStore = new BenchmarkStore(fresh, TimeProvider.System);
        _ = await freshStore.RecoverRunsOnStartupAsync();
        var recovered = AssertEx.NotNull(await freshStore.GetRunAsync(runId));
        AssertBytes(receipt, AssertEx.NotNull(recovered.PrimaryLaunchEvidence).ReceiptJson!.Value.Span);
    }

    [Test]
    public async Task MarkPrimaryLaunchReady_RefusesRecoveredWorkAndAnotherRunsWorkItem()
    {
        var databasePath = GetDatabasePath("launch-ready-negatives.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(CreateProject());
        var first = await store.StartRunAsync(CreateRun(project));
        project = AssertEx.NotNull(await store.GetProjectAsync(project.Id));
        var second = await store.StartRunAsync(CreateRun(project));
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(first.Id, claimed.RunId);
        var receipt = Receipt(Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("{}"));

        // A restart failed this work item ("Interrupted by application restart."), which is neither Running nor a
        // cancellation successor — the process that comes back must not be able to backfill evidence onto it.
        _ = await store.RecoverRunsOnStartupAsync();
        AssertEx.False(await store.MarkPrimaryLaunchReadyAsync(claimed.RunId, claimed.QueueSequence, claimed.Version, receipt),
            "Recovered (Failed) work must refuse a launch checkpoint.");

        AssertEx.False(await store.MarkPrimaryLaunchReadyAsync(second.Id, claimed.QueueSequence, claimed.Version, receipt),
            "A work item belonging to another run must never carry this run's evidence.");
        AssertEx.Null(AssertEx.NotNull(await store.GetRunAsync(first.Id)).PrimaryLaunchEvidence);
        AssertEx.Null(AssertEx.NotNull(await store.GetRunAsync(second.Id)).PrimaryLaunchEvidence);
    }

    [Test]
    public async Task MarkJudgeLaunchReady_AcceptsTheCancellationSuccessorVersionAndRefusesEverythingElse()
    {
        var databasePath = GetDatabasePath("launch-ready-cas.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var judgeWork = await CreateRunningJudgeAsync(store);
        var receipt = Receipt(Encoding.UTF8.GetBytes("{}"), Encoding.UTF8.GetBytes("{}"));
        var run = AssertEx.NotNull(await store.GetRunAsync(judgeWork.RunId));

        var attemptId = judgeWork.JudgeAttemptId!.Value;

        AssertEx.False(await store.MarkJudgeLaunchReadyAsync(attemptId, judgeWork.QueueSequence + 100, judgeWork.Version, receipt, "cohort-key"),
            "A checkpoint naming a work item that does not exist must be refused.");
        AssertEx.False(await store.MarkJudgeLaunchReadyAsync(attemptId, judgeWork.QueueSequence, judgeWork.Version + 2, receipt, "cohort-key"),
            "Only the claimed version and its cancellation successor are accepted.");
        AssertEx.False(await store.MarkPrimaryLaunchReadyAsync(judgeWork.RunId, judgeWork.QueueSequence, judgeWork.Version, receipt),
            "A primary checkpoint must not land on the judge's work item.");
        AssertEx.Null(AssertEx.NotNull(await store.GetJudgeAttemptAsync(attemptId)).LaunchEvidence);

        // An operator cancellation terminalizes the judge work item, bumping its version by exactly one; the launch
        // that was already coming up must still be able to record what it did.
        _ = await store.CancelAsync(judgeWork.RunId, run.Version);
        AssertEx.True(await store.MarkJudgeLaunchReadyAsync(attemptId, judgeWork.QueueSequence, judgeWork.Version, receipt, "cohort-key"),
            "A cancelled work item at the successor version is the proven cancel-first ordering.");
        var persistedAttempt = AssertEx.NotNull(await store.GetJudgeAttemptAsync(attemptId));
        AssertEx.NotNull(persistedAttempt.LaunchEvidence);
        AssertEx.Equal("cohort-key", persistedAttempt.JudgeExecutionKey, "The cohort key is written in the same insert-if-null checkpoint.");
        AssertEx.Equal(BenchmarkRunJudgeStates.Cancelled, AssertEx.NotNull(AssertEx.NotNull(await store.GetRunAsync(judgeWork.RunId)).Judge).State);
    }

    [Test]
    public async Task EncryptedField_CiphertextSubstitutionFailsAad()
    {
        var databasePath = GetDatabasePath("aad.sqlite");
        Guid substitutedRunId;
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new BenchmarkStore(context, TimeProvider.System);
            var project = await store.CreateProjectAsync(CreateProject());
            var first = await store.StartRunAsync(CreateRun(project));
            project = AssertEx.NotNull(await store.GetProjectAsync(project.Id));
            var second = await store.StartRunAsync(CreateRun(project));
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE benchmark_runs SET runtime_snapshot_json = (SELECT runtime_snapshot_json FROM benchmark_runs WHERE id = {first.Id}) WHERE id = {second.Id}");
            substitutedRunId = second.Id;
        }

        await using var fresh = CreateContext(databasePath);
        _ = await AssertEx.ThrowsAsync<AuthenticationTagMismatchException>(() =>
            new BenchmarkStore(fresh, TimeProvider.System).GetRunAsync(substitutedRunId));
    }

    [Test]
    public async Task ModelOrigin_NullAndExactLowercaseValues_RoundTrip()
    {
        var databasePath = GetDatabasePath("model-origin-roundtrip.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(CreateProject());
        var withoutOrigin = await store.StartRunAsync(CreateRun(project) with
        {
            PrimaryModelOrigin = null
        });
        project = AssertEx.NotNull(await store.GetProjectAsync(project.Id));
        var huggingFace = await store.StartRunAsync(CreateRun(project) with
        {
            PrimaryModelOrigin = LocalModelOrigin.HuggingFace
        });

        AssertEx.Null(AssertEx.NotNull(await store.GetRunAsync(withoutOrigin.Id)).PrimaryModelOrigin);
        AssertEx.Equal(LocalModelOrigin.HuggingFace, AssertEx.NotNull(await store.GetRunAsync(huggingFace.Id)).PrimaryModelOrigin);
    }

    [Test]
    public async Task ModelOrigin_UnknownEnumValue_FailsClosed()
    {
        var databasePath = GetDatabasePath("model-origin-enum.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new BenchmarkStore(context, TimeProvider.System);
        var project = await store.CreateProjectAsync(CreateProject());

        var exception = await AssertEx.ThrowsAsync<DbUpdateException>(() =>
            store.StartRunAsync(CreateRun(project) with
            {
                PrimaryModelOrigin = (LocalModelOrigin)999
            }));

        AssertEx.True(exception.GetBaseException() is InvalidOperationException invalidOperation
                      && invalidOperation.Message == "Unknown benchmark model origin enum value.",
            "EF may wrap conversion failures, but the exact strict origin converter must remain the root cause.");
    }

    [Test]
    [Arguments("HuggingFace")]
    [Arguments("IMPORTED")]
    [Arguments("unknown")]
    public async Task ModelOrigin_UnknownOrCaseVariantDatabaseValue_FailsClosed(string persistedValue)
    {
        var databasePath = GetDatabasePath($"model-origin-db-{persistedValue}.sqlite");
        Guid runId;
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new BenchmarkStore(context, TimeProvider.System);
            var project = await store.CreateProjectAsync(CreateProject());
            runId = (await store.StartRunAsync(CreateRun(project))).Id;
            _ = await context.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
            _ = await context.Database.ExecuteSqlInterpolatedAsync($"UPDATE benchmark_runs SET primary_model_origin = {persistedValue} WHERE id = {runId}");
        }

        await using var fresh = CreateContext(databasePath);
        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => new BenchmarkStore(fresh, TimeProvider.System).GetRunAsync(runId));
    }

    private NodeChatDbContext CreateContext(string path) =>
        AgentDefinitionTestContextFactory.Create(path, _keyHolder);

    /// <summary>
    ///     A judge-enabled project with an active policy revision. Without one there is no revision to hang an attempt
    ///     off, and a judge work item cannot exist without an attempt.
    /// </summary>
    private static async Task<(BenchmarkProjectRecord Project, BenchmarkJudgePolicyRevisionRecord Revision)> CreateJudgeProjectAsync(BenchmarkStore store)
    {
        var project = await store.CreateProjectAsync(CreateProject());
        var activation = await store.ActivateJudgePolicyAsync(project.Id, project.Version, PolicyBytes, PolicyHash);
        return (AssertEx.NotNull(await store.GetProjectAsync(project.Id)), activation.Revision);
    }

    private static BenchmarkJudgeAttemptSeed JudgeSeed(BenchmarkJudgePolicyRevisionRecord revision) =>
        new(revision.Id, new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"judgeRuntime\":1}")));

    private static BenchmarkProjectInput CreateProject(Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "Benchmark", Encoding.UTF8.GetBytes("{\"task\":\"answer\"}"), 4096, Guid.NewGuid());

    private static BenchmarkStartRunCommand CreateRun(BenchmarkProjectRecord project) =>
        new(Guid.NewGuid(), project.Id, project.Version,
            Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"), "model.gguf", LocalModelOrigin.Imported, "v1:" + new string('a', 64), "Agent", 1, 4096);

    private static async Task<BenchmarkClaimedWork> CreateRunningJudgeAsync(BenchmarkStore store)
    {
        var (project, revision) = await CreateJudgeProjectAsync(store);
        var run = await store.StartRunAsync(CreateRun(project));
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(run.Id,
            primary.Run.Version,
            Encoding.UTF8.GetBytes("[]"),
            LastStreamSequence: 10,
            EffectiveContextTokens: 4096,
            DurationMs: 1,
            TotalTokens: null,
            TokensPerSecond: null,
            JudgeSeed(revision)));
        var judge = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(BenchmarkWorkKind.Judge, judge.Kind);
        AssertEx.True(judge.JudgeAttemptId is not null, "Claimed judge work must name the attempt it judges.");
        return judge;
    }

    private static BenchmarkLaunchReceiptCommand Receipt(byte[] receiptJson, byte[] environmentJson, string receiptHash = "receipt-hash") =>
        new(Encoding.UTF8.GetString(receiptJson),
            Encoding.UTF8.GetString(environmentJson),
            "environment-hash",
            receiptHash,
            "effective-identity",
            "cuda",
            33,
            33,
            "exe-sha",
            true,
            "auto");

    private static async Task<byte[]> ReadRunLaunchReceiptAsync(SqliteConnection connection, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT primary_launch_receipt_json FROM benchmark_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static async Task<byte[]> ReadRunEnvironmentFactsAsync(SqliteConnection connection, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT primary_environment_facts_json FROM benchmark_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static async Task<byte[]> ReadProjectCoreTaskAsync(SqliteConnection connection, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT core_task_json FROM benchmark_projects WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static async Task<byte[]> ReadRunRuntimeSnapshotAsync(SqliteConnection connection, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT runtime_snapshot_json FROM benchmark_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static async Task<byte[]> ReadRunOutputAsync(SqliteConnection connection, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT output_parts_json FROM benchmark_runs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static async Task<byte[]> ReadAttemptResultAsync(SqliteConnection connection, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_json FROM benchmark_judge_attempts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private static void AssertCiphertext(byte[] actual, byte[] plaintext)
    {
        AssertEx.False(actual.AsSpan().SequenceEqual(plaintext), "Raw SQL must return ciphertext.");
        AssertEx.False(Encoding.UTF8.GetString(actual).Contains(Encoding.UTF8.GetString(plaintext), StringComparison.Ordinal), "Ciphertext must not contain plaintext fragments.");
    }

    private static void AssertBytes(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        AssertEx.True(actual.SequenceEqual(expected), "Byte payload should round-trip exactly.");

    private static async Task<(bool Won, BenchmarkConflictException? Conflict)> RaceAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return (true, null);
        }
        catch (BenchmarkConflictException exception)
        {
            return (false, exception);
        }
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private sealed class RejectingFreezeCommitGuard : IBenchmarkFreezeCommitGuard
    {
        public bool WasCalled { get; private set; }

        public Task<bool> IsCurrentAsync(CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(false);
        }
    }
}
