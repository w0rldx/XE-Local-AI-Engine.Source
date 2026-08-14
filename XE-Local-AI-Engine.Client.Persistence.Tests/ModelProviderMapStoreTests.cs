namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Models;

public sealed class ModelProviderMapStoreTests : IDisposable
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
    public async Task UpsertAsync_ThenReadBackInNewContext_RoundTripsProviderMapping()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");

        await using (var writeContext = CreateContext(databasePath))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            var store = new ModelProviderMapStore(writeContext, TimeProvider.System);
            var mapped = await store.UpsertAsync("llama3.1-gguf", "llamacpp");

            AssertEx.Equal("llama3.1-gguf", mapped.ModelName);
            AssertEx.Equal("llamacpp", mapped.ProviderName);
            AssertEx.True(mapped.UpdatedAtUtc > 0, "Upsert should stamp an updated-at time.");
        }

        await using var readContext = CreateContext(databasePath);
        var readStore = new ModelProviderMapStore(readContext, TimeProvider.System);

        var provider = await readStore.GetProviderForModelAsync("llama3.1-gguf");
        AssertEx.Equal("llamacpp", provider);
    }

    [Test]
    public async Task GetProviderForModelAsync_WhenUnmapped_ReturnsNull()
    {
        var databasePath = GetDatabasePath("unmapped.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelProviderMapStore(context, TimeProvider.System);

        var provider = await store.GetProviderForModelAsync("never-mapped");

        AssertEx.Null(provider, "An unmapped model returns null so the caller can apply its routing default.");
    }

    [Test]
    public async Task UpsertAsync_WhenModelAlreadyMapped_RepointsToNewProvider()
    {
        var databasePath = GetDatabasePath("repoint.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelProviderMapStore(context, TimeProvider.System);

        _ = await store.UpsertAsync("mistral", "ollama");
        var repointed = await store.UpsertAsync("mistral", "llamacpp");

        AssertEx.Equal("llamacpp", repointed.ProviderName);

        var all = await store.ListAsync();
        AssertEx.Equal(expected: 1, all.Count);
        AssertEx.Equal("llamacpp", all[0].ProviderName);
    }

    [Test]
    public async Task GetProviderForModelAsync_MatchesNameCaseInsensitively()
    {
        var databasePath = GetDatabasePath("nocase.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelProviderMapStore(context, TimeProvider.System);

        _ = await store.UpsertAsync("Phi-3-Mini", "llamacpp");

        // The model_name primary key uses NOCASE collation, so a differently-cased lookup resolves the same row, and a
        // second upsert with a differently-cased name updates that same row rather than inserting a duplicate.
        var lookedUp = await store.GetProviderForModelAsync("phi-3-mini");
        AssertEx.Equal("llamacpp", lookedUp);

        _ = await store.UpsertAsync("PHI-3-MINI", "ollama");

        var all = await store.ListAsync();
        AssertEx.Equal(expected: 1, all.Count);
        AssertEx.Equal("ollama", all[0].ProviderName);
    }

    [Test]
    public async Task ListAsync_ReturnsAllRowsOrderedByName()
    {
        var databasePath = GetDatabasePath("list.sqlite");

        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new ModelProviderMapStore(context, TimeProvider.System);

        _ = await store.UpsertAsync("zephyr", "ollama");
        _ = await store.UpsertAsync("alpaca", "llamacpp");
        _ = await store.UpsertAsync("mistral", "llamacpp");

        var all = await store.ListAsync();

        AssertEx.Equal(expected: 3, all.Count);
        AssertEx.Equal("alpaca", all[0].ModelName);
        AssertEx.Equal("mistral", all[1].ModelName);
        AssertEx.Equal("zephyr", all[2].ModelName);
    }

    [Test]
    public async Task ConditionalMutation_ChangedRevisionCannotBeRemovedOrRestored()
    {
        var databasePath = GetDatabasePath("conditional.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var rawStore = new ModelProviderMapStore(context, TimeProvider.System);
        var domain = new KeyedCompositeLockDomain();
        var leaseCoordinator = new ModelProviderMapLeaseCoordinator(domain);
        var coordinated = new CoordinatedModelProviderMapStore(rawStore);

        await using var lease = await leaseCoordinator.AcquireMapMutationAsync("mistral", ModelProviderMapMutationKind.MapClaim);
        var claim = await coordinated.TryClaimLlamaCppAsync(lease, "mistral");
        var created = claim as ProviderMapClaimResult.Created;
        AssertEx.NotNull(created);

        var replacement = await rawStore.UpsertAsync("mistral", "ollama");
        AssertEx.True(replacement.Revision.Length > 0);
        var restore = await coordinated.TryRestoreAsync(lease, created!.Receipt);
        AssertEx.Equal(ProviderMapRestoreResult.Superseded, restore);

        var removal = await coordinated.TryRemoveIfMatchAsync(lease, "mistral", "llamacpp", created.Receipt.Mutation!.Revision);
        AssertEx.True(removal is ProviderMapRemovalResult.Superseded,
            "A conditional removal must preserve a newer provider/revision.");
        AssertEx.Equal("ollama", await rawStore.GetProviderForModelAsync("mistral"));
    }

    [Test]
    public async Task CoordinatedClaim_CreateAndRestore_RemovesOnlyClaimedRevision()
    {
        var databasePath = GetDatabasePath("claim-restore.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var rawStore = new ModelProviderMapStore(context, TimeProvider.System);
        var leaseCoordinator = new ModelProviderMapLeaseCoordinator(new KeyedCompositeLockDomain());
        var coordinated = new CoordinatedModelProviderMapStore(rawStore);

        await using var lease = await leaseCoordinator.AcquireMapMutationAsync("Phi-3", ModelProviderMapMutationKind.MapClaim);
        var claim = await coordinated.TryClaimLlamaCppAsync(lease, "phi-3");
        var created = AssertEx.NotNull(claim as ProviderMapClaimResult.Created);
        AssertEx.True(created.Receipt.Mutation!.Revision.Length > 0);

        var restored = await coordinated.TryRestoreAsync(lease, created.Receipt);
        AssertEx.Equal(ProviderMapRestoreResult.Restored, restored);
        AssertEx.Null(await rawStore.ReadAsync("PHI-3"));
    }

    [Test]
    public async Task TryInsertAsync_NonUniqueDatabaseFailurePropagates()
    {
        var databasePath = GetDatabasePath("insert-failure.sqlite");
        await using var context = CreateContext(databasePath);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        await context.Database.ExecuteSqlRawAsync("""
                                                  CREATE TRIGGER reject_provider_map_insert
                                                  BEFORE INSERT ON model_provider_map
                                                  BEGIN
                                                      SELECT RAISE(ABORT, 'injected persistence failure');
                                                  END;
                                                  """);
        var store = new ModelProviderMapStore(context, TimeProvider.System);

        _ = await AssertEx.ThrowsAsync<DbUpdateException>(() => store.TryInsertAsync("mistral", "llamacpp"));
    }

    private NodeChatDbContext CreateContext(string databasePath)
    {
        // The model_provider_map table holds no encrypted columns, so the non-encrypting migration factory is used.
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
