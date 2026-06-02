namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class ModelClassificationStoreTests : IDisposable
{
    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task UpsertDetectedAsync_ThenReadBackInNewContext_RoundTripsDetectedFields()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");

        await using (var writeContext = CreateContext(databasePath))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new ModelClassificationStore(writeContext, TimeProvider.System);
            var detected = await store.UpsertDetectedAsync(
                "llama3.1",
                "sha256:abc123",
                ModelKind.Chat,
                """["completion","tools"]""");

            AssertEx.Equal("llama3.1", detected.ModelName);
            AssertEx.Equal("sha256:abc123", detected.Digest);
            AssertEx.Equal(ModelKind.Chat, detected.DetectedKind);
            AssertEx.Equal("""["completion","tools"]""", detected.DetectedCapabilitiesJson);
            AssertEx.Null(detected.OverrideKind, "A freshly detected row has no override.");
            AssertEx.True(detected.DetectedAtUtc is > 0, "Detection should stamp a detected-at time.");
            AssertEx.True(detected.UpdatedAtUtc > 0, "Detection should stamp an updated-at time.");
        }

        await using var readContext = CreateContext(databasePath);
        var readStore = new ModelClassificationStore(readContext, TimeProvider.System);

        var byName = AssertEx.NotNull(await readStore.GetByNameAsync("llama3.1"), "Model should be found by name.");
        AssertEx.Equal("sha256:abc123", byName.Digest);
        AssertEx.Equal(ModelKind.Chat, byName.DetectedKind);
        AssertEx.Equal("""["completion","tools"]""", byName.DetectedCapabilitiesJson);

        var unknown = await readStore.GetByNameAsync("does-not-exist");
        AssertEx.Null(unknown, "An unknown model name should return null.");
    }

    [Test]
    public async Task UpsertDetectedAsync_WhenDigestChanges_ReDetectsAndPreservesOverride()
    {
        var databasePath = GetDatabasePath("redetect.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelClassificationStore(context, TimeProvider.System);

        _ = await store.UpsertDetectedAsync("phi3", "sha256:old", ModelKind.Chat, """["completion"]""");

        // Operator overrides the model to Embedding (an intentional, name-keyed override).
        _ = await store.SetOverrideAsync("phi3", ModelKind.Embedding);

        // A re-pull changes the digest and re-detects the detected fields; the override must survive.
        var reDetected = await store.UpsertDetectedAsync("phi3", "sha256:new", ModelKind.Chat, """["completion","vision"]""");

        AssertEx.Equal("sha256:new", reDetected.Digest);
        AssertEx.Equal(ModelKind.Chat, reDetected.DetectedKind);
        AssertEx.Equal("""["completion","vision"]""", reDetected.DetectedCapabilitiesJson);
        AssertEx.Equal(ModelKind.Embedding, reDetected.OverrideKind);
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

        _ = await store.UpsertDetectedAsync("mistral", "sha256:m", ModelKind.Chat, """["completion"]""");
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

        _ = await store.UpsertDetectedAsync("Nomic-Embed-Text", "sha256:n", ModelKind.Embedding, """["embedding"]""");

        // The model_name primary key uses NOCASE collation, so a differently-cased lookup resolves the same row, and a
        // second upsert with a differently-cased name updates that same row rather than inserting a duplicate.
        var lookedUp = AssertEx.NotNull(await store.GetByNameAsync("nomic-embed-text"), "A case-only-different name should resolve the same row.");
        AssertEx.Equal(ModelKind.Embedding, lookedUp.DetectedKind);

        _ = await store.UpsertDetectedAsync("NOMIC-EMBED-TEXT", "sha256:n2", ModelKind.Embedding, """["embedding"]""");

        var all = await store.ListAsync();
        AssertEx.Equal(1, all.Count);
        AssertEx.Equal("sha256:n2", all[0].Digest);
    }

    [Test]
    public async Task ListAsync_ReturnsAllRowsOrderedByName()
    {
        var databasePath = GetDatabasePath("list.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelClassificationStore(context, TimeProvider.System);

        _ = await store.UpsertDetectedAsync("zephyr", "sha256:z", ModelKind.Chat, capabilitiesJson: null);
        _ = await store.UpsertDetectedAsync("alpaca", "sha256:a", ModelKind.Chat, capabilitiesJson: null);
        _ = await store.UpsertDetectedAsync("mxbai-embed-large", "sha256:m", ModelKind.Embedding, """["embedding"]""");

        var all = await store.ListAsync();

        AssertEx.Equal(3, all.Count);
        AssertEx.Equal("alpaca", all[0].ModelName);
        AssertEx.Equal("mxbai-embed-large", all[1].ModelName);
        AssertEx.Equal("zephyr", all[2].ModelName);
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
