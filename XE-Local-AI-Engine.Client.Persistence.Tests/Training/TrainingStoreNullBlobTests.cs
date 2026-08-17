namespace XE_Local_AI_Engine.Client.Persistence.Tests.Training;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Pins that a NULL blob column reads back as an absent <see cref="ReadOnlyMemory{T}" />, not as a present-but-empty
///     one.
/// </summary>
/// <remarks>
///     <c>ReadOnlyMemory&lt;byte&gt;</c> converts implicitly from <c>byte[]</c>, so <c>entity.Foo?.ToArray()</c> and
///     <c>entity.Foo is null ? null : …</c> both yield <c>HasValue == true</c> with <c>Length == 0</c> — the record
///     claims the document exists. The license gate distinguishes "no license metadata found" from "a license the
///     operator must read", so the distinction is load-bearing rather than cosmetic; these tests fail loudly if the
///     projections ever stop routing through <c>OptionalBlob</c>.
/// </remarks>
public sealed class TrainingStoreNullBlobTests : IDisposable
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
    public async Task BaseArtifact_WithNoLicenseColumn_ReadsBackAsAnAbsentDocument()
    {
        await using var context = await CreateDatabaseAsync("base-artifact-null-license.sqlite");
        var store = new TrainingBaseArtifactStore(context, TimeProvider.System);

        var started = await store.StartDownloadAsync("org/base-model", new string('b', count: 40));
        var ready = await store.MarkReadyAsync(started.Id, started.Version, Encoding.UTF8.GetBytes("[]"), totalBytes: 1, licenseJson: null);

        AssertEx.False(ready.LicenseJson.HasValue, "A repository with no license tag must read back as absent, not as an empty document.");
        var reread = AssertEx.NotNull(await store.GetAsync(ready.Id), "The artifact must still exist.");
        AssertEx.False(reread.LicenseJson.HasValue, "The absence must survive a round trip through the database.");
    }

    [Test]
    public async Task Sample_WithNoValidationColumn_ReadsBackAsAnAbsentDocument()
    {
        await using var context = await CreateDatabaseAsync("sample-null-validation.sqlite");
        var store = new TrainingDatasetStore(context, TimeProvider.System);

        var definition = await store.CreateDefinitionAsync(new TrainingDefinitionInput("tool calling", TrainingDatasetKind.ToolCalling, Encoding.UTF8.GetBytes("""{"schemaVersion":1}""")));
        var dataset = await store.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));
        _ = await store.ClaimNextAsync();
        var appended = await store.AppendSampleAsync(new TrainingSampleInput(dataset.Id,
            "tool-call",
            TrainingSampleLabel.Good,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"parts":[]}"""),
            ValidationJson: null,
            TrainingSampleProvenance.Manual,
            new string('c', count: 64)));

        var sample = AssertEx.NotNull(appended.Sample, "A non-duplicate append returns the stored sample.");
        AssertEx.False(sample.ValidationJson.HasValue, "An unvalidated sample must read back as absent, not as an empty verdict.");
        var listed = await store.ListAllSamplesAsync(dataset.Id);
        AssertEx.False(listed.Single().ValidationJson.HasValue, "The absence must survive the list projection too.");
    }

    [Test]
    public async Task Dataset_WithNoPinnedDefinitionColumn_ReadsBackAsAnAbsentDocument()
    {
        await using var context = await CreateDatabaseAsync("dataset-null-definition.sqlite");
        var store = new TrainingDatasetStore(context, TimeProvider.System);

        var definition = await store.CreateDefinitionAsync(new TrainingDefinitionInput("tool calling", TrainingDatasetKind.ToolCalling, Encoding.UTF8.GetBytes("""{"schemaVersion":1}""")));
        var dataset = await store.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));

        // The shape of a row written before pinning existed: the additive migration leaves the column NULL. Reading it
        // as an empty-but-present body would let both executors believe an unreadable definition was pinned.
        _ = await context.Database.ExecuteSqlRawAsync("UPDATE training_datasets SET definition_json = NULL;");
        context.ChangeTracker.Clear();

        var reread = AssertEx.NotNull(await store.GetDatasetAsync(dataset.Id), "The dataset must still exist.");
        AssertEx.False(reread.DefinitionJson.HasValue, "A dataset that predates pinning must read back as absent, not as an empty body.");
    }

    [Test]
    public async Task ToolMock_WithNoVerificationColumn_ReadsBackAsAnAbsentDocument()
    {
        await using var context = await CreateDatabaseAsync("mock-null-verification.sqlite");
        var store = new TrainingDatasetStore(context, TimeProvider.System);

        var mock = await store.CreateMockAsync(new ToolMockInput("read_file", Encoding.UTF8.GetBytes("""{"schemaVersion":1,"rules":[]}"""), Enabled: true));

        AssertEx.False(mock.VerificationJson.HasValue, "An unverified mock must read back as absent, not as an empty verdict.");
        var reread = AssertEx.NotNull(await store.GetMockAsync(mock.Id), "The mock must still exist.");
        AssertEx.False(reread.VerificationJson.HasValue, "The absence must survive a round trip through the database.");
    }

    private async Task<NodeChatDbContext> CreateDatabaseAsync(string fileName)
    {
        _ = Directory.CreateDirectory(_rootPath);
        var context = AgentDefinitionTestContextFactory.Create(Path.Combine(_rootPath, fileName), _keyHolder);
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();
        return context;
    }
}
