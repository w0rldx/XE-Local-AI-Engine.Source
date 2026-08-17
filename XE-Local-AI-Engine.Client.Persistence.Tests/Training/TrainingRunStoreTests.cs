namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class TrainingRunStoreTests : IDisposable
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
    public async Task CreateRun_CopiesTheDatasetFreezeKey_AndEnqueuesOneWorkItem()
    {
        await using var context = await CreateDatabaseAsync("create.sqlite");
        var fixture = await SeedAsync(context);
        var store = new TrainingRunStore(context, TimeProvider.System);

        var run = await store.CreateAndEnqueueAsync(Command(fixture));

        AssertEx.Equal(fixture.DatasetContentFingerprint, run.DatasetContentFingerprint);
        AssertEx.Equal(fixture.DatasetRevision, run.DatasetRevision);
        AssertEx.Equal(TrainingRunStatus.Queued, run.Status);
        AssertEx.Equal(TrainingWorkStatus.Queued, run.WorkStatus!.Value);
        AssertEx.Equal(expected: 1L, run.Version);
        AssertEx.Equal(expected: 1, await context.TrainingWorkItems.CountAsync(item => item.TargetId == run.Id));
    }

    [Test]
    public async Task CreateRun_WhenAnyPreconditionFails_InsertsNothing()
    {
        var databasePath = GetDatabasePath("atomic.sqlite");
        await using (var context = await CreateDatabaseAsync(databasePath))
        {
            var fixture = await SeedAsync(context);
            var store = new TrainingRunStore(context, TimeProvider.System);

            // The dataset moved under the caller's confirmation dialog.
            var stale = await AssertEx.ThrowsAsync<TrainingConflictException>(
                () => store.CreateAndEnqueueAsync(Command(fixture) with { ExpectedDatasetVersion = fixture.DatasetVersion + 1 }));
            AssertEx.Equal("VersionConflict", stale.Code);

            _ = await AssertEx.ThrowsAsync<TrainingNotFoundException>(
                () => store.CreateAndEnqueueAsync(Command(fixture) with { BaseArtifactId = Guid.NewGuid() }));

            // The license gate is mandatory at creation, not at launch.
            _ = await AssertEx.ThrowsAsync<TrainingValidationException>(
                () => store.CreateAndEnqueueAsync(Command(fixture) with { LicenseConfirmationJson = ReadOnlyMemory<byte>.Empty }));
        }

        await using var verify = CreateContext(databasePath);
        AssertEx.Equal(expected: 0, await verify.TrainingRuns.CountAsync(), "A refused creation must leave no run behind.");
        AssertEx.Equal(expected: 0, await verify.TrainingWorkItems.CountAsync(), "A refused creation must leave no work item behind.");
    }

    [Test]
    public async Task Queue_ClaimsRecoversAndTerminalizesIdempotently()
    {
        var databasePath = GetDatabasePath("queue.sqlite");
        Guid runId;
        await using (var setup = await CreateDatabaseAsync(databasePath))
        {
            var fixture = await SeedAsync(setup);
            var store = new TrainingRunStore(setup, TimeProvider.System);
            var run = await store.CreateAndEnqueueAsync(Command(fixture));
            runId = run.Id;

            var claimed = AssertEx.NotNull(await store.ClaimNextAsync(), "The queued work item should be claimable.");
            AssertEx.Equal(runId, claimed.TargetId);
            AssertEx.Equal(TrainingWorkKind.TrainingRun, claimed.Kind);
            AssertEx.NotNull(claimed.Run, "A training-run claim carries its run.");
            AssertEx.Null(await store.ClaimNextAsync(), "A claimed work item must not be claimable twice.");

            var training = await store.TransitionAsync(runId, run.Version, TrainingRunStatus.Preparing);
            _ = await store.TransitionAsync(runId, training.Version, TrainingRunStatus.Training);
            await store.SetLaunchReceiptAsync(runId, Encoding.UTF8.GetBytes("""{"pid":4242}"""));
        }

        // A fresh process finds the interrupted Running row and terminalizes it — never retries it in place.
        await using var recovery = CreateContext(databasePath);
        var recovered = await new TrainingRunStore(recovery, TimeProvider.System).RecoverOnStartupAsync();
        AssertEx.True(recovered.Contains(runId), "The interrupted run should be recovered.");

        await using var after = CreateContext(databasePath);
        var afterStore = new TrainingRunStore(after, TimeProvider.System);
        var failed = AssertEx.NotNull(await afterStore.GetAsync(runId));
        AssertEx.Equal(TrainingRunStatus.Failed, failed.Status);
        AssertEx.Equal(TrainingWorkStatus.Failed, failed.WorkStatus!.Value);
        // Inverted deliberately: recovery used to null the receipt here. Terminalizing the row proves the HOST died,
        // not the trainer — so dropping the receipt was dropping the only thing that could still identify a live
        // orphan holding VRAM. The receipt now outlives recovery and only TrainingRunStartupReaper clears it, after it
        // has killed or ruled out the process.
        AssertEx.True(failed.LaunchReceiptJson.HasValue, "Recovery must leave the receipt for the reaper to act on.");

        var receipts = await afterStore.ListLaunchReceiptsAsync();
        AssertEx.Equal(expected: 1, receipts.Count, "The unpaged receipt query is what the reaper sweeps; a recovered run is still in it.");
        AssertEx.Equal(runId, receipts[0].RunId);
        AssertEx.Equal("""{"pid":4242}""", Encoding.UTF8.GetString(receipts[0].LaunchReceiptJson.Span),
            "The receipt column is ciphertext at rest; the query must hand back the decrypted document.");

        // Clearing is the reaper's move, and it is the plain SetLaunchReceiptAsync null write.
        await afterStore.SetLaunchReceiptAsync(runId, launchReceiptJson: null);
        AssertEx.Empty(await afterStore.ListLaunchReceiptsAsync(), "A cleared receipt leaves the sweep.");
        AssertEx.False(AssertEx.NotNull(await afterStore.GetAsync(runId)).LaunchReceiptJson.HasValue,
            "A cleared receipt must read as absent, not as an empty blob.");

        // An absent blob column must read as an absent ReadOnlyMemory. It is one keystroke from silently becoming an
        // EMPTY one instead — ReadOnlyMemory<byte> converts implicitly from byte[], so `value?.ToArray()` and
        // `value is null ? null : ...` both yield HasValue == true. This run never reported progress.
        AssertEx.Null(failed.ProgressJson, "An unreported progress snapshot must read as absent, not as an empty blob.");
        AssertEx.Empty(await afterStore.RecoverOnStartupAsync(), "Recovery is idempotent.");

        // Terminalizing an already-terminal work item is a silent no-op, so a startup retrace cannot double-transition.
        var again = await afterStore.CompleteRunAsync(runId, TrainingWorkStatus.Succeeded, errorMessage: null);
        AssertEx.Equal(TrainingWorkStatus.Failed, again.WorkStatus!.Value);
        AssertEx.Equal(TrainingRunStatus.Failed, again.Status);

        // A terminal run refuses to be walked back onto the executor's progression.
        var terminal = await AssertEx.ThrowsAsync<TrainingConflictException>(
            () => afterStore.TransitionAsync(runId, again.Version, TrainingRunStatus.Training));
        AssertEx.Equal("RunTerminal", terminal.Code);
    }

    [Test]
    public async Task LogTail_KeepsOnlyTheLastCharacters()
    {
        await using var context = await CreateDatabaseAsync("logtail.sqlite");
        var fixture = await SeedAsync(context);
        var store = new TrainingRunStore(context, TimeProvider.System);
        var run = await store.CreateAndEnqueueAsync(Command(fixture));

        await store.AppendLogTailAsync(run.Id, new string('x', TrainingRunStore.MaxLogTailLength));
        await store.AppendLogTailAsync(run.Id, "TAIL");

        var bounded = AssertEx.NotNull(await store.GetAsync(run.Id));
        var logTail = AssertEx.NotNull(bounded.LogTail, "The run carries a log tail.");
        AssertEx.Equal(TrainingRunStore.MaxLogTailLength, logTail.Length, "The tail is bounded in the store, not by a CHECK.");
        AssertEx.True(logTail.EndsWith("TAIL", StringComparison.Ordinal), "The tail keeps the newest output, not the oldest.");

        // Telemetry writes are deliberately outside the concurrency token, so the caller's expected version survives them.
        AssertEx.Equal(run.Version, bounded.Version);
    }

    [Test]
    public async Task Artifact_Promotion_RequiresADecidedSmoke()
    {
        await using var context = await CreateDatabaseAsync("artifact.sqlite");
        var fixture = await SeedAsync(context);
        var store = new TrainingRunStore(context, TimeProvider.System);
        var run = await store.CreateAndEnqueueAsync(Command(fixture));

        var staged = await store.CreateArtifactAsync(new TrainingArtifactInput(run.Id, TrainingArtifactKind.AdapterGguf, "adapter.gguf"));
        AssertEx.Equal(TrainingArtifactSmokeState.Pending, staged.SmokeState);
        AssertEx.Null(staged.Sha256, "A freshly staged artifact has not been hashed yet.");

        var hashed = await store.SetArtifactDigestAsync(staged.Id, staged.Version, new string('a', count: 64), sizeBytes: 4096);
        AssertEx.Equal(expected: 4096L, hashed.SizeBytes);

        var pending = await AssertEx.ThrowsAsync<TrainingConflictException>(
            () => store.SetArtifactCommittedNameAsync(hashed.Id, hashed.Version, "trained-adapter"));
        AssertEx.Equal("SmokeNotPassed", pending.Code);

        // A failure and a skip are both decisions that owe a reason.
        _ = await AssertEx.ThrowsAsync<TrainingValidationException>(
            () => store.SetArtifactSmokeStateAsync(hashed.Id, hashed.Version, TrainingArtifactSmokeState.Skipped, reason: null));

        var smoked = await store.SetArtifactSmokeStateAsync(hashed.Id, hashed.Version, TrainingArtifactSmokeState.Passed, reason: null);
        var promoted = await store.SetArtifactCommittedNameAsync(smoked.Id, smoked.Version, "trained-adapter");
        AssertEx.Equal("trained-adapter", promoted.CommittedModelName!);

        var committed = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.DeleteArtifactAsync(promoted.Id, promoted.Version));
        AssertEx.Equal("ArtifactPromoted", committed.Code);
    }

    [Test]
    public async Task RunDelete_IsRefusedWhileActiveOrPromoted_AndOtherwiseRemovesArtifacts()
    {
        var databasePath = GetDatabasePath("delete.sqlite");
        Guid runId;
        await using (var setup = await CreateDatabaseAsync(databasePath))
        {
            var fixture = await SeedAsync(setup);
            var store = new TrainingRunStore(setup, TimeProvider.System);
            var run = await store.CreateAndEnqueueAsync(Command(fixture));
            runId = run.Id;

            var queued = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.DeleteAsync(runId, run.Version));
            AssertEx.Equal("RunActive", queued.Code);

            _ = AssertEx.NotNull(await store.ClaimNextAsync());
            // A second artifact stays staged, so the run delete has a child to remove explicitly.
            _ = await store.CreateArtifactAsync(new TrainingArtifactInput(runId, TrainingArtifactKind.HfAdapterDir, "adapter/"));
            var staged = await store.CreateArtifactAsync(new TrainingArtifactInput(runId, TrainingArtifactKind.MergedGguf, "merged.gguf"));
            var passed = await store.SetArtifactSmokeStateAsync(staged.Id, staged.Version, TrainingArtifactSmokeState.Passed, reason: null);
            var promoted = await store.SetArtifactCommittedNameAsync(passed.Id, passed.Version, "merged-model");
            var done = await store.CompleteRunAsync(runId, TrainingWorkStatus.Succeeded, errorMessage: null);

            var referenced = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.DeleteAsync(runId, done.Version));
            AssertEx.Equal("ArtifactPromoted", referenced.Code);

            // Un-promoting is the escape hatch: without it the run would be undeletable for as long as the row existed.
            var unpromoted = await store.SetArtifactCommittedNameAsync(promoted.Id, promoted.Version, committedModelName: null);
            AssertEx.Null(unpromoted.CommittedModelName);
            await store.DeleteArtifactAsync(unpromoted.Id, unpromoted.Version);
            await store.DeleteAsync(runId, done.Version);
        }

        await using var verify = CreateContext(databasePath);
        AssertEx.Equal(expected: 0, await verify.TrainingArtifacts.CountAsync(item => item.RunId == runId));
        AssertEx.Equal(expected: 0, await verify.TrainingWorkItems.CountAsync(item => item.TargetId == runId));
        AssertEx.Equal(expected: 0, await verify.TrainingRuns.CountAsync(item => item.Id == runId));
    }

    [Test]
    public async Task WorkItem_SecondItemForTheSameTarget_IsRejectedByTheUniqueIndex()
    {
        await using var context = await CreateDatabaseAsync("unique.sqlite");
        var fixture = await SeedAsync(context);
        var store = new TrainingRunStore(context, TimeProvider.System);
        var run = await store.CreateAndEnqueueAsync(Command(fixture));

        // One work item per target per kind, ever — the row is deleted with its run, so this is also what guarantees at
        // most one NON-TERMINAL item per target.
        _ = context.TrainingWorkItems.Add(new TrainingWorkItem
        {
            Kind = TrainingWorkKind.TrainingRun,
            TargetId = run.Id,
            Status = TrainingWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = 1
        });
        _ = await AssertEx.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(),
            "A second work item for the same target and kind must violate the unique index.");
    }

    private static TrainingRunEnqueueCommand Command(RunFixture fixture) =>
        new(fixture.DatasetId,
            fixture.DatasetVersion,
            fixture.BaseArtifactId,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"sampleIds":["a"],"holdout":[]}"""),
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"epochs":3}"""),
            Encoding.UTF8.GetBytes("""{"license":"apache-2.0","confirmedAtUtc":1}"""));

    /// <summary>Drives the shipped dataset and base-artifact stores to produce the Ready dataset and Ready checkpoint a run needs.</summary>
    private static async Task<RunFixture> SeedAsync(NodeChatDbContext context)
    {
        var datasetStore = new TrainingDatasetStore(context, TimeProvider.System);
        var definition = await datasetStore.CreateDefinitionAsync(
            new TrainingDefinitionInput("tool calling", TrainingDatasetKind.ToolCalling, Encoding.UTF8.GetBytes("""{"schemaVersion":1}""")));
        var dataset = await datasetStore.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));
        _ = await datasetStore.ClaimNextAsync();
        _ = await datasetStore.AppendSampleAsync(new TrainingSampleInput(dataset.Id,
            "tool-call",
            TrainingSampleLabel.Good,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"parts":[]}"""),
            ValidationJson: null,
            TrainingSampleProvenance.Generated,
            new string('c', count: 64)));
        var ready = await datasetStore.CompleteGenerationAsync(dataset.Id, DatasetGenerationWorkStatus.Succeeded, errorMessage: null);

        var artifactStore = new TrainingBaseArtifactStore(context, TimeProvider.System);
        var downloading = await artifactStore.StartDownloadAsync("org/base-model", new string('b', count: 40));
        var baseArtifact = await artifactStore.MarkReadyAsync(downloading.Id,
            downloading.Version,
            Encoding.UTF8.GetBytes("""[]"""),
            totalBytes: 42,
            licenseJson: null);

        return new RunFixture(ready.Id,
            ready.Version,
            AssertEx.NotNull(ready.ContentFingerprint, "A ready dataset carries a content fingerprint."),
            ready.Revision,
            baseArtifact.Id);
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

    private sealed record RunFixture(Guid DatasetId, long DatasetVersion, string DatasetContentFingerprint, int DatasetRevision, Guid BaseArtifactId);
}
