namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Persistence round-trips for the adaptive-agent-memory data model: typed <see cref="MemoryScope" /> on a playbook
///     action, the new <see cref="PlaybookActionSource.Extracted" /> provenance, the per-agent
///     <c>DefaultTemporaryChat</c> flag, and the metadata-only <c>AgentExecutionLog</c>.
/// </summary>
public sealed class AdaptiveAgentMemoryStoreTests : IDisposable
{
    private const string Instructions = "You are a careful engineering agent. Follow the repository conventions exactly.";
    private const string Behavior = "Always run the full test suite before reporting a task complete.";
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task PlaybookActionMemoryScope_RoundTrips()
    {
        var databasePath = GetDatabasePath("memory-scope.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid actionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var agentId = await SeedAgentAsync(context);
            var store = new PlaybookActionStore(context, TimeProvider.System);

            var added = await store.AddAsync(CreatePlaybookInput(agentId) with
            {
                State = PlaybookActionState.Suggested,
                Source = PlaybookActionSource.Extracted,
                MemoryScope = MemoryScope.Failure
            });
            actionId = added.Id;

            AssertEx.Equal(MemoryScope.Failure, added.MemoryScope);
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new PlaybookActionStore(readContext, TimeProvider.System);

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(actionId), "Action should be found by id.");
        AssertEx.Equal(MemoryScope.Failure, byId.MemoryScope);
        AssertEx.Equal(PlaybookActionSource.Extracted, byId.Source);

        var list = await readStore.ListByAgentAsync(byId.AgentDefinitionId);
        AssertEx.Equal(MemoryScope.Failure, list[0].MemoryScope);
    }

    [Test]
    public async Task PlaybookActionMemoryScope_WhenUntyped_RoundTripsNull()
    {
        var databasePath = GetDatabasePath("memory-scope-null.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid actionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var agentId = await SeedAgentAsync(context);
            var store = new PlaybookActionStore(context, TimeProvider.System);

            // CreatePlaybookInput leaves MemoryScope at its null default — a legacy/untyped action.
            var added = await store.AddAsync(CreatePlaybookInput(agentId));
            actionId = added.Id;

            AssertEx.Null(added.MemoryScope, "An untyped action carries no memory scope.");
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new PlaybookActionStore(readContext, TimeProvider.System);

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(actionId), "Action should be found by id.");
        AssertEx.Null(byId.MemoryScope, "An untyped action's memory scope should read back as null.");
    }

