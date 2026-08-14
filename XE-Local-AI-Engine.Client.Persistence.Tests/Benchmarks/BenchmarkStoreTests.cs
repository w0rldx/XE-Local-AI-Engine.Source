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

        var projectB = await store.CreateProjectAsync(CreateProject() with
        {
            JudgeEnabled = true,
            JudgeModelName = "judge.gguf",
            JudgeContextTokens = 2048
        });
        var judgeRun = await store.StartRunAsync(CreateRun(projectB) with
        {
            JudgeEnabled = true
        });
        var judgePrimaryClaim = AssertEx.NotNull(await store.ClaimNextAsync());
        var primarySucceeded = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(judgeRun.Id, judgePrimaryClaim.Run.Version,
            Encoding.UTF8.GetBytes("[]"), 1, 4096, 10, null, null));
        var judgeClaim = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(BenchmarkWorkKind.Judge, judgeClaim.Kind);
        var queuedProject = await store.CreateProjectAsync(CreateProject());
        var queuedRun = await store.StartRunAsync(CreateRun(queuedProject));

        var recovered = await store.RecoverRunsOnStartupAsync();
        AssertEx.Equal(expected: 2, recovered.Count);
        var primaryAfter = AssertEx.NotNull(await store.GetRunAsync(primaryRun.Id));
        AssertEx.Equal(BenchmarkPrimaryStatus.Failed, primaryAfter.PrimaryStatus);
        AssertEx.Equal(BenchmarkJudgeStatus.Disabled, primaryAfter.JudgeStatus);
        AssertEx.Equal(primaryRun.LastStreamSequence + 1, primaryAfter.LastStreamSequence);
        var judgeAfter = AssertEx.NotNull(await store.GetRunAsync(judgeRun.Id));
        AssertEx.Equal(BenchmarkPrimaryStatus.Succeeded, judgeAfter.PrimaryStatus);
        AssertEx.Equal(BenchmarkJudgeStatus.Failed, judgeAfter.JudgeStatus);
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
        var project = await store.CreateProjectAsync(CreateProject() with
        {
            JudgeEnabled = true,
            JudgeModelName = "judge.gguf",
            JudgeContextTokens = 2048
        });
        var run = await store.StartRunAsync(CreateRun(project) with
        {
            JudgeEnabled = true
        });
        var claim = AssertEx.NotNull(await store.ClaimNextAsync());
        var requested = await store.CancelAsync(run.Id, claim.Run.Version);

        var recovered = await store.RecoverRunsOnStartupAsync();

        AssertEx.Equal(1, recovered.Count);
        var after = AssertEx.NotNull(await store.GetRunAsync(run.Id));
        AssertEx.Equal(BenchmarkPrimaryStatus.Cancelled, after.PrimaryStatus);
        AssertEx.Equal(BenchmarkJudgeStatus.Skipped, after.JudgeStatus);
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
        var project = await store.CreateProjectAsync(CreateProject() with
        {
            JudgeEnabled = true,
            JudgeModelName = "judge.gguf",
            JudgeContextTokens = 2048
        });
        var run = await store.StartRunAsync(CreateRun(project) with
        {
            JudgeEnabled = true
        });

        var cancelled = await store.CancelAsync(run.Id, run.Version);

        AssertEx.Equal(BenchmarkPrimaryStatus.Cancelled, cancelled.PrimaryStatus);
        AssertEx.Equal(BenchmarkJudgeStatus.Skipped, cancelled.JudgeStatus);
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

        AssertEx.Equal(BenchmarkJudgeStatus.Cancelled, repeated.JudgeStatus);
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
        AssertEx.Equal(BenchmarkJudgeStatus.Succeeded, success.JudgeStatus);
        AssertEx.Equal(BenchmarkJudgeStatus.Failed, failure.JudgeStatus);
        AssertEx.Equal(BenchmarkJudgeStatus.Cancelled, cancelled.JudgeStatus);
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
        AssertEx.Equal(BenchmarkJudgeStatus.Succeeded, completed.JudgeStatus);
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
        var project = await store.CreateProjectAsync(CreateProject() with
        {
            JudgeEnabled = true,
            JudgeModelName = "judge.gguf",
            JudgeContextTokens = 2048
        });
        var run = await store.StartRunAsync(CreateRun(project) with
        {
            JudgeEnabled = true
        });
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
        AssertEx.Equal(BenchmarkJudgeStatus.Skipped, completed.JudgeStatus);
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
        AssertEx.Equal(expected: 0, (await store.ListRunsAsync(project.Id)).Count);
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
        await using (var context = CreateContext(databasePath))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new BenchmarkStore(context, TimeProvider.System);
            var project = await store.CreateProjectAsync(CreateProject() with
            {
                CoreTaskJson = task,
                JudgeEnabled = true,
                JudgeModelName = "judge.gguf",
                JudgeContextTokens = 2048
            });
            var run = await store.StartRunAsync(CreateRun(project) with
            {
                RuntimeSnapshotJson = snapshot,
                JudgeEnabled = true
            });
            var primary = AssertEx.NotNull(await store.ClaimNextAsync());
            var primaryDone = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(run.Id, primary.Run.Version, output, 1, 4096, 4, 1, 250));
            var judgeWork = AssertEx.NotNull(await store.ClaimNextAsync());
            _ = await store.MarkJudgeSucceededAsync(new BenchmarkJudgeSuccessCommand(run.Id, judgeWork.Version, judge));
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
            AssertCiphertext(await ReadRunJudgeResultAsync(connection, runId), judge);
        }

        await using var fresh = CreateContext(databasePath);
        var freshStore = new BenchmarkStore(fresh, TimeProvider.System);
        var reloadedProject = AssertEx.NotNull(await freshStore.GetProjectAsync(projectId));
        AssertBytes(task, reloadedProject.CoreTaskJson.Span);
        var reloaded = AssertEx.NotNull(await freshStore.GetRunAsync(runId));
        AssertBytes(snapshot, reloaded.RuntimeSnapshotJson.Span);
        AssertBytes(output, reloaded.OutputPartsJson!.Value.Span);
        AssertBytes(judge, reloaded.JudgeResultJson!.Value.Span);
    }

    [Test]
    public async Task EncryptedField_CiphertextSubstitutionFailsAad()
    {
        var databasePath = GetDatabasePath("aad.sqlite");
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
        }

        await using var fresh = CreateContext(databasePath);
        _ = await AssertEx.ThrowsAsync<AuthenticationTagMismatchException>(() =>
            new BenchmarkStore(fresh, TimeProvider.System).ListRunsAsync(fresh.BenchmarkProjects.Select(static item => item.Id).Single()));
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

    private static BenchmarkProjectInput CreateProject(Guid? id = null) =>
        new(id ?? Guid.NewGuid(), "Benchmark", Encoding.UTF8.GetBytes("{\"task\":\"answer\"}"),
            4096, Guid.NewGuid(), false, null, null);

    private static BenchmarkStartRunCommand CreateRun(BenchmarkProjectRecord project) =>
        new(Guid.NewGuid(), project.Id, project.Version,
            Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"), "model.gguf", LocalModelOrigin.Imported, "v1:" + new string('a', 64), "Agent", 1, 4096, project.JudgeEnabled);

    private static async Task<BenchmarkClaimedWork> CreateRunningJudgeAsync(BenchmarkStore store)
    {
        var project = await store.CreateProjectAsync(CreateProject() with
        {
            JudgeEnabled = true,
            JudgeModelName = "judge.gguf",
            JudgeContextTokens = 2048
        });
        var run = await store.StartRunAsync(CreateRun(project) with
        {
            JudgeEnabled = true
        });
        var primary = AssertEx.NotNull(await store.ClaimNextAsync());
        _ = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand(run.Id,
            primary.Run.Version,
            Encoding.UTF8.GetBytes("[]"),
            LastStreamSequence: 10,
            EffectiveContextTokens: 4096,
            DurationMs: 1,
            TotalTokens: null,
            TokensPerSecond: null));
        var judge = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(BenchmarkWorkKind.Judge, judge.Kind);
        return judge;
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

    private static async Task<byte[]> ReadRunJudgeResultAsync(SqliteConnection connection, Guid id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT judge_result_json FROM benchmark_runs WHERE id = $id;";
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
