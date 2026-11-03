namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[Category(TestCategories.Integration)]
public sealed class ModelClassificationStoreTests : IDisposable
{
    private const long SeededAtUtc = 1_000;

    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task GetByNameAsync_ReadsAnExistingRowBackInANewContext()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");

        await using (var writeContext = CreateContext(databasePath))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();
            await SeedDetectedAsync(writeContext, "llama3.1", "sha256:abc123", ModelKind.Chat, """["completion","tools"]""");
        }

        await using var readContext = CreateContext(databasePath);
        var readStore = new ModelClassificationStore(readContext, TimeProvider.System);

        var byName = AssertEx.NotNull(await readStore.GetByNameAsync("llama3.1"), "Model should be found by name.");
        AssertEx.Equal("llama3.1", byName.ModelName);
        AssertEx.Equal("sha256:abc123", byName.Digest);
        AssertEx.Equal(ModelKind.Chat, byName.DetectedKind);
        AssertEx.Equal(expected: """["completion","tools"]""", byName.DetectedCapabilitiesJson);
        AssertEx.Null(byName.OverrideKind, "A row with no operator choice has no override.");
        AssertEx.Equal(SeededAtUtc, byName.DetectedAtUtc);
        AssertEx.Equal(SeededAtUtc, byName.UpdatedAtUtc);

        var unknown = await readStore.GetByNameAsync("does-not-exist");
        AssertEx.Null(unknown, "An unknown model name should return null.");
    }

    [Test]
    public async Task SetOverrideAsync_OnAnExistingRow_StoresTheOverrideAndKeepsDetectedFields()
    {
        var databasePath = GetDatabasePath("override-existing.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelClassificationStore(context, TimeProvider.System);
        await SeedDetectedAsync(context, "phi3", "sha256:old", ModelKind.Chat, """["completion"]""");

        var overridden = await store.SetOverrideAsync("phi3", ModelKind.Embedding);

        AssertEx.Equal(ModelKind.Embedding, overridden.OverrideKind);
        AssertEx.Equal("sha256:old", overridden.Digest);
        AssertEx.Equal(ModelKind.Chat, overridden.DetectedKind);
        AssertEx.Equal(expected: """["completion"]""", overridden.DetectedCapabilitiesJson);
        AssertEx.True(overridden.UpdatedAtUtc > SeededAtUtc, "Setting an override should stamp a newer updated-at time.");
    }

    [Test]
    public async Task SetOverrideAsync_WhenNoRowExists_InsertsOverrideAgainstUnknownDetectedBaseline()
    {
        var databasePath = GetDatabasePath("override-insert.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelClassificationStore(context, TimeProvider.System);

        var overridden = await store.SetOverrideAsync("brand-new-model", ModelKind.Chat);

        AssertEx.Equal("brand-new-model", overridden.ModelName);
        AssertEx.Equal(ModelKind.Chat, overridden.OverrideKind);
        AssertEx.Equal(ModelKind.Unknown, overridden.DetectedKind);
        AssertEx.Null(overridden.Digest, "An override-only row has no detected digest yet.");
        AssertEx.Null(overridden.DetectedAtUtc, "An override-only row has not been probed.");
        AssertEx.True(overridden.UpdatedAtUtc > 0, "Setting an override should stamp an updated-at time.");
    }

    [Test]
    public async Task SetOverrideAsync_WithNull_ClearsOverrideAndKeepsDetectedFields()
    {
        var databasePath = GetDatabasePath("override-clear.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelClassificationStore(context, TimeProvider.System);

        await SeedDetectedAsync(context, "mistral", "sha256:m", ModelKind.Chat, """["completion"]""");
        _ = await store.SetOverrideAsync("mistral", ModelKind.Embedding);

        var cleared = await store.SetOverrideAsync("mistral", overrideKind: null);

        AssertEx.Null(cleared.OverrideKind, "A null override should clear the operator override.");
        AssertEx.Equal(ModelKind.Chat, cleared.DetectedKind, "Clearing the override must leave the detected fields intact.");
        AssertEx.Equal("sha256:m", cleared.Digest);
    }

    [Test]
    public async Task GetByNameAsync_MatchesNameCaseInsensitively()
    {
        var databasePath = GetDatabasePath("nocase.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelClassificationStore(context, TimeProvider.System);

        await SeedDetectedAsync(context, "Nomic-Embed-Text", "sha256:n", ModelKind.Embedding, """["embedding"]""");

        // The model_name primary key uses NOCASE collation, so a differently-cased lookup resolves the same row, and an
        // override with a differently-cased name updates that same row rather than inserting a duplicate.
        var lookedUp = AssertEx.NotNull(await store.GetByNameAsync("nomic-embed-text"), "A case-only-different name should resolve the same row.");
        AssertEx.Equal(ModelKind.Embedding, lookedUp.DetectedKind);

        _ = await store.SetOverrideAsync("NOMIC-EMBED-TEXT", ModelKind.Chat);

        var all = await store.ListAsync();
        AssertEx.Equal(expected: 1, all.Count);
        AssertEx.Equal(ModelKind.Chat, all[0].OverrideKind);
    }

    [Test]
    public async Task ListAsync_ReturnsAllRowsOrderedByName()
    {
        var databasePath = GetDatabasePath("list.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelClassificationStore(context, TimeProvider.System);

        await SeedDetectedAsync(context, "zephyr", "sha256:z", ModelKind.Chat, capabilitiesJson: null);
        await SeedDetectedAsync(context, "alpaca", "sha256:a", ModelKind.Chat, capabilitiesJson: null);
        await SeedDetectedAsync(context, "mxbai-embed-large", "sha256:m", ModelKind.Embedding, """["embedding"]""");

        var all = await store.ListAsync();

        AssertEx.Equal(expected: 3, all.Count);
        AssertEx.Equal("alpaca", all[0].ModelName);
        AssertEx.Equal("mxbai-embed-large", all[1].ModelName);
        AssertEx.Equal("zephyr", all[2].ModelName);
    }

    // Nothing in production writes the detected fields any more; rows that carry them only exist on upgraded databases.
    private static async Task SeedDetectedAsync(NodeChatDbContext context, string modelName, string digest, ModelKind kind, string? capabilitiesJson)
    {
        _ = context.ModelClassifications.Add(new ModelClassification
        {
            ModelName = modelName,
            Digest = digest,
            DetectedKind = kind,
            DetectedCapabilitiesJson = capabilitiesJson,
            DetectedAtUtc = SeededAtUtc,
            UpdatedAtUtc = SeededAtUtc
        });
        _ = await context.SaveChangesAsync();
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        // The model_classifications table holds no encrypted columns, so the non-encrypting migration factory is used.
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
