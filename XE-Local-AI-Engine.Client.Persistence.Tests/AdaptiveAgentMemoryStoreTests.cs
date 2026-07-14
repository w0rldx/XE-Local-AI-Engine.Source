namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
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
            Directory.Delete(_rootPath, recursive: true);
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
        AssertEx.Equal(expected: 0, (int)PlaybookActionSource.Manual);
        AssertEx.Equal(expected: 1, (int)PlaybookActionSource.Analysis);
        AssertEx.Equal(expected: 2, (int)PlaybookActionSource.Extracted);

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
            AssertEx.Equal(expected: 1, updated.Version, "Toggling DefaultTemporaryChat alone must not bump Version (non-config-affecting).");
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
            AssertEx.Equal(expected: 1, updated.Version, "Toggling MemoryExtractionEnabled alone must not bump Version (non-config-affecting).");
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
                LatencyMs: 1234L,
                Success: true,
                PromptTokens: 100,
                CompletionTokens: 42));
            logId = added.Id;

            AssertEx.True(added.Id != Guid.Empty, "Add should assign a log id.");
            AssertEx.True(added.CreatedAtUtc > 0, "Add should stamp a creation time.");
            AssertEx.Equal(expected: 1234L, added.LatencyMs);
            AssertEx.Equal(expected: 100, added.PromptTokens);
            AssertEx.Equal(expected: 42, added.CompletionTokens);
            AssertEx.True(added.Success);
            AssertEx.Null(added.ErrorClass, "A successful run carries no error class.");
        }

        await using var readContext = CreateContext(databasePath, keyHolder);
        var readStore = new AgentExecutionLogStore(readContext, TimeProvider.System);

        var page = await readStore.ListByAgentAsync(agentDefinitionId, limit: 10);
        AssertEx.Equal(expected: 1, page.Count);
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
            LatencyMs: 500L,
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
            _ = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId, ConversationId: null, MessageId: null, "llama", "h", index, Success: true));
        }

        // A row for a different agent must not appear in the page.
        _ = await store.AddAsync(new AgentExecutionLogInput(otherAgentId, ConversationId: null, MessageId: null, "llama", "h", LatencyMs: 99L, Success: true));

        var firstPage = await store.ListByAgentAsync(agentDefinitionId, limit: 2);
        AssertEx.Equal(expected: 2, firstPage.Count);
        AssertEx.True(firstPage[0].CreatedAtUtc >= firstPage[1].CreatedAtUtc, "Logs should be newest first.");

        var secondPage = await store.ListByAgentAsync(agentDefinitionId, limit: 2, offset: 2);
        AssertEx.Equal(expected: 1, secondPage.Count);

        var all = await store.ListByAgentAsync(agentDefinitionId, limit: 100);
        AssertEx.Equal(expected: 3, all.Count);
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
        var oldLog = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId, ConversationId: null, MessageId: null, "llama", "h", LatencyMs: 1L, Success: true));

        // New row — CreatedAtUtc = 2_000.
        clock.Advance(1_000);
        var newLog = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId, ConversationId: null, MessageId: null, "llama", "h", LatencyMs: 2L, Success: true));

        // Cut off at 1_500 → old row (1_000) is expired; new row (2_000) survives.
        var deleted = await store.DeleteOlderThanAsync(1_500);

        AssertEx.Equal(expected: 1, deleted);
        var remaining = await store.ListByAgentAsync(agentDefinitionId, limit: 10);
        AssertEx.Equal(expected: 1, remaining.Count);
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

        _ = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId, ConversationId: null, MessageId: null, "llama", "h", LatencyMs: 1L, Success: true));

        var deleted = await store.DeleteOlderThanAsync(1_000);

        AssertEx.Equal(expected: 0, deleted);
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
            _ = await store.AddAsync(new AgentExecutionLogInput(agentA, ConversationId: null, MessageId: null, "llama", "h", index, Success: true));
        }

        clock.Advance(10);
        _ = await store.AddAsync(new AgentExecutionLogInput(agentB, ConversationId: null, MessageId: null, "llama", "h", LatencyMs: 99L, Success: true));

        // Cap each agent to its 2 newest rows: agent A loses 2, agent B (only 1 row) loses none.
        var deleted = await store.TrimToMaxPerAgentAsync(2);

        AssertEx.Equal(expected: 2, deleted);

        var agentARows = await store.ListByAgentAsync(agentA, limit: 10);
        AssertEx.Equal(expected: 2, agentARows.Count);
        // The two survivors are the newest (1_040, 1_030).
        AssertEx.Equal(expected: 1_040, agentARows[0].CreatedAtUtc);
        AssertEx.Equal(expected: 1_030, agentARows[1].CreatedAtUtc);

        var agentBRows = await store.ListByAgentAsync(agentB, limit: 10);
        AssertEx.Equal(expected: 1, agentBRows.Count);
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
        _ = await store.AddAsync(new AgentExecutionLogInput(agentDefinitionId, ConversationId: null, MessageId: null, "llama", "h", LatencyMs: 1L, Success: true));

        AssertEx.Equal(expected: 0, await store.TrimToMaxPerAgentAsync(0));
        AssertEx.Equal(expected: 0, await store.TrimToMaxPerAgentAsync(-5));
        AssertEx.Equal(expected: 1, (await store.ListByAgentAsync(agentDefinitionId, limit: 10)).Count);
    }

    [Test]
    public async Task AddRunEnvelopeAsync_CompletedRun_PersistsBoundedFieldsAndDiscriminator()
    {
        var databasePath = GetDatabasePath("run-envelope-completed.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var invocationId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, new MutableTimeProvider(4_242));

        await store.AddRunEnvelopeAsync(new AgentRunEnvelopeInput(conversationId,
            messageId,
            invocationId,
            requestId,
            "llama-3.1",
            "completed",
            Success: true,
            DurationMs: 4321L,
            FailureCategory: null,
            PromptTokens: 120,
            CompletionTokens: 30,
            ContentChunkCount: 12,
            ReasoningChunkCount: 4,
            TraceId: "0af7651916cd43dd8448eb211c80319c"));

        var envelopeRows = await context.AgentExecutionLogs
                                        .AsNoTracking()
                                        .Where(log => log.RecordKind == (int)AgentExecutionLogRecordKind.ChatRunEnvelope)
                                        .ToListAsync();

        AssertEx.Equal(expected: 1, envelopeRows.Count);
        var row = envelopeRows[0];
        AssertEx.Equal(expected: 2, row.SchemaVersion);
        // The bound agent id is not available at the terminalization seam, so envelope rows record Guid.Empty.
        AssertEx.Equal(Guid.Empty, row.AgentDefinitionId);
        AssertEx.Equal(conversationId, row.ConversationId);
        AssertEx.Equal(messageId, row.MessageId);
        AssertEx.Equal(invocationId, row.InvocationId);
        AssertEx.Equal(requestId, row.RequestId);
        AssertEx.Equal("llama-3.1", row.ModelName);
        AssertEx.Equal("completed", row.TerminalStatus);
        AssertEx.True(row.Success);
        AssertEx.Null(row.ErrorClass, "A completed run carries no failure category.");
        AssertEx.Equal(expected: 4321L, row.LatencyMs);
        AssertEx.Equal(expected: 120, row.PromptTokens);
        AssertEx.Equal(expected: 30, row.CompletionTokens);
        AssertEx.Equal(expected: 12, row.ContentChunkCount);
        AssertEx.Equal(expected: 4, row.ReasoningChunkCount);
        AssertEx.Equal("0af7651916cd43dd8448eb211c80319c", row.TraceId);
        AssertEx.Equal(expected: 4_242L, row.CreatedAtUtc);
    }

    [Test]
    public async Task AddRunEnvelopeAsync_FailedRun_StoresFailureCategoryInErrorClassColumn()
    {
        var databasePath = GetDatabasePath("run-envelope-failed.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, TimeProvider.System);

        await store.AddRunEnvelopeAsync(new AgentRunEnvelopeInput(ConversationId: null,
            MessageId: null,
            InvocationId: null,
            RequestId: null,
            "llama",
            "failed",
            Success: false,
            DurationMs: 7L,
            FailureCategory: "ProviderUnreachable"));

        var row = (await context.AgentExecutionLogs.AsNoTracking().ToListAsync()).Single();
        AssertEx.Equal("failed", row.TerminalStatus);
        AssertEx.False(row.Success);
        // The failure-category enum name reuses the text-free ErrorClass column.
        AssertEx.Equal("ProviderUnreachable", row.ErrorClass);
        AssertEx.Null(row.PromptTokens, "A failed run reports no usage.");
        AssertEx.Null(row.CompletionTokens, "A failed run reports no usage.");
    }

    [Test]
    public async Task AddRunEnvelopeAsync_RowIsExcludedFromAdaptiveMemoryDiagnosticsView()
    {
        var databasePath = GetDatabasePath("run-envelope-excluded.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var agentId = Guid.NewGuid();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, TimeProvider.System);

        _ = await store.AddAsync(new AgentExecutionLogInput(agentId, ConversationId: null, MessageId: null, "llama", "h", LatencyMs: 5L, Success: true));
        await store.AddRunEnvelopeAsync(new AgentRunEnvelopeInput(ConversationId: null, MessageId: null, InvocationId: null, RequestId: null, "llama", "completed", Success: true, DurationMs: 1L));

        // The diagnostics read for the real agent returns only its memory row, never the envelope row.
        var agentView = await store.ListByAgentAsync(agentId, limit: 10);
        AssertEx.Equal(expected: 1, agentView.Count);
        AssertEx.Equal(agentId, agentView[0].AgentDefinitionId);

        // The envelope row is stored under Guid.Empty but the kind filter still excludes it, so the view is empty.
        var emptyAgentView = await store.ListByAgentAsync(Guid.Empty, limit: 10);
        AssertEx.Equal(expected: 0, emptyAgentView.Count);

        // Both rows physically exist in the shared table.
        var allRows = await context.AgentExecutionLogs.AsNoTracking().ToListAsync();
        AssertEx.Equal(expected: 2, allRows.Count);
    }

    [Test]
    public async Task DeleteOlderThanAsync_PrunesRunEnvelopeRowsByTable()
    {
        var databasePath = GetDatabasePath("run-envelope-retention.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var agentId = Guid.NewGuid();
        var clock = new MutableTimeProvider(1_000);

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, clock);

        // One memory row and one envelope row, both stamped old (1_000).
        _ = await store.AddAsync(new AgentExecutionLogInput(agentId, ConversationId: null, MessageId: null, "llama", "h", LatencyMs: 1L, Success: true));
        await store.AddRunEnvelopeAsync(new AgentRunEnvelopeInput(ConversationId: null, MessageId: null, InvocationId: null, RequestId: null, "llama", "completed", Success: true, DurationMs: 1L));

        // Retention operates on the whole table, so the sweep prunes BOTH producers' rows regardless of kind.
        var deleted = await store.DeleteOlderThanAsync(1_500);

        AssertEx.Equal(expected: 2, deleted);
        AssertEx.Empty(await context.AgentExecutionLogs.AsNoTracking().ToListAsync());
    }

    [Test]
    public async Task AddRunEnvelopeAsync_IsIdempotentOnMessageId_FirstWriteWins()
    {
        var databasePath = GetDatabasePath("run-envelope-idempotent.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var messageId = Guid.NewGuid();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, TimeProvider.System);

        var conversationId = Guid.NewGuid();
        await store.AddRunEnvelopeAsync(new AgentRunEnvelopeInput(conversationId, messageId, Guid.NewGuid(), Guid.NewGuid(), "llama", "completed", Success: true, DurationMs: 100L));
        // A retry / crash-recovery backfill for the SAME message must not duplicate; the first write wins.
        await store.AddRunEnvelopeAsync(new AgentRunEnvelopeInput(conversationId, messageId, Guid.NewGuid(), Guid.NewGuid(), "llama", "interrupted", Success: false, DurationMs: 0L));

        var rows = await context.AgentExecutionLogs
                                .AsNoTracking()
                                .Where(log => log.RecordKind == (int)AgentExecutionLogRecordKind.ChatRunEnvelope)
                                .ToListAsync();

        AssertEx.Equal(expected: 1, rows.Count);
        AssertEx.Equal("completed", rows[0].TerminalStatus);
        AssertEx.True(rows[0].Success, "The first (completed) write must win over the later interrupted retry.");
    }

    [Test]
    public async Task RunEnvelopeUniqueIndex_RejectsDuplicateMessageIdAtDatabaseLevel()
    {
        var databasePath = GetDatabasePath("run-envelope-unique-index.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var messageId = Guid.NewGuid();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();

        // Insert two envelope rows for the same message id directly (bypassing the store's find-check) to prove the
        // filtered UNIQUE index — not just the app-level check — is the durability guard that can never duplicate.
        context.AgentExecutionLogs.Add(NewEnvelope(messageId));
        _ = await context.SaveChangesAsync();

        context.AgentExecutionLogs.Add(NewEnvelope(messageId));
        _ = await AssertEx.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());

        static Entities.AgentExecutionLog NewEnvelope(Guid messageId)
        {
            return new Entities.AgentExecutionLog
            {
                Id = Guid.NewGuid(),
                RecordKind = (int)AgentExecutionLogRecordKind.ChatRunEnvelope,
                SchemaVersion = AgentRunEnvelope.CurrentSchemaVersion,
                AgentDefinitionId = Guid.Empty,
                MessageId = messageId,
                ModelName = string.Empty,
                ConfigHash = string.Empty,
                TerminalStatus = "completed",
                CreatedAtUtc = 1L
            };
        }
    }

    [Test]
    public async Task AddRunEnvelopeAsync_PersistsV2LifecycleFieldsAndSchemaVersion()
    {
        var databasePath = GetDatabasePath("run-envelope-v2.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, TimeProvider.System);

        await store.AddRunEnvelopeAsync(new AgentRunEnvelopeInput(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "llama",
            "completed",
            Success: true,
            DurationMs: 1234L,
            FailureCategory: null,
            PromptTokens: 100,
            CompletionTokens: 40,
            ContentChunkCount: 9,
            ReasoningChunkCount: 3,
            TraceId: "0af7651916cd43dd8448eb211c80319c",
            ReasoningTokens: 12,
            TotalTokens: 152,
            StartedAtUtc: 5_000L));

        var row = (await context.AgentExecutionLogs.AsNoTracking().ToListAsync()).Single();
        AssertEx.Equal(expected: 2, row.SchemaVersion);
        AssertEx.Equal(expected: 12, row.ReasoningTokens);
        AssertEx.Equal(expected: 152, row.TotalTokens);
        AssertEx.Equal(expected: 5_000L, row.StartedAtUtc);
    }

    [Test]
    public async Task ListRunEnvelopesAsync_FiltersByConversation_AndExcludesMemoryRows()
    {
        var databasePath = GetDatabasePath("run-envelope-list.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, TimeProvider.System);

        await store.AddRunEnvelopeAsync(new AgentRunEnvelopeInput(conversationA, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "llama", "completed", Success: true, DurationMs: 1L));
        await store.AddRunEnvelopeAsync(new AgentRunEnvelopeInput(conversationB, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "llama", "failed", Success: false, DurationMs: 2L));
        // A memory-diagnostics row for conversationA must never appear in the run-envelope read path.
        _ = await store.AddAsync(new AgentExecutionLogInput(Guid.NewGuid(), conversationA, MessageId: null, "llama", "h", LatencyMs: 3L, Success: true));

        var conversationAEnvelopes = await store.ListRunEnvelopesAsync(conversationA, limit: 10);
        AssertEx.Equal(expected: 1, conversationAEnvelopes.Count);
        AssertEx.Equal(conversationA, conversationAEnvelopes[0].ConversationId);
        AssertEx.Equal("completed", conversationAEnvelopes[0].TerminalStatus);
        AssertEx.Equal(expected: 2, conversationAEnvelopes[0].SchemaVersion);

        var allEnvelopes = await store.ListRunEnvelopesAsync(conversationId: null, limit: 10);
        AssertEx.Equal(expected: 2, allEnvelopes.Count);
    }

    [Test]
    public async Task ConversationFootprintPurge_DeletesEnvelopeAndMemoryExecutionLogs()
    {
        var databasePath = GetDatabasePath("run-envelope-purge.sqlite");
        using var keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
        var purgedConversation = Guid.NewGuid();
        var keptConversation = Guid.NewGuid();

        await using var context = CreateContext(databasePath, keyHolder);
        await context.Database.EnsureDeletedAsync();
        await context.Database.EnsureCreatedAsync();
        var store = new AgentExecutionLogStore(context, TimeProvider.System);

        // Both producers write plaintext conversation correlations: a run envelope and a memory-diagnostics row.
        await store.AddRunEnvelopeAsync(new AgentRunEnvelopeInput(purgedConversation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "llama", "completed", Success: true, DurationMs: 1L));
        _ = await store.AddAsync(new AgentExecutionLogInput(Guid.NewGuid(), purgedConversation, Guid.NewGuid(), "llama", "h", LatencyMs: 2L, Success: true));
        // A different conversation's rows must survive the purge.
        await store.AddRunEnvelopeAsync(new AgentRunEnvelopeInput(keptConversation, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "llama", "completed", Success: true, DurationMs: 3L));

        await ConversationFootprintPurge.DeleteAsync(context, purgedConversation, CancellationToken.None);

        var remaining = await context.AgentExecutionLogs.AsNoTracking().ToListAsync();
        AssertEx.True(remaining.All(log => log.ConversationId != purgedConversation),
            "No agent_execution_logs rows (envelope or memory) may remain for a purged conversation.");
        AssertEx.Equal(expected: 1, remaining.Count(log => log.ConversationId == keptConversation));
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
            Description: null,
            Instructions,
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            [],
            new Dictionary<string, bool>(),
            OrchestrationTopologyJson: null);
    }

    private static PlaybookActionInput CreatePlaybookInput(Guid agentDefinitionId)
    {
        return new PlaybookActionInput(agentDefinitionId,
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            "When the user asks to finish or close out work.",
            Behavior,
            "testing",
            Priority: 10);
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
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 1)).ToArray();
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
