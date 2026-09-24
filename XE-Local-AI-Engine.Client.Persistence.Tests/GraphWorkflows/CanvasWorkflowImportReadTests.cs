namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Import;
using XE_Local_AI_Engine.Client.Services.Tools;

/// <summary>
///     The pre-migration half of the one-shot Open Canvas import, exercised over the real upgrade path: a database
///     migrated to the migration BEFORE <c>DropCanvasWorkflows</c>, seeded with encrypted rows, read by
///     <c>CanvasWorkflowImport.ReadAsync</c>, then migrated to head so the table is gone.
///     <para>
///         This suite lives in the persistence project rather than beside the write half because the source table it
///         needs no longer exists at head: there is no entity, no store and no seam to seed through, so the rows go in
///         as raw SQL against the historical schema and their <c>graph_json</c> is encrypted here under exactly the AAD
///         the deleted save interceptor bound — the empty conversation id, the row id, and the column name. Every read
///         assertion is an absolute count, so each test migrates a database of its own.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class CanvasWorkflowImportReadTests
{
    /// <summary>The migration immediately before <c>DropCanvasWorkflows</c> — the last point <c>canvas_workflows</c> exists.</summary>
    private const string PreDropMigrationId = "20260904234758_AddVramAtLoadTelemetry";

    /// <summary>
    ///     A stored canvas blob, written out as the literal the database actually held: web-defaults camelCase members
    ///     and <c>kind</c> as the enum's own name, which is what the Open Canvas serializer produced.
    /// </summary>
    private const string LinearGraph = """
                                       {
                                         "startText": "Summarize the release notes.",
                                         "nodes": [
                                           { "id": "start", "kind": "Start" },
                                           { "id": "agent-1", "kind": "Agent", "label": "Summarize", "instructions": "Summarize it.", "model": "qwen3:8b" },
                                           { "id": "end", "kind": "End" }
                                         ],
                                         "edges": [
                                           { "sourceId": "start", "targetId": "agent-1" },
                                           { "sourceId": "agent-1", "targetId": "end" }
                                         ]
                                       }
                                       """;

    private const string PauseGraph = """
                                      {
                                        "startText": "Draft it, then let me sign off.",
                                        "nodes": [
                                          { "id": "start", "kind": "Start" },
                                          { "id": "draft", "kind": "Agent", "instructions": "Draft the note.", "model": "qwen3:8b" },
                                          { "id": "sign-off", "kind": "Pause", "label": "Sign off" },
                                          { "id": "end", "kind": "End" }
                                        ],
                                        "edges": [
                                          { "sourceId": "start", "targetId": "draft" },
                                          { "sourceId": "draft", "targetId": "sign-off" },
                                          { "sourceId": "sign-off", "targetId": "end" }
                                        ]
                                      }
                                      """;

    /// <summary>
    ///     The whole upgrade in one test: encrypted rows on the old schema, read back in plaintext, then the drop that
    ///     takes the table away. The candidate count is the assertion that catches a column silently failing to bind —
    ///     the reader aliases every column by hand because an unmapped-type query binds by property name.
    /// </summary>
    [Test]
    public async Task ReadAsync_OverEncryptedRows_AnswersEveryCanvasInPlaintextBeforeTheDropTakesTheTable()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-round-trip.sqlite", PreDropMigrationId);
        var first = await SeedAsync(probe, "Release notes", LinearGraph, createdAtUtc: 1);
        var second = await SeedAsync(probe, "Sign off", PauseGraph, createdAtUtc: 2);

        var (snapshot, logger) = await ReadAsync(probe);

        AssertEx.Equal(expected: 2, snapshot.Candidates.Count, "the candidate count is what catches a column that silently failed to bind.");
        AssertEx.Equal(expected: 0, snapshot.FailedCount);
        AssertEx.Equal("Release notes, Sign off", string.Join(", ", snapshot.Candidates.Select(static candidate => candidate.Name)),
            "ordered by creation, so ids and ordering stay natural.");
        AssertEx.Equal(first, snapshot.Candidates[0].Id);
        AssertEx.Equal(second, snapshot.Candidates[1].Id);
        AssertEx.True(snapshot.Candidates[0].GraphJson.Contains("Summarize it.", StringComparison.Ordinal),
            "the graph comes back decrypted, not as the stored ciphertext.");
        AssertEx.True(logger.HasEntry(LogLevel.Information, "2 saved workflow(s) read before migrations"));

        await probe.MigrateToAsync(targetMigration: null);

        AssertEx.False(await probe.TableExistsAsync("canvas_workflows"),
            "the read happens before migrations precisely because the drop that follows it is unconditional.");
    }

    /// <summary>
    ///     A node with neither legacy canvases nor pending recovery has nothing to import.
    /// </summary>
    [Test]
    public async Task ReadAsync_AtHeadWhereTheTableIsAlreadyDropped_AnswersAnEmptySnapshotWithoutThrowing()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-no-table.sqlite");

        AssertEx.False(await probe.TableExistsAsync("canvas_workflows"), "head is the state every start after the first sees.");

        var (snapshot, _) = await ReadAsync(probe);

        AssertEx.Empty(snapshot.Candidates);
        AssertEx.Equal(expected: 0, snapshot.FailedCount);
    }

    /// <summary>
    ///     An unreadable row stops the upgrade; neither its source nor the intact sibling may be discarded.
    /// </summary>
    [Test]
    public async Task ReadAsync_WithARowThatWillNotDecrypt_StopsBeforeMigrationAndRetainsCiphertext()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-damaged.sqlite", PreDropMigrationId);
        var damaged = await SeedAsync(probe, "Damaged", LinearGraph, createdAtUtc: 1);
        _ = await SeedAsync(probe, "Intact", PauseGraph, createdAtUtc: 2);
        await probe.ExecuteAsync("UPDATE canvas_workflows SET graph_json = $graph WHERE id = $id;", command =>
        {
            command.Parameters.AddWithValue("$graph", new byte[]
            {
                0x00,
                0x01,
                0x02
            });
            command.Parameters.AddWithValue("$id", damaged.ToString());
        });

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(async () => { _ = await ReadAsync(probe); });

        AssertEx.True(await probe.TableExistsAsync("canvas_workflows"));
        AssertEx.True(await probe.TableExistsAsync("canvas_workflow_import_recovery"));
        AssertEx.Equal(2L, await probe.ScalarAsync("SELECT COUNT(*) FROM canvas_workflow_import_recovery"));
        AssertEx.Equal(2L,
            await probe.ScalarAsync(
                "SELECT COUNT(*) FROM canvas_workflows AS source JOIN canvas_workflow_import_recovery AS recovery ON source.id = recovery.id WHERE source.graph_json = recovery.graph_json"));
    }

    /// <summary>
    ///     No cap and no option. A limit here would destroy every canvas past it as the import's NORMAL outcome, since
    ///     the drop migration runs in the same build whether the read took the row or not.
    /// </summary>
    [Test]
    public async Task ReadAsync_WithMoreRowsThanAnyReasonableCap_ReadsEveryOne()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-sixty.sqlite", PreDropMigrationId);
        for (var index = 0; index < 60; index++)
        {
            _ = await SeedAsync(probe, $"Canvas {index}", LinearGraph, index + 1);
        }

        var (snapshot, _) = await ReadAsync(probe);

        AssertEx.Equal(expected: 60, snapshot.Candidates.Count);
    }

    [Test]
    public async Task Restart_AfterSourceDrop_ImportsDurableCiphertextOnceAndRemovesRecovery()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-restart.sqlite", PreDropMigrationId);
        _ = await SeedAsync(probe, "Release notes", LinearGraph, createdAtUtc: 1);
        var ciphertext = (byte[])(await probe.ScalarAsync("SELECT graph_json FROM canvas_workflows"))!;
        _ = await ReadAsync(probe);
        await probe.MigrateToAsync(targetMigration: null);

        AssertEx.False(await probe.TableExistsAsync("canvas_workflows"));
        var stagedCiphertext = (byte[])(await probe.ScalarAsync("SELECT graph_json FROM canvas_workflow_import_recovery"))!;
        AssertEx.True(ciphertext.SequenceEqual(stagedCiphertext),
            "staging preserves the original authenticated ciphertext, never a plaintext export.");

        // Discard the pre-migration snapshot: a new context must recover solely from the database after restart.
        await ImportFromRecoveryAsync(probe);
        AssertEx.False(await probe.TableExistsAsync("canvas_workflow_import_recovery"));
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions"));
        await ImportFromRecoveryAsync(probe);
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions"));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Import_WhenSecondWriteFails_RollsBackFirstAndRetriesWithoutDuplicates(bool needsAttention)
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-retry.sqlite", PreDropMigrationId);
        _ = await SeedAsync(probe, "First", LinearGraph, createdAtUtc: 1);
        _ = await SeedAsync(probe, "Second", needsAttention ? PauseGraph.Replace("\"Agent\"", "\"Unknown\"", StringComparison.Ordinal) : PauseGraph, createdAtUtc: 2);
        _ = await ReadAsync(probe);
        await probe.MigrateToAsync(targetMigration: null);
        await probe.ExecuteAsync("CREATE TRIGGER reject_second_canvas BEFORE INSERT ON graph_workflow_definitions WHEN NEW.name = 'Second' BEGIN SELECT RAISE(ABORT, 'injected write failure'); END;");

        _ = await AssertEx.ThrowsAsync<InvalidOperationException>(() => ImportFromRecoveryAsync(probe));
        AssertEx.Equal(0L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions"));
        AssertEx.Equal(2L, await probe.ScalarAsync("SELECT COUNT(*) FROM canvas_workflow_import_recovery"));

        await probe.ExecuteAsync("DROP TRIGGER reject_second_canvas");
        await ImportFromRecoveryAsync(probe);
        AssertEx.Equal(2L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions"));
        AssertEx.False(await probe.TableExistsAsync("canvas_workflow_import_recovery"));
        await ImportFromRecoveryAsync(probe);
        AssertEx.Equal(2L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions"));
    }

    [Test]
    public async Task Read_WhenStagingCannotBeWritten_LeavesLegacySourceIntact()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-stage-failure.sqlite", PreDropMigrationId);
        _ = await SeedAsync(probe, "Release notes", LinearGraph, createdAtUtc: 1);
        // A schema collision makes SQLite refuse the durable copy before destructive migrations can run.
        await probe.ExecuteAsync("CREATE VIEW canvas_workflow_import_recovery AS SELECT * FROM canvas_workflows");
        _ = await AssertEx.ThrowsAsync<SqliteException>(async () => { _ = await ReadAsync(probe); });
        AssertEx.True(await probe.TableExistsAsync("canvas_workflows"));
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM canvas_workflows"));
    }

    [Test]
    public async Task Import_WithEmptyLegacyTable_CleansUpStagingAndStaysEmpty()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-empty.sqlite", PreDropMigrationId);
        _ = await ReadAsync(probe);
        AssertEx.True(await probe.TableExistsAsync("canvas_workflow_import_recovery"));
        await probe.MigrateToAsync(targetMigration: null);
        await ImportFromRecoveryAsync(probe);
        AssertEx.False(await probe.TableExistsAsync("canvas_workflow_import_recovery"));
        AssertEx.Equal(0L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions"));
    }

    [Test]
    public async Task Import_WhenRecoveryCleanupFails_RollsBackDefinitionsAndKeepsRetrySource()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-cleanup-failure.sqlite", PreDropMigrationId);
        _ = await SeedAsync(probe, "Release notes", LinearGraph, createdAtUtc: 1);
        _ = await ReadAsync(probe);
        await probe.MigrateToAsync(targetMigration: null);

        _ = await AssertEx.ThrowsAsync<IOException>(() => ImportFromRecoveryAsync(probe, new RejectRecoveryCleanup()));
        AssertEx.Equal(0L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions"));
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM canvas_workflow_import_recovery"));
        await ImportFromRecoveryAsync(probe);
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM graph_workflow_definitions"));
        AssertEx.False(await probe.TableExistsAsync("canvas_workflow_import_recovery"));
    }

    private sealed class RejectRecoveryCleanup : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText == "DROP TABLE IF EXISTS canvas_workflow_import_recovery")
            {
                throw new IOException("Injected recovery cleanup failure.");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task ImportFromRecoveryAsync(MigrationSchemaProbe probe, IInterceptor? interceptor = null)
    {
        using var keyHolder = new NullNodeSqliteKeyHolder();
        await using var context = interceptor is null
            ? AgentDefinitionTestContextFactory.Create(probe.DatabasePath, keyHolder)
            : AgentDefinitionTestContextFactory.Create(probe.DatabasePath, keyHolder, interceptor);
        var logger = new RecordingLogger();
        var snapshot = await CanvasWorkflowImport.ReadAsync(context, logger);
        var store = new GraphWorkflowStore(context, TimeProvider.System);
        var definitions = new GraphWorkflowDefinitionService(store,
            Substitute.For<IToolInvocationService>(),
            Substitute.For<ILocalModelProviderResolver>(),
            Options.Create(new GraphWorkflowOptions()));
        await CanvasWorkflowImport.ImportAsync(context, definitions, store, snapshot, logger);
    }

    /// <summary>
    ///     Writes one historical row the way the deleted save interceptor did: plaintext name, and <c>graph_json</c>
    ///     encrypted under AAD (empty conversation id, row id, <c>graph_json</c>). Building the ciphertext here rather
    ///     than through an entity is the only option left — the entity and its store are gone at head.
    /// </summary>
    private static async Task<Guid> SeedAsync(MigrationSchemaProbe probe, string name, string graphJson, long createdAtUtc)
    {
        var id = Guid.NewGuid();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        var encrypted = NodePayloadProtector.Encrypt(Encoding.UTF8.GetBytes(graphJson), keyHolder.Key.Span, Guid.Empty, id, "graph_json");

        await probe.ExecuteAsync("INSERT INTO canvas_workflows (id, name, graph_json, version, created_at_utc, updated_at_utc) "
                                 + "VALUES ($id, $name, $graph, 1, $created, $created);",
            command =>
            {
                command.Parameters.AddWithValue("$id", id.ToString());
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$graph", encrypted);
                command.Parameters.AddWithValue("$created", createdAtUtc.ToString(CultureInfo.InvariantCulture));
            });

        return id;
    }

    private static async Task<(CanvasWorkflowImportSnapshot Snapshot, RecordingLogger Logger)> ReadAsync(MigrationSchemaProbe probe)
    {
        var logger = new RecordingLogger();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        await using var dbContext = AgentDefinitionTestContextFactory.CreateForMigration(probe.DatabasePath, keyHolder);
        return (await CanvasWorkflowImport.ReadAsync(dbContext, logger), logger);
    }

    /// <summary>Records the rendered message of every entry, so the log assertions above can name what an operator reads.</summary>
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public bool HasEntry(LogLevel level, string fragment)
        {
            lock (_entries)
            {
                return _entries.Any(entry => entry.Level == level && entry.Message.Contains(fragment, StringComparison.Ordinal));
            }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
            NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) =>
            true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