    [Test]
    public async Task PlaybookActionSource_Extracted_EncodesDecodes()
    {
        // Pin the existing ints so a future reorder of the enum is caught by this test (the value is persisted as a
        // plain int, so the on-disk contract depends on these never changing).
        AssertEx.Equal(0, (int)PlaybookActionSource.Manual);
        AssertEx.Equal(1, (int)PlaybookActionSource.Analysis);
        AssertEx.Equal(2, (int)PlaybookActionSource.Extracted);

        var databasePath = GetDatabasePath("source-extracted.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid actionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var agentId = await SeedAgentAsync(context);
            var store = new PlaybookActionStore(context, TimeProvider.System);

            var added = await store.AddAsync(CreatePlaybookInput(agentId) with
            {
                State = PlaybookActionState.Suggested,
                Source = PlaybookActionSource.Extracted,
                MemoryScope = MemoryScope.Procedural
            });
            actionId = added.Id;
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new PlaybookActionStore(readContext, TimeProvider.System);

        var byId = AssertEx.NotNull(await readStore.GetByIdAsync(actionId), "Action should be found by id.");
        AssertEx.Equal(PlaybookActionSource.Extracted, byId.Source);
    }

    [Test]
    public async Task AgentDefaultTemporaryChat_RoundTrips()
    {
        var databasePath = GetDatabasePath("default-temp-chat.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid definitionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentDefinitionStore(context, TimeProvider.System);

            var added = await store.AddAsync(CreateAgentInput() with
            {
                DefaultTemporaryChat = true
            });
            definitionId = added.Id;

            AssertEx.True(added.DefaultTemporaryChat, "Add should persist DefaultTemporaryChat.");
        }

        await using (var readContext = CreateContext(databasePath, keyHolder))
        {
            var readStore = new AgentDefinitionStore(readContext, TimeProvider.System);
            var byId = AssertEx.NotNull(await readStore.GetByIdAsync(definitionId), "Definition should be found by id.");
            AssertEx.True(byId.DefaultTemporaryChat, "DefaultTemporaryChat should round-trip true.");

            // Toggling it off via update must round-trip and (it is non-config-affecting) must not bump Version.
            var updated = AssertEx.NotNull(await readStore.UpdateAsync(definitionId, CreateAgentInput() with
                {
                    DefaultTemporaryChat = false
                }),
                "Update should find the definition.");
            AssertEx.False(updated.DefaultTemporaryChat, "DefaultTemporaryChat should round-trip false after update.");
            AssertEx.Equal(1, updated.Version, "Toggling DefaultTemporaryChat alone must not bump Version (non-config-affecting).");
        }
    }

    [Test]
    public async Task AgentDefaultTemporaryChat_DefaultsFalse()
    {
        var databasePath = GetDatabasePath("default-temp-chat-default.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentDefinitionStore(context, TimeProvider.System);

        var added = await store.AddAsync(CreateAgentInput());

        AssertEx.False(added.DefaultTemporaryChat, "A definition created without the flag defaults to non-temporary.");
    }

    [Test]
    public async Task AgentMemoryExtractionEnabled_DefaultsTrue()
    {
        var databasePath = GetDatabasePath("memory-extraction-default.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentDefinitionStore(context, TimeProvider.System);

        // CreateAgentInput leaves MemoryExtractionEnabled at its record default (true) — opting into the playbook keeps
        // learning from runs unless extraction is explicitly turned off.
        var added = await store.AddAsync(CreateAgentInput());

        AssertEx.True(added.MemoryExtractionEnabled, "A definition created without the flag defaults to extraction ON.");
    }

    [Test]
    public async Task AgentMemoryExtractionEnabled_RoundTrips()
    {
        var databasePath = GetDatabasePath("memory-extraction-roundtrip.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        Guid definitionId;
        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentDefinitionStore(context, TimeProvider.System);

            // Opt into retrieval-only memory: extraction off.
            var added = await store.AddAsync(CreateAgentInput() with
            {
                MemoryExtractionEnabled = false
            });
            definitionId = added.Id;

            AssertEx.False(added.MemoryExtractionEnabled, "Add should persist MemoryExtractionEnabled.");
        }

        await using (var readContext = CreateContext(databasePath, keyHolder))
        {
            var readStore = new AgentDefinitionStore(readContext, TimeProvider.System);
            var byId = AssertEx.NotNull(await readStore.GetByIdAsync(definitionId), "Definition should be found by id.");
            AssertEx.False(byId.MemoryExtractionEnabled, "MemoryExtractionEnabled should round-trip false.");

            // Toggling it back on via update must round-trip and (it is non-config-affecting) must not bump Version.
            var updated = AssertEx.NotNull(await readStore.UpdateAsync(definitionId, CreateAgentInput() with
                {
                    MemoryExtractionEnabled = true
                }),
                "Update should find the definition.");
            AssertEx.True(updated.MemoryExtractionEnabled, "MemoryExtractionEnabled should round-trip true after update.");
            AssertEx.Equal(1, updated.Version, "Toggling MemoryExtractionEnabled alone must not bump Version (non-config-affecting).");
        }
    }

    [Test]
    public async Task AgentExecutionLog_StoresMetadataOnly_NoContent()
    {
        var databasePath = GetDatabasePath("exec-log.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        var agentDefinitionId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        Guid logId;

        await using (var context = CreateContext(databasePath, keyHolder))
        {
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            var store = new AgentExecutionLogStore(context, TimeProvider.System);

            var added = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId,
                conversationId,
                messageId,
                "llama",
                "config-hash-abc",
                1234L,
                Success: true,
                PromptTokens: 100,
                CompletionTokens: 42));
            logId = added.Id;

            AssertEx.True(added.Id != Guid.Empty, "Add should assign a log id.");
            AssertEx.True(added.CreatedAtUtc > 0, "Add should stamp a creation time.");
            AssertEx.Equal(1234L, added.LatencyMs);
            AssertEx.Equal(100, added.PromptTokens);
            AssertEx.Equal(42, added.CompletionTokens);
            AssertEx.True(added.Success);
            AssertEx.Null(added.ErrorClass, "A successful run carries no error class.");
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new AgentExecutionLogStore(readContext, TimeProvider.System);

        var page = await readStore.ListByAgentAsync(agentDefinitionId, 10);
        AssertEx.Equal(1, page.Count);
        AssertEx.Equal(logId, page[0].Id);
        AssertEx.Equal(conversationId, page[0].ConversationId);
        AssertEx.Equal(messageId, page[0].MessageId);
        AssertEx.Equal("config-hash-abc", page[0].ConfigHash);

        // The table must hold metadata only: assert at the schema level that no content/behavior column exists.
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(agent_execution_logs);";
        var columns = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(reader.GetOrdinal("name")));
            }
        }

        AssertEx.False(columns.Contains("content"), "The execution log must not carry message content.");
        AssertEx.False(columns.Contains("behavior"), "The execution log must not carry behavior text.");
        AssertEx.False(columns.Contains("instructions"), "The execution log must not carry instruction text.");
    }

