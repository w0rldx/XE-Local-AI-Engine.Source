namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     A run freezes its own copy of a dataset, but its freeze record still names the dataset it came from. Deleting
///     that dataset would leave the lineage pointing at nothing, so the delete is refused for as long as any run
///     references it — including a finished one, because the lineage question outlives the run.
/// </summary>
public sealed class TrainingDatasetRunReferenceTests : IDisposable
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
    public async Task Dataset_DeleteWhileReferenced_Refused()
    {
        await using var context = await CreateDatabaseAsync("referenced.sqlite");
        var datasets = new TrainingDatasetStore(context, TimeProvider.System);
        var fixture = await SeedAsync(context, datasets);
        var runs = new TrainingRunStore(context, TimeProvider.System);
        var run = await runs.CreateAndEnqueueAsync(Command(fixture));

        var refused = await AssertEx.ThrowsAsync<TrainingConflictException>(
            () => datasets.DeleteDatasetAsync(fixture.DatasetId, fixture.DatasetVersion));

        AssertEx.Equal("DatasetReferenced", refused.Code);

        // Still refused after the run finishes: the freeze names this dataset for as long as the run row exists.
        _ = await runs.CompleteRunAsync(run.Id, TrainingWorkStatus.Succeeded, errorMessage: null);
        var stillRefused = await AssertEx.ThrowsAsync<TrainingConflictException>(
            () => datasets.DeleteDatasetAsync(fixture.DatasetId, fixture.DatasetVersion));
        AssertEx.Equal("DatasetReferenced", stillRefused.Code);
    }

    [Test]
    public async Task Dataset_DeleteAfterTheRunIsDeleted_Succeeds()
    {
        await using var context = await CreateDatabaseAsync("released.sqlite");
        var datasets = new TrainingDatasetStore(context, TimeProvider.System);
        var fixture = await SeedAsync(context, datasets);
        var runs = new TrainingRunStore(context, TimeProvider.System);
        var run = await runs.CreateAndEnqueueAsync(Command(fixture));
        var finished = await runs.CompleteRunAsync(run.Id, TrainingWorkStatus.Failed, "gave up");
        await runs.DeleteAsync(finished.Id, finished.Version);

        await datasets.DeleteDatasetAsync(fixture.DatasetId, fixture.DatasetVersion);

        AssertEx.Null(await datasets.GetDatasetAsync(fixture.DatasetId), "The guard is a reference count, not a permanent lock.");
    }

    [Test]
    public async Task Dataset_WithNoRuns_IsStillDeletable()
    {
        await using var context = await CreateDatabaseAsync("unreferenced.sqlite");
        var datasets = new TrainingDatasetStore(context, TimeProvider.System);
        var fixture = await SeedAsync(context, datasets);

        await datasets.DeleteDatasetAsync(fixture.DatasetId, fixture.DatasetVersion);

        AssertEx.Null(await datasets.GetDatasetAsync(fixture.DatasetId));
    }

    /// <summary>
    ///     A cancel that arrives before the queue claims terminalizes the work item where it stands, so the completion
    ///     path has to accept a QUEUED item and not only a running one.
    /// </summary>
    [Test]
    public async Task CompleteGeneration_TerminalizesAWorkItemThatWasStillQueued()
    {
        await using var context = await CreateDatabaseAsync("cancel-queued-generation.sqlite");
        var datasets = new TrainingDatasetStore(context, TimeProvider.System);
        var definition = await datasets.CreateDefinitionAsync(
            new TrainingDefinitionInput("tool calling", TrainingDatasetKind.ToolCalling, Encoding.UTF8.GetBytes("""{"schemaVersion":1}""")));
        var dataset = await datasets.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));
        AssertEx.Equal(DatasetGenerationWorkStatus.Queued, dataset.WorkStatus);

        var cancelled = await datasets.CompleteGenerationAsync(dataset.Id, DatasetGenerationWorkStatus.Cancelled, "Cancelled before generation started.");

        AssertEx.Equal(DatasetGenerationWorkStatus.Cancelled, cancelled.WorkStatus);
        AssertEx.Null(await datasets.ClaimNextAsync(), "A cancelled work item must not be claimable afterwards.");
    }

    private static TrainingRunEnqueueCommand Command(RunFixture fixture) =>
        new(fixture.DatasetId,
            fixture.DatasetVersion,
            fixture.BaseArtifactId,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""),
            Encoding.UTF8.GetBytes("""{"schemaVersion":1}"""),
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"repoId":"org/base-model"}"""));

    private static async Task<RunFixture> SeedAsync(NodeChatDbContext context, TrainingDatasetStore datasets)
    {
        var definition = await datasets.CreateDefinitionAsync(
            new TrainingDefinitionInput("tool calling", TrainingDatasetKind.ToolCalling, Encoding.UTF8.GetBytes("""{"schemaVersion":1}""")));
        var dataset = await datasets.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));
        _ = await datasets.ClaimNextAsync();
        _ = await datasets.AppendSampleAsync(new TrainingSampleInput(dataset.Id,
            "tool-call",
            TrainingSampleLabel.Good,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"parts":[]}"""),
            ValidationJson: null,
            TrainingSampleProvenance.Generated,
            new string('c', count: 64)));
        var ready = await datasets.CompleteGenerationAsync(dataset.Id, DatasetGenerationWorkStatus.Succeeded, errorMessage: null);

        var artifacts = new TrainingBaseArtifactStore(context, TimeProvider.System);
        var downloading = await artifacts.StartDownloadAsync("org/base-model", new string('b', count: 40));
        var baseArtifact = await artifacts.MarkReadyAsync(downloading.Id, downloading.Version, Encoding.UTF8.GetBytes("[]"), totalBytes: 1, licenseJson: null);
        return new RunFixture(ready.Id, ready.Version, baseArtifact.Id);
    }

    private async Task<NodeChatDbContext> CreateDatabaseAsync(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();
        return context;
    }

    private sealed record RunFixture(Guid DatasetId, long DatasetVersion, Guid BaseArtifactId);
}
