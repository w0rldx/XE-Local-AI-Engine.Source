namespace XE_Local_AI_Engine.Client.Persistence.Tests.GraphWorkflows;

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Import;

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
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-round-trip.sqlite", PreDropMigrationId).ConfigureAwait(false);
        var first = await SeedAsync(probe, "Release notes", LinearGraph, createdAtUtc: 1).ConfigureAwait(false);
        var second = await SeedAsync(probe, "Sign off", PauseGraph, createdAtUtc: 2).ConfigureAwait(false);

        var (snapshot, logger) = await ReadAsync(probe).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, snapshot.Candidates.Count, "the candidate count is what catches a column that silently failed to bind.");
        AssertEx.Equal(expected: 0, snapshot.FailedCount);
        AssertEx.Equal("Release notes, Sign off", string.Join(", ", snapshot.Candidates.Select(static candidate => candidate.Name)),
            "ordered by creation, so ids and ordering stay natural.");
        AssertEx.Equal(first, snapshot.Candidates[0].Id);
        AssertEx.Equal(second, snapshot.Candidates[1].Id);
        AssertEx.True(snapshot.Candidates[0].GraphJson.Contains("Summarize it.", StringComparison.Ordinal),
            "the graph comes back decrypted, not as the stored ciphertext.");
        AssertEx.True(logger.HasEntry(LogLevel.Information, "2 saved workflow(s) read before migrations"));

        await probe.MigrateToAsync(targetMigration: null).ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync("canvas_workflows").ConfigureAwait(false),
            "the read happens before migrations precisely because the drop that follows it is unconditional.");
    }

    /// <summary>
    ///     The idempotency mechanism, stated as a test: on every start after the first the table is gone, so the read
    ///     is a permanent no-op. No marker table, no flag column, nothing to keep honest.
    /// </summary>
    [Test]
    public async Task ReadAsync_AtHeadWhereTheTableIsAlreadyDropped_AnswersAnEmptySnapshotWithoutThrowing()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-no-table.sqlite").ConfigureAwait(false);

        AssertEx.False(await probe.TableExistsAsync("canvas_workflows").ConfigureAwait(false), "head is the state every start after the first sees.");

        var (snapshot, _) = await ReadAsync(probe).ConfigureAwait(false);

        AssertEx.Empty(snapshot.Candidates);
        AssertEx.Equal(expected: 0, snapshot.FailedCount);
    }

    /// <summary>
    ///     A blob that will not decrypt is the one thing this import genuinely loses, and it should be impossible: a
    ///     row written by the canvas endpoint decrypts under its own AAD. So it is counted and its id is named at
    ///     Error — the operator's only route back to it is the pre-migration backup — and the read carries on.
    /// </summary>
    [Test]
    public async Task ReadAsync_WithARowThatWillNotDecrypt_CountsItAndStillReturnsTheOthers()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-damaged.sqlite", PreDropMigrationId).ConfigureAwait(false);
        var damaged = await SeedAsync(probe, "Damaged", LinearGraph, createdAtUtc: 1).ConfigureAwait(false);
        _ = await SeedAsync(probe, "Intact", PauseGraph, createdAtUtc: 2).ConfigureAwait(false);
        await probe.ExecuteAsync("UPDATE canvas_workflows SET graph_json = $graph WHERE id = $id;", command =>
        {
            command.Parameters.AddWithValue("$graph", new byte[]
            {
                0x00,
                0x01,
                0x02
            });
            command.Parameters.AddWithValue("$id", damaged.ToString());
        }).ConfigureAwait(false);

        var (snapshot, logger) = await ReadAsync(probe).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, snapshot.FailedCount);
        AssertEx.Equal("Intact", string.Join(", ", snapshot.Candidates.Select(static candidate => candidate.Name)),
            "one damaged row never costs the operator the rest.");
        AssertEx.True(logger.HasEntry(LogLevel.Error, damaged.ToString()),
            "the id is named, because it is what an operator pulls from the backup.");
    }

    /// <summary>
    ///     No cap and no option. A limit here would destroy every canvas past it as the import's NORMAL outcome, since
    ///     the drop migration runs in the same build whether the read took the row or not.
    /// </summary>
    [Test]
    public async Task ReadAsync_WithMoreRowsThanAnyReasonableCap_ReadsEveryOne()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("canvas-import-sixty.sqlite", PreDropMigrationId).ConfigureAwait(false);
        for (var index = 0; index < 60; index++)
        {
            _ = await SeedAsync(probe, $"Canvas {index}", LinearGraph, index + 1).ConfigureAwait(false);
        }

        var (snapshot, _) = await ReadAsync(probe).ConfigureAwait(false);

        AssertEx.Equal(expected: 60, snapshot.Candidates.Count);
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
            }).ConfigureAwait(false);

        return id;
    }

    private static async Task<(CanvasWorkflowImportSnapshot Snapshot, RecordingLogger Logger)> ReadAsync(MigrationSchemaProbe probe)
    {
        var logger = new RecordingLogger();
        using var keyHolder = new NullNodeSqliteKeyHolder();
        await using var dbContext = AgentDefinitionTestContextFactory.CreateForMigration(probe.DatabasePath, keyHolder);
        return (await CanvasWorkflowImport.ReadAsync(dbContext, logger).ConfigureAwait(false), logger);
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
