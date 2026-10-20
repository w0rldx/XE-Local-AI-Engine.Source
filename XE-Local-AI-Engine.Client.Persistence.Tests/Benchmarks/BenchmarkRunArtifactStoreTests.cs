namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Artifact rows are evidence of one judging: keyed to the attempt, one per (criterion, kind), and deleted with the
///     run strictly before the attempt they name — foreign keys are on, so the order is enforced, not hoped for.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class BenchmarkRunArtifactStoreTests : IDisposable
{
    private const string PolicyHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task AddRunArtifacts_WhenDuplicateKind_Rejects()
    {
        await using var context = await CreateContextAsync("artifact-duplicate.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgeProjectAsync(store);
        var judged = await StartJudgedRunAsync(store, project.Id);
        var run = judged.Run;
        var attemptId = judged.AttemptId;
        _ = await store.AddRunArtifactsAsync(run.Id, attemptId, [Artifact("c1", BenchmarkRunArtifactKinds.PythonTestsStdout)]);

        _ = await AssertEx.ThrowsAsync<BenchmarkConflictException>(() =>
                store.AddRunArtifactsAsync(run.Id, attemptId, [Artifact("c1", BenchmarkRunArtifactKinds.PythonTestsStdout)]),
            "A second stdout for the same criterion of the same judging is refused.");

        // The same kind for another criterion, and the other kind for the same criterion, are distinct evidence.
        _ = await store.AddRunArtifactsAsync(run.Id, attemptId,
            [Artifact("c2", BenchmarkRunArtifactKinds.PythonTestsStdout), Artifact("c1", BenchmarkRunArtifactKinds.PythonTestsStderr)]);
        AssertEx.Equal(expected: 3, (await store.GetRunArtifactsAsync(run.Id)).Count);
    }

    [Test]
    public async Task AddRunArtifacts_WhenCriterionIdMissing_Rejects()
    {
        await using var context = await CreateContextAsync("artifact-no-criterion.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgeProjectAsync(store);
        var judged = await StartJudgedRunAsync(store, project.Id);

        // criterion_id sits inside the unique index, where SQLite would let NULLs repeat: the store refuses before the schema has to.
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            store.AddRunArtifactsAsync(judged.Run.Id, judged.AttemptId, [Artifact("c1", BenchmarkRunArtifactKinds.PythonTestsStdout) with { CriterionId = null }]));
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            store.AddRunArtifactsAsync(judged.Run.Id, judged.AttemptId, [Artifact(" ", BenchmarkRunArtifactKinds.PythonTestsStdout)]));
        AssertEx.Equal(expected: 0, await context.BenchmarkRunArtifacts.CountAsync(), "A refused batch writes nothing.");
    }

    [Test]
    public async Task AddRunArtifacts_WhenAttemptBelongsToAnotherRun_IsNotFound()
    {
        await using var context = await CreateContextAsync("artifact-foreign-attempt.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgeProjectAsync(store);
        var first = (await StartJudgedRunAsync(store, project.Id)).Run;
        var otherAttemptId = (await StartJudgedRunAsync(store, project.Id)).AttemptId;

        _ = await AssertEx.ThrowsAsync<BenchmarkNotFoundException>(() =>
            store.AddRunArtifactsAsync(first.Id, otherAttemptId, [Artifact("c1", BenchmarkRunArtifactKinds.PythonTestsStdout)]));
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() =>
            store.AddRunArtifactsAsync(first.Id, otherAttemptId, [Artifact("c1", "screenshot")]), "The kind vocabulary is closed.");
        AssertEx.Equal(expected: 0, await context.BenchmarkRunArtifacts.CountAsync());
    }

    [Test]
    public async Task DeleteRun_DeletesArtifactsBeforeJudgeAttempts()
    {
        await using var context = await CreateContextAsync("artifact-delete-run.sqlite");
        var store = NewStore(context);
        AssertEx.Equal(expected: 1L, await ForeignKeysAsync(context), "The order is only proven with foreign keys enforced.");
        var project = await CreateJudgeProjectAsync(store);
        var judged = await StartJudgedRunAsync(store, project.Id);
        var run = judged.Run;
        var attemptId = judged.AttemptId;
        _ = await store.AddRunArtifactsAsync(run.Id, attemptId,
            [Artifact("c1", BenchmarkRunArtifactKinds.PythonTestsStdout), Artifact("c1", BenchmarkRunArtifactKinds.PythonTestsStderr)]);

        await store.DeleteRunAsync(run.Id, run.Version);

        AssertEx.Equal(expected: 0, await context.BenchmarkRunArtifacts.CountAsync(), "The artifacts go with their run.");
        AssertEx.Equal(expected: 0, await context.BenchmarkJudgeAttempts.CountAsync(), "And so does the attempt they named.");
    }

    [Test]
    public async Task DeleteProject_DeletesEveryRunsArtifacts()
    {
        await using var context = await CreateContextAsync("artifact-delete-project.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgeProjectAsync(store);
        foreach (var round in Enumerable.Range(0, 2))
        {
            var judged = await StartJudgedRunAsync(store, project.Id);
            var run = judged.Run;
            var attemptId = judged.AttemptId;
            _ = await store.AddRunArtifactsAsync(run.Id, attemptId, [Artifact("c1", BenchmarkRunArtifactKinds.PythonTestsStdout)]);
        }

        AssertEx.Equal(expected: 2, await context.BenchmarkRunArtifacts.CountAsync());
        await store.DeleteProjectAsync(project.Id, AssertEx.NotNull(await store.GetProjectAsync(project.Id)).Version);

        AssertEx.Equal(expected: 0, await context.BenchmarkRunArtifacts.CountAsync(), "The project cascade runs the same per-run delete.");
        AssertEx.Null(await store.GetProjectAsync(project.Id));
    }

    [Test]
    public async Task GetRun_CarriesArtifactMetadata()
    {
        await using var context = await CreateContextAsync("artifact-get-run.sqlite");
        var store = NewStore(context);
        var project = await CreateJudgeProjectAsync(store);
        var judged = await StartJudgedRunAsync(store, project.Id);
        var run = judged.Run;
        var attemptId = judged.AttemptId;
        var stdout = Artifact("c1", BenchmarkRunArtifactKinds.PythonTestsStdout);
        _ = await store.AddRunArtifactsAsync(run.Id, attemptId, [stdout]);

        var readBack = AssertEx.NotNull(await store.GetRunAsync(run.Id));

        var artifact = readBack.Artifacts.Single();
        AssertEx.Equal(stdout.Id, artifact.Id, "The row id is the blob id.");
        AssertEx.Equal(run.Id, artifact.RunId);
        AssertEx.Equal(attemptId, artifact.JudgeAttemptId);
        AssertEx.Equal("c1", artifact.CriterionId);
        AssertEx.Equal(BenchmarkRunArtifactKinds.PythonTestsStdout, artifact.Kind);
        AssertEx.Equal("text/plain; charset=utf-8", artifact.MediaType);
        AssertEx.Equal(expected: 42L, artifact.ByteCount);
        AssertEx.Equal(stdout.ContentHash, artifact.ContentHash);
        AssertEx.Equal(expected: 0, AssertEx.NotNull(await store.GetRunAsync((await StartJudgedRunAsync(store, project.Id)).Run.Id)).Artifacts.Count,
            "A run without artifacts reads an empty list, never another run's.");
    }

    private static BenchmarkRunArtifactCommand Artifact(string criterionId, string kind) =>
        new()
        {
            Id = Guid.NewGuid(),
            CriterionId = criterionId,
            Kind = kind,
            MediaType = "text/plain; charset=utf-8",
            ByteCount = 42,
            ContentHash = new string('f', 64)
        };

    private static async Task<long> ForeignKeysAsync(NodeChatDbContext context)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<BenchmarkProjectRecord> CreateJudgeProjectAsync(BenchmarkStore store)
    {
        var project = await store.CreateProjectAsync(new BenchmarkProjectInput
        {
            Id = Guid.NewGuid(),
            Name = "Benchmark",
            CoreTaskJson = Encoding.UTF8.GetBytes("""{"task":"answer"}"""),
            ContextTokens = 4096,
            AgentDefinitionId = Guid.NewGuid()
        });
        _ = await store.ActivateJudgePolicyAsync(project.Id, project.Version, Encoding.UTF8.GetBytes("""{"rubric":"v1"}"""), PolicyHash);
        return AssertEx.NotNull(await store.GetProjectAsync(project.Id));
    }

    /// <summary>A succeeded run whose success seeded a judge attempt — the attempt an artifact must name — with that judging cancelled.</summary>
    private static async Task<JudgedRun> StartJudgedRunAsync(BenchmarkStore store, Guid projectId)
    {
        var project = AssertEx.NotNull(await store.GetProjectAsync(projectId));
        var run = await store.StartRunAsync(new BenchmarkStartRunCommand
        {
            RunId = Guid.NewGuid(),
            ProjectId = project.Id,
            ExpectedProjectVersion = project.Version,
            RuntimeSnapshotJson = Encoding.UTF8.GetBytes("{\"schemaVersion\":1}"),
            PrimaryModelName = "model.gguf",
            PrimaryModelOrigin = LocalModelOrigin.Imported,
            ModelContentFingerprint = "v1:" + new string('a', 64),
            AgentName = "Agent",
            AgentVersion = 1,
            RequestedContextTokens = 4096
        });
        var claimed = AssertEx.NotNull(await store.ClaimNextAsync());
        AssertEx.Equal(run.Id, claimed.RunId);
        var succeeded = await store.MarkPrimarySucceededAsync(new BenchmarkPrimarySuccessCommand
        {
            RunId = run.Id,
            ExpectedWorkVersion = claimed.Run.Version,
            OutputPartsJson = Encoding.UTF8.GetBytes("[]"),
            LastStreamSequence = 1,
            EffectiveContextTokens = 4096,
            DurationMs = 1,
            TotalTokens = null,
            TokensPerSecond = null,
            JudgeAttempt = new BenchmarkJudgeAttemptSeed
            {
                ExpectedJudgePolicyRevisionId = project.CurrentJudgePolicyRevisionId,
                RuntimeJson = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{\"judgeRuntime\":1}"))
            }
        });
        var attemptId = AssertEx.NotNull(succeeded.Judge).AttemptId ?? throw new AssertionException("Primary success seeds the judge attempt.");

        // Cancelling the judging terminalizes the attempt: it stays as the row artifacts name, the queue moves on to the next primary, and the run is deletable.
        return new JudgedRun
        {
            Run = await store.CancelAsync(run.Id, succeeded.Version),
            AttemptId = attemptId
        };
    }

    private static BenchmarkStore NewStore(NodeChatDbContext context) =>
        new(context, TimeProvider.System, NullLogger<BenchmarkStore>.Instance);

    private async Task<NodeChatDbContext> CreateContextAsync(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();
        return context;
    }

    private sealed record JudgedRun
    {
        public required BenchmarkRunRecord Run { get; init; }

        public required Guid AttemptId { get; init; }
    }
}
