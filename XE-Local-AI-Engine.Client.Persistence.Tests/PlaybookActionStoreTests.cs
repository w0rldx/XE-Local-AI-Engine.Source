namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class PlaybookActionStoreTests : IDisposable
{
    private const string Instructions = "You are a careful engineering agent. Follow the repository conventions exactly.";
    private const string Behavior = "Always run the full test suite before reporting a task complete.";
    private const string TriggerCondition = "When the user asks to finish or close out work.";
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task AddAsync_ThenReadBackInNewContext_DecryptsBehaviorAndTriggerCondition()
    {
        var databasePath = GetDatabasePath("roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid actionId;
        Guid agentId;
        await using (var writeContext = CreateContext(databasePath, keyHolder))
        {
            await writeContext.Database.EnsureDeletedAsync();
            await writeContext.Database.EnsureCreatedAsync();

            agentId = await SeedAgentAsync(writeContext);
            var store = new PlaybookActionStore(writeContext, TimeProvider.System);
            var added = await store.AddAsync(CreateInput(agentId));

            AssertEx.Equal(agentId, added.AgentDefinitionId);
            AssertEx.Equal(Behavior, added.Behavior);
            AssertEx.Equal(TriggerCondition, added.TriggerCondition);
            AssertEx.Equal(PlaybookActionState.Enabled, added.State);
            AssertEx.Equal(PlaybookActionSource.Manual, added.Source);
            AssertEx.Equal(10, added.Priority);
            AssertEx.Equal(1, added.Version);
            AssertEx.True(added.Id != Guid.Empty, "Add should assign an action id.");
            AssertEx.True(added.CreatedAtUtc > 0, "Add should stamp a creation time.");
            AssertEx.Equal(added.CreatedAtUtc, added.UpdatedAtUtc);
            actionId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new PlaybookActionStore(readContext, TimeProvider.System);

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(actionId), "Action should be found by id.");
        AssertEx.Equal(Behavior, byId.Behavior);
        AssertEx.Equal(TriggerCondition, byId.TriggerCondition);

        var list = await readStore.ListByAgentAsync(agentId);
        AssertEx.Equal(1, list.Count);

        var unknown = await readStore.GetByIdAsync(Guid.NewGuid());
        AssertEx.Null(unknown, "Unknown id should return null.");
    }

    [Test]
    public async Task AddAsync_WithNullTriggerCondition_RoundTripsNull()
    {
        var databasePath = GetDatabasePath("null-trigger.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid actionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var agentId = await SeedAgentAsync(context);
            var store = new PlaybookActionStore(context, TimeProvider.System);
            var added = await store.AddAsync(CreateInput(agentId) with { TriggerCondition = null });
            actionId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new PlaybookActionStore(readContext, TimeProvider.System);

        var record = AssertEx.NotNull(await readStore.GetByIdAsync(actionId), "Action should be found by id.");
        AssertEx.Null(record.TriggerCondition, "A null trigger condition should round-trip as null.");
        AssertEx.Equal(Behavior, record.Behavior);
    }

    [Test]
    public async Task ListEnabledByAgentAsync_ReturnsOnlyEnabledOrderedByPriorityThenCreatedAt()
    {
        var databasePath = GetDatabasePath("list-enabled.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid agentId;
        Guid otherAgentId;
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        agentId = await SeedAgentAsync(context);
        otherAgentId = await SeedAgentAsync(context);
        var store = new PlaybookActionStore(context, clock);

        // Two enabled actions with the same priority but different creation times to prove the CreatedAtUtc tiebreak.
        clock.Advance(1);
        var second = await store.AddAsync(CreateInput(agentId) with { Behavior = "second", Priority = 5 });
        clock.Advance(1);
        var first = await store.AddAsync(CreateInput(agentId) with { Behavior = "first", Priority = 1 });
        clock.Advance(1);
        var sameTie = await store.AddAsync(CreateInput(agentId) with { Behavior = "tie-later", Priority = 5 });
        // A disabled action on the same agent must be excluded.
        _ = await store.AddAsync(CreateInput(agentId) with { Behavior = "disabled", Priority = 0, State = PlaybookActionState.Disabled });
        // An enabled action on a different agent must be excluded.
        _ = await store.AddAsync(CreateInput(otherAgentId) with { Behavior = "other-agent", Priority = 0 });

        var enabled = await store.ListEnabledByAgentAsync(agentId);

        AssertEx.Equal(3, enabled.Count);
        AssertEx.Equal("first", enabled[0].Behavior);
        AssertEx.Equal("second", enabled[1].Behavior);
        AssertEx.Equal("tie-later", enabled[2].Behavior);
        AssertEx.Equal(first.Id, enabled[0].Id);
        AssertEx.Equal(second.Id, enabled[1].Id);
        AssertEx.Equal(sameTie.Id, enabled[2].Id);
    }

    [Test]
    public async Task UpdateAsync_WhenBehaviorPriorityOrStateChanges_BumpsVersion()
    {
        var databasePath = GetDatabasePath("version-bump.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var agentId = await SeedAgentAsync(context);
        var store = new PlaybookActionStore(context, clock);

        var added = await store.AddAsync(CreateInput(agentId));
        AssertEx.Equal(1, added.Version);

        clock.Advance(10);
        var behaviorChanged = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, CreateInput(agentId) with { Behavior = "A different behavior." }),
            "Update should find the action.");
        AssertEx.Equal(2, behaviorChanged.Version);
        AssertEx.True(behaviorChanged.UpdatedAtUtc > added.UpdatedAtUtc, "A config change should advance UpdatedAtUtc.");

        clock.Advance(10);
        var priorityChanged = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, CreateInput(agentId) with { Behavior = "A different behavior.", Priority = 99 }),
            "Update should find the action.");
        AssertEx.Equal(3, priorityChanged.Version);

        clock.Advance(10);
        var stateChanged = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, CreateInput(agentId) with { Behavior = "A different behavior.", Priority = 99, State = PlaybookActionState.Disabled }),
            "Update should find the action.");
        AssertEx.Equal(4, stateChanged.Version);
    }

    [Test]
    public async Task UpdateAsync_NeverChangesAgentDefinitionId()
    {
        var databasePath = GetDatabasePath("no-reparent.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var ownerAgentId = await SeedAgentAsync(context);
        var otherAgentId = await SeedAgentAsync(context);
        var store = new PlaybookActionStore(context, TimeProvider.System);

        var added = await store.AddAsync(CreateInput(ownerAgentId));

        // Even if a caller bypasses the service guard and supplies a different (real) agent id, the store must NOT
        // re-parent the action — defense-in-depth for the cross-agent IDOR fix.
        var updated = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, CreateInput(otherAgentId) with { Behavior = "Edited behavior." }),
            "Update should find the action.");

        AssertEx.Equal(ownerAgentId, updated.AgentDefinitionId);
    }

    [Test]
    public async Task UpdateAsync_WhenOnlyScopeOrTriggerConditionChanges_DoesNotBumpVersion()
    {
        var databasePath = GetDatabasePath("no-bump.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var clock = new MutableTimeProvider(2_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var agentId = await SeedAgentAsync(context);
        var store = new PlaybookActionStore(context, clock);

        var added = await store.AddAsync(CreateInput(agentId));
        AssertEx.Equal(1, added.Version);

        clock.Advance(25);
        var updated = AssertEx.NotNull(
            await store.UpdateAsync(added.Id, CreateInput(agentId) with { Scope = "new-scope", TriggerCondition = "A different trigger." }),
            "Update should find the action.");

        AssertEx.Equal("new-scope", updated.Scope);
        AssertEx.Equal("A different trigger.", updated.TriggerCondition);
        AssertEx.Equal(1, updated.Version);
        AssertEx.True(updated.UpdatedAtUtc > added.UpdatedAtUtc, "A scope/trigger edit should still advance UpdatedAtUtc.");
    }

    [Test]
    public async Task UpdateAsync_WhenIdMissing_ReturnsNull()
    {
        var databasePath = GetDatabasePath("update-missing.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var agentId = await SeedAgentAsync(context);
        var store = new PlaybookActionStore(context, TimeProvider.System);

        var result = await store.UpdateAsync(Guid.NewGuid(), CreateInput(agentId));
        AssertEx.Null(result, "Updating an unknown id should return null.");
    }

    [Test]
    public async Task DeleteAsync_RemovesRow()
    {
        var databasePath = GetDatabasePath("delete.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var agentId = await SeedAgentAsync(context);
        var store = new PlaybookActionStore(context, TimeProvider.System);

        var added = await store.AddAsync(CreateInput(agentId));

        AssertEx.True(await store.DeleteAsync(added.Id), "Delete should report a removed row.");
        AssertEx.Null(await store.GetByIdAsync(added.Id), "Deleted action should no longer be found.");
        AssertEx.False(await store.DeleteAsync(added.Id), "Deleting a missing id should report no removal.");
    }

    [Test]
    public async Task DatabaseFile_AfterAdd_DoesNotContainPlaintextBehaviorOrTriggerCondition()
    {
        var databasePath = GetDatabasePath("ciphertext.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var behavior = "SECRET-BEHAVIOR-" + Guid.NewGuid().ToString("N");
        var trigger = "SECRET-TRIGGER-" + Guid.NewGuid().ToString("N");

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var agentId = await SeedAgentAsync(context);
            var store = new PlaybookActionStore(context, TimeProvider.System);
            _ = await store.AddAsync(CreateInput(agentId) with { Behavior = behavior, TriggerCondition = trigger });
        }

        var fileBytes = await File.ReadAllBytesAsync(databasePath);
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(behavior)),
            "The SQLite file should not contain the plaintext behavior.");
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(trigger)),
            "The SQLite file should not contain the plaintext trigger condition.");
    }

    [Test]
    public async Task GetByIdAsync_WhenBehaviorTampered_FailsAuthenticatedDecryption()
    {
        var databasePath = GetDatabasePath("tamper.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid actionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var agentId = await SeedAgentAsync(context);
            var store = new PlaybookActionStore(context, TimeProvider.System);
            var added = await store.AddAsync(CreateInput(agentId));
            actionId = added.Id;
        }

        await TamperBehaviorAsync(databasePath);

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new PlaybookActionStore(readContext, TimeProvider.System);

        _ = AssertEx.Throws<CryptographicException>(
            () => readStore.GetByIdAsync(actionId).GetAwaiter().GetResult(),
            "Tampered behavior ciphertext should fail authenticated decryption.");
    }

    private static async Task<Guid> SeedAgentAsync(NodeChatDbContext context)
    {
        var store = new AgentDefinitionStore(context, TimeProvider.System);
        var agent = await store.AddAsync(new AgentDefinitionInput(
            "Builder",
            Description: null,
            Instructions,
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            AllowedToolNames: [],
            ToolApprovals: new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null));
        return agent.Id;
    }

    private static PlaybookActionInput CreateInput(Guid agentDefinitionId)
    {
        return new PlaybookActionInput(
            agentDefinitionId,
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            TriggerCondition,
            Behavior,
            Scope: "testing",
            Priority: 10);
    }

    private static async Task TamperBehaviorAsync(string databasePath)
    {
        // The test database holds exactly one action, so the corruption targets that single row.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();

        byte[] blob;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT behavior FROM playbook_actions LIMIT 1;";
            blob = (byte[])(await read.ExecuteScalarAsync())!;
        }

        // Flip a byte of the trailing AES-GCM authentication tag so authenticated decryption must reject it.
        blob[^1] ^= 0xFF;

        await using var write = connection.CreateCommand();
        write.CommandText = "UPDATE playbook_actions SET behavior = $blob;";
        write.Parameters.AddWithValue("$blob", blob);
        _ = await write.ExecuteNonQueryAsync();
    }

    private static NodeChatDbContext CreateContext(string databasePath, INodeSqliteKeyHolder keyHolder)
    {
        return AgentDefinitionTestContextFactory.Create(databasePath, keyHolder);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(0, 32).Select(static value => (byte)(value + 1)).ToArray();
    }

    private static bool ContainsSubsequence(byte[] source, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var sourceIndex = 0; sourceIndex <= source.Length - needle.Length; sourceIndex++)
        {
            var matched = true;
            for (var needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                if (source[sourceIndex + needleIndex] == needle[needleIndex])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class MutableTimeProvider(long initialMilliseconds) : TimeProvider
    {
        private long _milliseconds = initialMilliseconds;

        public void Advance(long milliseconds)
        {
            _milliseconds += milliseconds;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(_milliseconds);
        }
    }

    private sealed class FixedNodeSqliteKeyHolder(byte[] key) : INodeSqliteKeyHolder
    {
        private byte[]? _key = key;

        public ReadOnlyMemory<byte> Key
        {
            get
            {
                ObjectDisposedException.ThrowIf(_key is null, this);
                return _key;
            }
        }

        public void Dispose()
        {
            if (_key is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
