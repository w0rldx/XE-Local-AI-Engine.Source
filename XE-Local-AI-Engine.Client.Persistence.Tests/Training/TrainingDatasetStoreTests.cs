namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class TrainingDatasetStoreTests : IDisposable
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
    public async Task DefinitionEdit_BumpsBothTheArtifactVersionAndTheConcurrencyToken()
    {
        await using var context = await CreateDatabaseAsync("definition-version.sqlite");
        var store = new TrainingDatasetStore(context, TimeProvider.System);
        var created = await store.CreateDefinitionAsync(Definition("first"));
        AssertEx.Equal(expected: 1L, created.DefinitionVersion);

        var updated = await store.UpdateDefinitionAsync(created.Id, created.Version, Definition("second"));
        AssertEx.Equal(expected: 2L, updated.DefinitionVersion);
        AssertEx.Equal(expected: 2L, updated.Version);
        _ = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.UpdateDefinitionAsync(created.Id, created.Version, Definition("third")));
    }

    [Test]
    public async Task DatasetCreate_PinsTheDefinitionBody_AndALaterEditDoesNotMoveIt()
    {
        await using var context = await CreateDatabaseAsync("definition-pin.sqlite");
        var store = new TrainingDatasetStore(context, TimeProvider.System);
        var definition = await store.CreateDefinitionAsync(Definition("pinned"));

        var dataset = await store.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));
        AssertEx.Equal(BodyOf("pinned"), PinnedBody(dataset), "A new dataset pins its definition body.");

        // The edit bumps DefinitionVersion; the dataset keeps both the version it claims AND the body that version named.
        var edited = await store.UpdateDefinitionAsync(definition.Id, definition.Version, Definition("edited"));
        AssertEx.Equal(expected: 2L, edited.DefinitionVersion);

        var reread = AssertEx.NotNull(await store.GetDatasetAsync(dataset.Id));
        AssertEx.Equal(expected: 1L, reread.DefinitionVersion);
        AssertEx.Equal(BodyOf("pinned"), PinnedBody(reread), "The pin survives an edit.");

        // A sample append rewrites the dataset row's counters; the untouched pin must not be re-encrypted or lost.
        _ = await store.ClaimNextAsync();
        _ = await store.AppendSampleAsync(Sample(dataset.Id, "hash-a"));
        var afterAppend = AssertEx.NotNull(await store.GetDatasetAsync(dataset.Id));
        AssertEx.Equal(BodyOf("pinned"), PinnedBody(afterAppend), "A counter update must not drop the pin.");
    }

    [Test]
    public async Task GenerationQueue_ClaimsRecoversAndTerminalizesIdempotently()
    {
        var databasePath = GetDatabasePath("queue.sqlite");
        Guid datasetId;
        await using (var setup = await CreateDatabaseAsync(databasePath))
        {
            var store = new TrainingDatasetStore(setup, TimeProvider.System);
            var definition = await store.CreateDefinitionAsync(Definition("queue"));
            var dataset = await store.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));
            datasetId = dataset.Id;

            var claimed = AssertEx.NotNull(await store.ClaimNextAsync(), "The queued work item should be claimable.");
            AssertEx.Equal(datasetId, claimed.DatasetId);
            AssertEx.Null(await store.ClaimNextAsync(), "A claimed work item must not be claimable twice.");
        }

        // A fresh process finds the interrupted Running row and terminalizes it — never retries it in place.
        await using var recovery = CreateContext(databasePath);
        var recovered = await new TrainingDatasetStore(recovery, TimeProvider.System).RecoverOnStartupAsync();
        AssertEx.True(recovered.Contains(datasetId), "The interrupted dataset should be recovered.");

        await using var after = CreateContext(databasePath);
        var afterStore = new TrainingDatasetStore(after, TimeProvider.System);
        var failed = AssertEx.NotNull(await afterStore.GetDatasetAsync(datasetId));
        AssertEx.Equal(TrainingDatasetStatus.Failed, failed.Status);
        AssertEx.Equal(DatasetGenerationWorkStatus.Failed, failed.WorkStatus!.Value);

        // Terminalizing an already-terminal work item is a silent no-op, so a startup retrace cannot double-transition.
        var again = await afterStore.CompleteGenerationAsync(datasetId, DatasetGenerationWorkStatus.Succeeded, errorMessage: null);
        AssertEx.Equal(DatasetGenerationWorkStatus.Failed, again.WorkStatus!.Value);
        AssertEx.Equal(TrainingDatasetStatus.Failed, again.Status);
    }

    [Test]
    public async Task Dataset_DeleteWhileReferenced_Refused()
    {
        await using var context = await CreateDatabaseAsync("delete-guard.sqlite");
        var store = new TrainingDatasetStore(context, TimeProvider.System);
        var definition = await store.CreateDefinitionAsync(Definition("guard"));
        var dataset = await store.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));

        var queued = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.DeleteDatasetAsync(dataset.Id, dataset.Version));
        AssertEx.Equal("GenerationActive", queued.Code);

        _ = AssertEx.NotNull(await store.ClaimNextAsync());
        var running = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.DeleteDatasetAsync(dataset.Id, dataset.Version));
        AssertEx.Equal("GenerationActive", running.Code);

        // A definition with datasets is likewise protected.
        var referenced = await AssertEx.ThrowsAsync<TrainingConflictException>(() => store.DeleteDefinitionAsync(definition.Id, definition.Version));
        AssertEx.Equal("DefinitionReferenced", referenced.Code);
    }

    [Test]
    public async Task DatasetDelete_RemovesSamplesExplicitly_BecauseNoCascadeFires()
    {
        var databasePath = GetDatabasePath("delete.sqlite");
        Guid datasetId;
        await using (var setup = await CreateDatabaseAsync(databasePath))
        {
            var store = new TrainingDatasetStore(setup, TimeProvider.System);
            var definition = await store.CreateDefinitionAsync(Definition("delete"));
            var dataset = await store.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));
            datasetId = dataset.Id;
            _ = AssertEx.NotNull(await store.ClaimNextAsync());
            _ = await store.AppendSampleAsync(Sample(datasetId, "hash-a"));
            _ = await store.AppendSampleAsync(Sample(datasetId, "hash-b"));
            var ready = await store.CompleteGenerationAsync(datasetId, DatasetGenerationWorkStatus.Succeeded, errorMessage: null);
            await store.DeleteDatasetAsync(datasetId, ready.Version);
        }

        await using var verify = CreateContext(databasePath);
        AssertEx.Equal(expected: 0, await verify.TrainingDatasetSamples.CountAsync(sample => sample.DatasetId == datasetId));
        AssertEx.Equal(expected: 0, await verify.DatasetGenerationWorkItems.CountAsync(work => work.DatasetId == datasetId));
        AssertEx.Equal(expected: 0, await verify.TrainingDatasets.CountAsync(dataset => dataset.Id == datasetId));
    }

    [Test]
    public async Task SampleAppend_DeduplicatesOnSourceHash_AndCountsTheSkip()
    {
        await using var context = await CreateDatabaseAsync("dedup.sqlite");
        var store = new TrainingDatasetStore(context, TimeProvider.System);
        var datasetId = await StartDatasetAsync(store);

        var first = await store.AppendSampleAsync(Sample(datasetId, "same-hash"));
        AssertEx.False(first.Duplicate, "The first sample is not a duplicate.");
        var second = await store.AppendSampleAsync(Sample(datasetId, "same-hash"));
        AssertEx.True(second.Duplicate, "A repeated source hash within one dataset must be skipped.");
        AssertEx.Null(second.Sample, "A skipped duplicate persists no sample.");

        var dataset = AssertEx.NotNull(await store.GetDatasetAsync(datasetId));
        AssertEx.Equal(expected: 1, dataset.TotalSampleCount);
        AssertEx.Equal(expected: 1, dataset.DuplicateSampleCount);
    }

    [Test]
    public async Task SampleMutation_BumpsRevisionAndRecomputesTheContentFingerprint()
    {
        await using var context = await CreateDatabaseAsync("fingerprint.sqlite");
        var store = new TrainingDatasetStore(context, TimeProvider.System);
        var datasetId = await StartDatasetAsync(store);
        var appended = AssertEx.NotNull((await store.AppendSampleAsync(Sample(datasetId, "hash-a"))).Sample);
        _ = await store.CompleteGenerationAsync(datasetId, DatasetGenerationWorkStatus.Succeeded, errorMessage: null);

        var ready = AssertEx.NotNull(await store.GetDatasetAsync(datasetId));
        var fingerprint = AssertEx.NotNull(ready.ContentFingerprint, "A ready dataset carries a content fingerprint.");
        AssertEx.True(fingerprint.StartsWith("v1:", StringComparison.Ordinal), "The fingerprint carries its algorithm tag.");
        AssertEx.Equal(expected: 64, fingerprint["v1:".Length..].Length);

        var relabelled = await store.ReviewSampleAsync(new TrainingSampleReviewCommand(appended.Id, TrainingSampleReviewVerb.Relabel, TrainingSampleLabel.Bad));
        AssertEx.Equal(TrainingSampleLabel.Bad, relabelled.Label);

        var mutated = AssertEx.NotNull(await store.GetDatasetAsync(datasetId));
        AssertEx.Equal(ready.Revision + 1, mutated.Revision);
        AssertEx.False(string.Equals(fingerprint, mutated.ContentFingerprint, StringComparison.Ordinal),
            "A relabel must move the content fingerprint.");
        AssertEx.Equal(expected: 0, mutated.GoodSampleCount);
        AssertEx.Equal(expected: 1, mutated.BadSampleCount);
    }

    [Test]
    public async Task MockVerification_WhenRejected_DisablesTheMock()
    {
        await using var context = await CreateDatabaseAsync("mock.sqlite");
        var store = new TrainingDatasetStore(context, TimeProvider.System);
        var mock = await store.CreateMockAsync(new ToolMockInput("read_file", Encoding.UTF8.GetBytes("{}"), Enabled: true));
        AssertEx.Equal(ToolMockVerificationState.Unverified, mock.VerificationState);
        AssertEx.Empty(await store.ListUsableMocksAsync("read_file"));

        var rejected = await store.SetMockVerificationAsync(mock.Id, mock.Version, ToolMockVerificationState.Rejected,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"passed":false,"findings":["bad"]}"""));
        AssertEx.False(rejected.Enabled, "A rejected mock cannot stay active.");
        AssertEx.Empty(await store.ListUsableMocksAsync("read_file"));

        var verified = await store.SetMockVerificationAsync(rejected.Id, rejected.Version, ToolMockVerificationState.Verified,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"passed":true,"findings":[]}"""));
        AssertEx.False(verified.Enabled, "Verification does not silently re-enable a mock the operator's edit disabled.");
    }

    private static async Task<Guid> StartDatasetAsync(TrainingDatasetStore store)
    {
        var definition = await store.CreateDefinitionAsync(Definition("dataset"));
        var dataset = await store.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));
        _ = await store.ClaimNextAsync();
        return dataset.Id;
    }

    private static TrainingDefinitionInput Definition(string name) =>
        new(name, TrainingDatasetKind.ToolCalling, Encoding.UTF8.GetBytes(BodyOf(name)));

    private static string PinnedBody(TrainingDatasetRecord dataset)
    {
        AssertEx.True(dataset.DefinitionJson.HasValue, "The dataset must carry a pinned definition body.");
        return Encoding.UTF8.GetString(dataset.DefinitionJson!.Value.Span);
    }

    /// <summary>The definition body, distinguishable per name so a pin can be told apart from a later edit.</summary>
    private static string BodyOf(string name) =>
        $$"""{"schemaVersion":1,"teacherModelName":"{{name}}-teacher.gguf"}""";

    private static TrainingSampleInput Sample(Guid datasetId, string sourceHash) =>
        new(datasetId, "tool-call", TrainingSampleLabel.Good,
            Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"parts":[{"kind":"user","sequence":0,"content":"{{sourceHash}}"}]}"""),
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"passed":true,"layers":[]}"""),
            TrainingSampleProvenance.Generated, sourceHash);

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
}