    [Test]
    public async Task AgentExecutionLog_FailedRun_StoresErrorClassTypeNameOnly()
    {
        var databasePath = GetDatabasePath("exec-log-failed.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var agentDefinitionId = Guid.NewGuid();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, TimeProvider.System);

        // ErrorClass is a type name only; usage tokens are absent on a failed run, so they round-trip as null.
        var added = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId,
            ConversationId: null,
            MessageId: null,
            "llama",
            "config-hash-xyz",
            500L,
            Success: false,
            ErrorClass: "HttpRequestException"));

        AssertEx.False(added.Success);
        AssertEx.Equal("HttpRequestException", added.ErrorClass);
        AssertEx.Null(added.PromptTokens, "A failed run reports no prompt usage.");
        AssertEx.Null(added.CompletionTokens, "A failed run reports no completion usage.");
        AssertEx.Null(added.ConversationId);
        AssertEx.Null(added.MessageId);
    }

    [Test]
    public async Task AgentExecutionLog_ListByAgent_ReturnsNewestFirstAndPages()
    {
        var databasePath = GetDatabasePath("exec-log-paging.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var agentDefinitionId = Guid.NewGuid();
        var otherAgentId = Guid.NewGuid();
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, clock);

        for (var index = 0; index < 3; index++)
        {
            clock.Advance(10);
            _ = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId, null, null, "llama", "h", index, Success: true));
        }

        // A row for a different agent must not appear in the page.
        _ = await store.AddAsync(new AgentExecutionLogInput(otherAgentId, null, null, "llama", "h", 99L, Success: true));

        var firstPage = await store.ListByAgentAsync(agentDefinitionId, 2);
        AssertEx.Equal(2, firstPage.Count);
        AssertEx.True(firstPage[0].CreatedAtUtc >= firstPage[1].CreatedAtUtc, "Logs should be newest first.");

        var secondPage = await store.ListByAgentAsync(agentDefinitionId, 2, 2);
        AssertEx.Equal(1, secondPage.Count);

        var all = await store.ListByAgentAsync(agentDefinitionId, 100);
        AssertEx.Equal(3, all.Count);
        AssertEx.True(all.All(log => log.AgentDefinitionId == agentDefinitionId), "Only the requested agent's logs should be returned.");
    }

    [Test]
    public async Task DeleteOlderThanAsync_RemovesRowsOlderThanCutoff_KeepsRecent()
    {
        var databasePath = GetDatabasePath("exec-log-sweep.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var agentDefinitionId = Guid.NewGuid();
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, clock);

        // Old row — CreatedAtUtc = 1_000.
        var oldLog = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId, null, null, "llama", "h", 1L, Success: true));

        // New row — CreatedAtUtc = 2_000.
        clock.Advance(1_000);
        var newLog = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId, null, null, "llama", "h", 2L, Success: true));

        // Cut off at 1_500 → old row (1_000) is expired; new row (2_000) survives.
        var deleted = await store.DeleteOlderThanAsync(1_500);

        AssertEx.Equal(1, deleted);
        var remaining = await store.ListByAgentAsync(agentDefinitionId, 10);
        AssertEx.Equal(1, remaining.Count);
        AssertEx.Equal(newLog.Id, remaining[0].Id);
        AssertEx.True(remaining.All(log => log.Id != oldLog.Id), "The expired row should have been swept.");
    }

    [Test]
    public async Task DeleteOlderThanAsync_WhenNoneExpired_ReturnsZero()
    {
        var databasePath = GetDatabasePath("exec-log-sweep-none.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var agentDefinitionId = Guid.NewGuid();
        var clock = new MutableTimeProvider(5_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, clock);

        _ = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId, null, null, "llama", "h", 1L, Success: true));

        var deleted = await store.DeleteOlderThanAsync(1_000);

        AssertEx.Equal(0, deleted);
    }

    [Test]
    public async Task TrimToMaxPerAgentAsync_KeepsNewestPerAgent_DeletesRest()
    {
        var databasePath = GetDatabasePath("exec-log-trim.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, clock);

        // Four rows for agent A (CreatedAtUtc 1_010..1_040) and one for agent B (1_050).
        for (var index = 0; index < 4; index++)
        {
            clock.Advance(10);
            _ = await store.AddAsync(new AgentExecutionLogInput(agentA, null, null, "llama", "h", index, Success: true));
        }

        clock.Advance(10);
        _ = await store.AddAsync(new AgentExecutionLogInput(agentB, null, null, "llama", "h", 99L, Success: true));

        // Cap each agent to its 2 newest rows: agent A loses 2, agent B (only 1 row) loses none.
        var deleted = await store.TrimToMaxPerAgentAsync(2);

        AssertEx.Equal(2, deleted);

        var agentARows = await store.ListByAgentAsync(agentA, 10);
        AssertEx.Equal(2, agentARows.Count);
        // The two survivors are the newest (1_040, 1_030).
        AssertEx.Equal(1_040, agentARows[0].CreatedAtUtc);
        AssertEx.Equal(1_030, agentARows[1].CreatedAtUtc);

        var agentBRows = await store.ListByAgentAsync(agentB, 10);
        AssertEx.Equal(1, agentBRows.Count);
    }

    [Test]
    public async Task TrimToMaxPerAgentAsync_WhenCapNonPositive_DeletesNothing()
    {
        var databasePath = GetDatabasePath("exec-log-trim-zero.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var agentDefinitionId = Guid.NewGuid();
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, clock);

        clock.Advance(10);
        _ = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId, null, null, "llama", "h", 1L, Success: true));

        AssertEx.Equal(0, await store.TrimToMaxPerAgentAsync(0));
        AssertEx.Equal(0, await store.TrimToMaxPerAgentAsync(-5));
        AssertEx.Equal(1, (await store.ListByAgentAsync(agentDefinitionId, 10)).Count);
    }

    private static async Task<Guid> SeedAgentAsync(NodeChatDbContext context)
    {
        var store = new AgentDefinitionStore(context, TimeProvider.System);
        var agent = await store.AddAsync(CreateAgentInput());
        return agent.Id;
    }

    private static AgentDefinitionInput CreateAgentInput()
    {
        return new AgentDefinitionInput("Builder",
            null,
            Instructions,
            null,
            null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            null);
    }

    private static PlaybookActionInput CreatePlaybookInput(Guid agentDefinitionId)
    {
        return new PlaybookActionInput(agentDefinitionId,
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            "When the user asks to finish or close out work.",
            Behavior,
            "testing",
            10);
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
