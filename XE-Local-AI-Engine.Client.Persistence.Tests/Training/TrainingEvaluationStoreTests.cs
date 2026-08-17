namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class TrainingEvaluationStoreTests : IDisposable
{
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
    public async Task CreateEvaluation_EnqueuesOneWorkItem_AndLeavesTheBlobsAbsentUntilScored()
    {
        await using var context = await CreateDatabaseAsync("create.sqlite");
        var fixture = await SeedAsync(context);
        var store = new TrainingEvaluationStore(context, TimeProvider.System);

        var evaluation = await store.CreateAndEnqueueAsync(Command(fixture));

        AssertEx.Equal(TrainingEvaluationStatus.Queued, evaluation.Status);
        AssertEx.Equal(TrainingWorkStatus.Queued, evaluation.WorkStatus!.Value);
        AssertEx.Equal(expected: 3, evaluation.TotalCount);
        AssertEx.Equal(expected: 0, evaluation.ScoredCount);
        AssertEx.Equal(expected: 1,
            await context.TrainingWorkItems.CountAsync(item => item.TargetId == evaluation.Id && item.Kind == TrainingWorkKind.EvaluationRun));

        // An absent blob column must read as an absent ReadOnlyMemory, not an empty one — ReadOnlyMemory<byte>
        // converts implicitly from byte[], so a null array silently becomes HasValue == true with Length == 0.
        AssertEx.Null(evaluation.ResultsJson, "An evaluation that has scored nothing must report absent results, not empty ones.");
        AssertEx.Null(evaluation.PerKindJson, "The per-kind tally is absent until the first verdict lands.");
        AssertEx.Null(evaluation.ComparisonId, "A fresh evaluation is unbound.");
    }

    [Test]
    public async Task CreateEvaluation_WhenAnyPreconditionFails_InsertsNothing()
    {
        var databasePath = GetDatabasePath("atomic.sqlite");
        await using (var context = await CreateDatabaseAsync(databasePath))
        {
            var fixture = await SeedAsync(context);
            var store = new TrainingEvaluationStore(context, TimeProvider.System);

            _ = await AssertEx.ThrowsAsync<TrainingValidationException>(() => store.CreateAndEnqueueAsync(Command(fixture) with
            {
                MembershipJson = ReadOnlyMemory<byte>.Empty
            }));
            _ = await AssertEx.ThrowsAsync<TrainingValidationException>(() => store.CreateAndEnqueueAsync(Command(fixture) with
            {
                TotalCount = 0
            }));
            _ = await AssertEx.ThrowsAsync<TrainingValidationException>(() => store.CreateAndEnqueueAsync(Command(fixture) with
            {
                ModelName = "  "
            }));
            _ = await AssertEx.ThrowsAsync<TrainingNotFoundException>(() => store.CreateAndEnqueueAsync(Command(fixture) with
            {
                DatasetId = Guid.NewGuid()
            }));
            _ = await AssertEx.ThrowsAsync<TrainingNotFoundException>(() => store.CreateAndEnqueueAsync(Command(fixture) with
            {
                TrainingRunId = Guid.NewGuid()
            }));
        }

        await using var verify = CreateContext(databasePath);
        AssertEx.Equal(expected: 0, await verify.TrainingEvaluationRuns.CountAsync(), "A refused creation must leave no evaluation behind.");
        AssertEx.Equal(expected: 0,
            await verify.TrainingWorkItems.CountAsync(item => item.Kind == TrainingWorkKind.EvaluationRun),
            "A refused creation must leave no work item behind.");
    }

    [Test]
    public async Task AppendResults_IsIdempotentBySampleId_AndRecomputesEveryAggregate()
    {
        await using var context = await CreateDatabaseAsync("append.sqlite");
        var fixture = await SeedAsync(context);
        var store = new TrainingEvaluationStore(context, TimeProvider.System);
        var evaluation = await store.CreateAndEnqueueAsync(Command(fixture));

        var first = await store.AppendResultsAsync(evaluation.Id,
        [
            new TrainingEvaluationResultEntry(fixture.SampleIds[0], "tool-call", Passed: true, "deterministic"),
            new TrainingEvaluationResultEntry(fixture.SampleIds[1], "no-tool", Passed: false, "deterministic", "The model called a tool.")
        ]);
        AssertEx.Equal(expected: 2, first.ScoredCount);
        AssertEx.Equal(expected: 1, first.PassedCount);

        // The same verdicts offered again — what a resume that re-walked the prefix would do — must change nothing.
        var replayed = await store.AppendResultsAsync(evaluation.Id,
        [
            new TrainingEvaluationResultEntry(fixture.SampleIds[0], "tool-call", Passed: false, "deterministic", "a contradicting re-score"),
            new TrainingEvaluationResultEntry(fixture.SampleIds[1], "no-tool", Passed: true, "deterministic")
        ]);
        AssertEx.Equal(expected: 2, replayed.ScoredCount, "A re-append of already-scored samples must be a no-op.");
        AssertEx.Equal(expected: 1, replayed.PassedCount, "The first verdict for a sample is the one that stands.");

        var tally = TrainingEvaluationResults.ReadTally(replayed.PerKindJson);
        AssertEx.Equal(expected: 1, tally["tool-call"].Passed);
        AssertEx.Equal(expected: 0, tally["no-tool"].Passed);

        // Telemetry-shaped: the append deliberately leaves the concurrency token alone so the executor's expected
        // version survives the whole scoring loop.
        AssertEx.Equal(evaluation.Version, replayed.Version);
    }

    [Test]
    public async Task InterruptedEvaluation_RecoversAsFailed_AndResumesAtTheNextUnscoredSample()
    {
        var databasePath = GetDatabasePath("resume.sqlite");
        Guid evaluationId;
        Guid[] sampleIds;
        await using (var setup = await CreateDatabaseAsync(databasePath))
        {
            var fixture = await SeedAsync(setup);
            sampleIds = [.. fixture.SampleIds];
            var store = new TrainingEvaluationStore(setup, TimeProvider.System);
            var runStore = new TrainingRunStore(setup, TimeProvider.System);
            var evaluation = await store.CreateAndEnqueueAsync(Command(fixture));
            evaluationId = evaluation.Id;

            AssertEx.Equal(TrainingWorkKind.EvaluationRun, (await runStore.PeekNextKindAsync())!.Value);
            var claimed = AssertEx.NotNull(await runStore.ClaimNextAsync(TrainingWorkKind.EvaluationRun), "The evaluation should be claimable.");
            AssertEx.Equal(evaluationId, claimed.TargetId);
            AssertEx.Null(claimed.Run, "An evaluation claim carries no run — its target lives in another table.");

            var running = await store.TransitionAsync(evaluationId, evaluation.Version, TrainingEvaluationStatus.Running);
            _ = await store.AppendResultsAsync(evaluationId,
                [new TrainingEvaluationResultEntry(sampleIds[0], "tool-call", Passed: true, "deterministic")]);
            AssertEx.Equal(TrainingEvaluationStatus.Running, running.Status);
        }

        // A fresh process finds the interrupted Running work item. The frozen queue semantics fail it rather than
        // retrying it in place — but the scored prefix survives, which is the whole point of resume.
        await using (var recovery = CreateContext(databasePath))
        {
            _ = await new TrainingRunStore(recovery, TimeProvider.System).RecoverOnStartupAsync();
        }

        await using var after = CreateContext(databasePath);
        var afterStore = new TrainingEvaluationStore(after, TimeProvider.System);
        var failed = AssertEx.NotNull(await afterStore.GetAsync(evaluationId));
        AssertEx.Equal(TrainingEvaluationStatus.Failed, failed.Status);
        AssertEx.Equal(TrainingWorkStatus.Failed, failed.WorkStatus!.Value);
        AssertEx.Equal(expected: 1, failed.ScoredCount, "Recovery must not discard what the interrupted attempt already scored.");

        var resumed = await afterStore.ResumeAsync(evaluationId, failed.Version);
        AssertEx.Equal(TrainingEvaluationStatus.Queued, resumed.Status);
        AssertEx.Equal(TrainingWorkStatus.Queued, resumed.WorkStatus!.Value);
        AssertEx.Equal(expected: 1, resumed.ScoredCount, "Resume keeps the cursor; it does not restart the hold-out set.");
        AssertEx.Null(resumed.ErrorMessage, "A resumed evaluation is no longer carrying the interruption's message.");

        // The cursor IS the persisted verdict set: the first sample is scored, the rest are not.
        var scored = TrainingEvaluationResults.Read(resumed.ResultsJson).Select(entry => entry.SampleId).ToHashSet();
        AssertEx.True(scored.Contains(sampleIds[0]), "The already-scored sample stays scored.");
        AssertEx.False(scored.Contains(sampleIds[1]), "The next unscored sample is where a resumed run picks up.");

        AssertEx.Equal(expected: 1,
            await after.TrainingWorkItems.CountAsync(item => item.TargetId == evaluationId),
            "Resume replaces the terminal work item rather than adding a second one for the same target.");
    }

    [Test]
    public async Task Resume_IsRefusedWhileInFlight_AndOnceEverySampleIsScored()
    {
        await using var context = await CreateDatabaseAsync("resume-guards.sqlite");
        var fixture = await SeedAsync(context);
        var store = new TrainingEvaluationStore(context, TimeProvider.System);
        var evaluation = await store.CreateAndEnqueueAsync(Command(fixture));

        var queued = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.ResumeAsync(evaluation.Id, evaluation.Version));
        AssertEx.Equal("EvaluationActive", queued.Code);

        _ = await store.AppendResultsAsync(evaluation.Id,
            fixture.SampleIds.Select(id => new TrainingEvaluationResultEntry(id, "tool-call", Passed: true, "deterministic")).ToArray());
        var done = await store.CompleteAsync(evaluation.Id, TrainingWorkStatus.Succeeded, errorMessage: null);
        AssertEx.Equal(TrainingEvaluationStatus.Succeeded, done.Status);

        var complete = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.ResumeAsync(evaluation.Id, done.Version));
        AssertEx.Equal("EvaluationComplete", complete.Code);

        // Terminalizing an already-terminal work item is a silent no-op, so a startup retrace cannot double-transition.
        var again = await store.CompleteAsync(evaluation.Id, TrainingWorkStatus.Failed, "late");
        AssertEx.Equal(TrainingEvaluationStatus.Succeeded, again.Status);
    }

    [Test]
    public async Task Comparison_BindsBothEvaluations_AndUnbindsThemOnDelete()
    {
        await using var context = await CreateDatabaseAsync("comparison.sqlite");
        var fixture = await SeedAsync(context);
        var store = new TrainingEvaluationStore(context, TimeProvider.System);
        var baseEvaluation = await Scored(store, fixture, "base-model");
        var tunedEvaluation = await Scored(store, fixture, "tuned-model");

        _ = await AssertEx.ThrowsAsync<TrainingValidationException>(() => store.CreateComparisonAsync(Report(baseEvaluation.Id, baseEvaluation.Id)),
            "A comparison needs two distinct evaluations.");
        _ = await AssertEx.ThrowsAsync<TrainingValidationException>(() => store.CreateComparisonAsync(Report(baseEvaluation.Id, tunedEvaluation.Id) with
        {
            Name = " "
        }));
        _ = await AssertEx.ThrowsAsync<TrainingValidationException>(() => store.CreateComparisonAsync(Report(baseEvaluation.Id, tunedEvaluation.Id) with
        {
            DeltasJson = ReadOnlyMemory<byte>.Empty
        }));

        var report = await store.CreateComparisonAsync(Report(baseEvaluation.Id, tunedEvaluation.Id));
        var bound = AssertEx.NotNull(await store.GetAsync(baseEvaluation.Id));
        AssertEx.Equal(report.Id, bound.ComparisonId!.Value);

        // A bound evaluation cannot be deleted: the report's deltas would stop being reproducible from storage.
        var referenced = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.DeleteAsync(bound.Id, bound.Version));
        AssertEx.Equal("EvaluationBound", referenced.Code);

        // A second report cannot claim an already-bound evaluation either.
        var reused = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.CreateComparisonAsync(Report(baseEvaluation.Id, tunedEvaluation.Id)));
        AssertEx.Equal("EvaluationBound", reused.Code);

        await store.DeleteComparisonAsync(report.Id, report.Version);
        var unbound = AssertEx.NotNull(await store.GetAsync(baseEvaluation.Id));
        AssertEx.Null(unbound.ComparisonId, "Deleting the report unbinds its evaluations, or they would be undeletable forever.");
        AssertEx.Empty(await store.ListComparisonsAsync());

        await store.DeleteAsync(unbound.Id, unbound.Version);
        AssertEx.Equal(expected: 0, await context.TrainingEvaluationRuns.CountAsync(item => item.Id == unbound.Id));
        AssertEx.Equal(expected: 0, await context.TrainingWorkItems.CountAsync(item => item.TargetId == unbound.Id));
    }

    [Test]
    public async Task EvaluationDelete_IsRefusedWhileTheWorkItemIsNonTerminal()
    {
        await using var context = await CreateDatabaseAsync("delete.sqlite");
        var fixture = await SeedAsync(context);
        var store = new TrainingEvaluationStore(context, TimeProvider.System);
        var evaluation = await store.CreateAndEnqueueAsync(Command(fixture));

        var active = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.DeleteAsync(evaluation.Id, evaluation.Version));
        AssertEx.Equal("EvaluationActive", active.Code);

        var stale = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.DeleteAsync(evaluation.Id, evaluation.Version + 1));
        AssertEx.Equal("VersionConflict", stale.Code);
    }

    [Test]
    public async Task RunDelete_IsRefusedWhileAnEvaluationBorrowedItsFreeze()
    {
        await using var context = await CreateDatabaseAsync("run-delete.sqlite");
        var fixture = await SeedAsync(context);
        var evaluations = new TrainingEvaluationStore(context, TimeProvider.System);
        var runs = new TrainingRunStore(context, TimeProvider.System);
        _ = await evaluations.CreateAndEnqueueAsync(Command(fixture));

        var run = AssertEx.NotNull(await runs.GetAsync(fixture.TrainingRunId));
        var done = await runs.CompleteRunAsync(run.Id, TrainingWorkStatus.Succeeded, errorMessage: null);

        // Nothing cascades on this connection, so an unguarded run delete would leave the evaluation describing a
        // freeze nothing can point at.
        var evaluated = await AssertEx.ThrowsAsync<TrainingConflictException>(() => runs.DeleteAsync(run.Id, done.Version));
        AssertEx.Equal("RunEvaluated", evaluated.Code);
    }

    [Test]
    public async Task Claim_ScopedToAKind_TakesOnlyThatKind()
    {
        await using var context = await CreateDatabaseAsync("claim-kind.sqlite");
        var fixture = await SeedAsync(context);
        var evaluations = new TrainingEvaluationStore(context, TimeProvider.System);
        var runs = new TrainingRunStore(context, TimeProvider.System);
        var evaluation = await evaluations.CreateAndEnqueueAsync(Command(fixture));

        // The training run's own work item was already claimed and terminalized by the fixture, so the evaluation is
        // the only queued row. A run-scoped claim must decline it rather than run it without the runtime-mutation
        // lease the run branch holds — that is what makes "acquire the right locks, then claim" safe.
        AssertEx.Null(await runs.ClaimNextAsync(TrainingWorkKind.TrainingRun), "A run-scoped claim must not take an evaluation.");

        var claimed = AssertEx.NotNull(await runs.ClaimNextAsync(TrainingWorkKind.EvaluationRun));
        AssertEx.Equal(evaluation.Id, claimed.TargetId);
        AssertEx.Null(await runs.PeekNextKindAsync(), "Nothing is queued once the only item is claimed.");
    }

    private static TrainingComparisonInput Report(Guid baseId, Guid tunedId) =>
        new("base vs tuned", baseId, tunedId, Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""));

    private static async Task<TrainingEvaluationRecord> Scored(TrainingEvaluationStore store, EvaluationFixture fixture, string modelName)
    {
        var evaluation = await store.CreateAndEnqueueAsync(Command(fixture) with
        {
            ModelName = modelName
        });
        _ = await store.AppendResultsAsync(evaluation.Id,
            fixture.SampleIds.Select(id => new TrainingEvaluationResultEntry(id, "tool-call", Passed: true, "deterministic")).ToArray());
        return await store.CompleteAsync(evaluation.Id, TrainingWorkStatus.Succeeded, errorMessage: null);
    }

    private static TrainingEvaluationEnqueueCommand Command(EvaluationFixture fixture) =>
        new(fixture.TrainingRunId,
            "base-model",
            "v1:" + new string('d', count: 64),
            fixture.DatasetId,
            fixture.DatasetContentFingerprint,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"holdoutSampleIds":[]}"""),
            fixture.SampleIds.Count);

    /// <summary>Drives the shipped stores to produce the dataset, checkpoint and training run an evaluation hangs off.</summary>
    private static async Task<EvaluationFixture> SeedAsync(NodeChatDbContext context)
    {
        var datasetStore = new TrainingDatasetStore(context, TimeProvider.System);
        var definition = await datasetStore.CreateDefinitionAsync(new TrainingDefinitionInput("tool calling", TrainingDatasetKind.ToolCalling, Encoding.UTF8.GetBytes("""{"schemaVersion":1}""")));
        var dataset = await datasetStore.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));
        _ = await datasetStore.ClaimNextAsync();

        var sampleIds = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var appended = await datasetStore.AppendSampleAsync(new TrainingSampleInput(dataset.Id,
                "tool-call",
                TrainingSampleLabel.Good,
                Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"parts":[{"kind":"user","sequence":0,"content":"q{{index}}"}]}"""),
                ValidationJson: null,
                TrainingSampleProvenance.Generated,
                new string((char)('a' + index), count: 64)));
            sampleIds.Add(AssertEx.NotNull(appended.Sample, "The sample should be appended.").Id);
        }

        var ready = await datasetStore.CompleteGenerationAsync(dataset.Id, DatasetGenerationWorkStatus.Succeeded, errorMessage: null);

        var artifactStore = new TrainingBaseArtifactStore(context, TimeProvider.System);
        var downloading = await artifactStore.StartDownloadAsync("org/base-model", new string('b', count: 40));
        var baseArtifact = await artifactStore.MarkReadyAsync(downloading.Id,
            downloading.Version,
            Encoding.UTF8.GetBytes("""[]"""),
            totalBytes: 42,
            licenseJson: null);

        var runStore = new TrainingRunStore(context, TimeProvider.System);
        var run = await runStore.CreateAndEnqueueAsync(new TrainingRunEnqueueCommand(ready.Id,
            ready.Version,
            baseArtifact.Id,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""),
            Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""),
            Encoding.UTF8.GetBytes("""{"license":"apache-2.0"}"""),
            "base-model"));
        // The run's own work item is claimed here so the evaluation is the only queued row in the kind-scoped tests.
        _ = await runStore.ClaimNextAsync(TrainingWorkKind.TrainingRun);

        return new EvaluationFixture(run.Id,
            ready.Id,
            AssertEx.NotNull(ready.ContentFingerprint, "A ready dataset carries a content fingerprint."),
            sampleIds);
    }

    private async Task<NodeChatDbContext> CreateDatabaseAsync(string fileNameOrPath)
    {
        var databasePath = Path.IsPathRooted(fileNameOrPath) ? fileNameOrPath : GetDatabasePath(fileNameOrPath);
        var context = CreateContext(databasePath);
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();
        return context;
    }

    private NodeChatDbContext CreateContext(string databasePath) =>
        AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder);

    private string GetDatabasePath(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private sealed record EvaluationFixture(Guid TrainingRunId, Guid DatasetId, string DatasetContentFingerprint, IReadOnlyList<Guid> SampleIds);
}
