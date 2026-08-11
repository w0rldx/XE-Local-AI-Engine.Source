namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class ModelLaunchArgumentsStoreTests : IDisposable
{
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
    public async Task UpsertAsync_ThenReadBackInNewContext_RoundTripsRawArguments()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");

        await using (var writeContext = CreateContext(databasePath))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new ModelLaunchArgumentsStore(writeContext, TimeProvider.System);
            var saved = await store.UpsertAsync("llama3-gguf", "--top-k 40 --repeat-penalty 1.1");

            AssertEx.Equal("llama3-gguf", saved.ModelName);
            AssertEx.Equal("--top-k 40 --repeat-penalty 1.1", saved.RawArguments);
            AssertEx.True(saved.UpdatedAtUtc > 0, "Upsert should stamp an updated-at time.");
        }

        await using var readContext = CreateContext(databasePath);
        var readStore = new ModelLaunchArgumentsStore(readContext, TimeProvider.System);

        var raw = await readStore.GetRawArgumentsAsync("llama3-gguf");
        AssertEx.Equal("--top-k 40 --repeat-penalty 1.1", raw);
    }

    [Test]
    public async Task GetRawArgumentsAsync_WhenNoOverride_ReturnsNull()
    {
        var databasePath = GetDatabasePath("none.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelLaunchArgumentsStore(context, TimeProvider.System);

        var raw = await store.GetRawArgumentsAsync("never-set");

        AssertEx.Null(raw, "A model with no override returns null so the resolver applies no extra args.");
    }

    [Test]
    public async Task UpsertAsync_WhenOverrideExists_ReplacesIt_MatchingNameCaseInsensitively()
    {
        var databasePath = GetDatabasePath("replace.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelLaunchArgumentsStore(context, TimeProvider.System);

        _ = await store.UpsertAsync("Phi-3-Mini", "--top-k 40");

        // The model_name primary key uses NOCASE collation, so a differently-cased upsert updates the same row.
        _ = await store.UpsertAsync("phi-3-mini", "--top-p 0.9");

        var all = await store.ListAsync();
        AssertEx.Equal(expected: 1, all.Count);
        AssertEx.Equal("--top-p 0.9", all[0].RawArguments);

        var lookedUp = await store.GetRawArgumentsAsync("PHI-3-MINI");
        AssertEx.Equal("--top-p 0.9", lookedUp);
    }

    [Test]
    public async Task DeleteAsync_RemovesOverride_AndIsIdempotent()
    {
        var databasePath = GetDatabasePath("delete.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelLaunchArgumentsStore(context, TimeProvider.System);

        _ = await store.UpsertAsync("mistral", "--top-k 40");

        AssertEx.True(await store.DeleteAsync("mistral"), "Deleting an existing override reports true.");
        AssertEx.Null(await store.GetRawArgumentsAsync("mistral"), "The override is gone after delete.");
        AssertEx.False(await store.DeleteAsync("mistral"), "Deleting an absent override is a no-op that reports false.");
    }

    [Test]
    public async Task ListAsync_ReturnsAllRowsOrderedByName()
    {
        var databasePath = GetDatabasePath("list.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelLaunchArgumentsStore(context, TimeProvider.System);

        _ = await store.UpsertAsync("zephyr", "--top-k 1");
        _ = await store.UpsertAsync("alpaca", "--top-k 2");
        _ = await store.UpsertAsync("mistral", "--top-k 3");

        var all = await store.ListAsync();

        AssertEx.Equal(expected: 3, all.Count);
        AssertEx.Equal("alpaca", all[0].ModelName);
        AssertEx.Equal("mistral", all[1].ModelName);
        AssertEx.Equal("zephyr", all[2].ModelName);
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        // The model_launch_arguments table holds no encrypted columns, so the non-encrypting migration factory is used.
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
