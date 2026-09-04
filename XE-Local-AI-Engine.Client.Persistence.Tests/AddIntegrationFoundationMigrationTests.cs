namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Schema and backfill coverage for <c>AddIntegrationFoundation</c>: the five integration tables, the four columns
///     the brief's own list does not name (<c>stop_requested_at_utc</c>, <c>output_bytes</c>, <c>principal_id</c> on
///     three tables) and the conversation-kind backfill that closes the work-session chat-list leak.
/// </summary>
public sealed class AddIntegrationFoundationMigrationTests
{
    private const string PreviousMigrationId = "20260902081629_AddDevWorkflowRuleSets";

    private const string BackfillSql = """
                                       UPDATE conversations
                                       SET kind = 'work-session'
                                       WHERE conversation_id IN (SELECT conversation_id FROM agent_work_sessions);
                                       """;

    [Test]
    public async Task MigrateToHead_CreatesEveryIntegrationTableWithItsDeclaredColumnsAndIndexes()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("integration-foundation.sqlite").ConfigureAwait(false);

        foreach (var table in new[]
                 {
                     "integration_triggers",
                     "integration_api_keys",
                     "integration_sessions",
                     "integration_executions",
                     "integration_execution_events"
                 })
        {
            AssertEx.True(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must exist after the migration chain.");
        }

        AssertEx.Equal("'chat'", await probe.ColumnDefaultAsync("conversations", "kind").ConfigureAwait(false),
            "A conversation with no explicit kind must read as an ordinary chat.");
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("integration_executions", "output_bytes").ConfigureAwait(false),
            "The plaintext output-byte tally starts at zero rather than NULL, because every write adds to it.");

        AssertEx.True((await probe.ColumnsAsync("integration_executions").ConfigureAwait(false)).Contains("stop_requested_at_utc"),
            "The durable cancel marker must exist, or the cancel path cannot survive a restart.");
        AssertEx.Equal(expected: 0L, await NotNullAsync(probe, "integration_executions", "stop_requested_at_utc").ConfigureAwait(false),
            "The cancel marker is nullable: an execution that was never cancelled carries no stamp.");

        foreach (var table in new[]
                 {
                     "integration_api_keys",
                     "integration_sessions",
                     "integration_executions"
                 })
        {
            AssertEx.True((await probe.ColumnsAsync(table).ConfigureAwait(false)).Contains("principal_id"), $"{table} must carry the ownership column.");
            AssertEx.Equal(expected: 1L, await NotNullAsync(probe, table, "principal_id").ConfigureAwait(false),
                $"{table}.principal_id is NOT NULL — no row can predate the column, because every table here is new in this migration.");
        }

        AssertEx.True(await probe.IndexExistsAsync("integration_triggers", "ux_integration_triggers_name", unique: true, "name").ConfigureAwait(false));
        AssertEx.True(await probe.IndexExistsAsync("integration_triggers", "ix_integration_triggers_enabled", unique: false, "enabled").ConfigureAwait(false));
        AssertEx.True(await probe.IndexExistsAsync("integration_api_keys", "ux_integration_api_keys_prefix", unique: true, "key_prefix").ConfigureAwait(false));
        AssertEx.True(await probe.IndexExistsAsync("integration_sessions", "ux_integration_sessions_conversation_id", unique: true, "conversation_id").ConfigureAwait(false));
        AssertEx.True(await probe.IndexExistsAsync("integration_sessions", "ix_integration_sessions_trigger", unique: false, "trigger_id").ConfigureAwait(false));
        AssertEx.True(await probe.IndexExistsAsync("integration_sessions", "ix_integration_sessions_status_activity", unique: false, "status", "last_activity_utc")
                                 .ConfigureAwait(false));
        AssertEx.True(await probe.IndexExistsAsync("integration_executions", "ix_integration_executions_session", unique: false, "session_id").ConfigureAwait(false));
        AssertEx.True(await probe.IndexExistsAsync("integration_executions", "ix_integration_executions_trigger", unique: false, "trigger_id").ConfigureAwait(false));
        AssertEx.True(await probe.IndexExistsAsync("integration_executions", "ix_integration_executions_status_received", unique: false, "status", "received_at_utc")
                                 .ConfigureAwait(false));
        AssertEx.True(await probe.IndexExistsAsync("integration_execution_events",
                                     "ux_integration_execution_events_execution_sequence",
                                     unique: true,
                                     "execution_id",
                                     "sequence")
                                 .ConfigureAwait(false));
        AssertEx.True(await probe.ForeignKeyExistsAsync("integration_execution_events", "execution_id", "integration_executions").ConfigureAwait(false),
            "Declared for parity with dev_workflow_run_events; decorative at runtime because the node connection leaves PRAGMA foreign_keys off.");
    }

    [Test]
    public async Task MigrateToHead_KeysRequestUniquenessOnThePrincipalAndNotGlobally()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("integration-request-uniqueness.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.IndexExistsAsync("integration_executions",
                                     "ux_integration_executions_principal_request",
                                     unique: true,
                                     "principal_id",
                                     "request_id")
                                 .ConfigureAwait(false),
            "principal_id must lead: the same index is the access path for the accept transaction's per-principal active count.");

        // A test that only checked for the new index would pass with BOTH present, and a surviving global unique index
        // would still let one integrator preclaim another's request id and force it a permanent 409.
        var globalUniqueOnRequestId = await probe.ScalarAsync("""
                                                              SELECT COUNT(*) FROM pragma_index_list('integration_executions') l
                                                              WHERE l."unique" = 1
                                                                AND (SELECT COUNT(*) FROM pragma_index_info(l.name)) = 1
                                                                AND (SELECT name FROM pragma_index_info(l.name)) = 'request_id';
                                                              """).ConfigureAwait(false);
        AssertEx.Equal(expected: 0L, Convert.ToInt64(globalUniqueOnRequestId, CultureInfo.InvariantCulture),
            "Ruling R4-6 REPLACED the global unique index on request_id; two principals may each hold the same request id.");
    }

    [Test]
    public async Task Migrate_BackfillsWorkSessionOwnedConversationsAndLeavesChatsAlone()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("integration-kind-backfill.sqlite", PreviousMigrationId).ConfigureAwait(false);

        const string OwnedConversationId = "1f1a0c66-0000-4000-8000-000000000001";
        const string PlainConversationId = "1f1a0c66-0000-4000-8000-000000000002";

        await probe.ExecuteAsync($"""
                                  INSERT INTO conversations (conversation_id, created_at_utc, last_seen_utc, purged)
                                  VALUES ('{OwnedConversationId}', 10, 10, 0), ('{PlainConversationId}', 20, 20, 0);
                                  """).ConfigureAwait(false);
        await probe.ExecuteAsync($"""
                                  INSERT INTO agent_work_sessions (
                                      id, title, objective, kind, agent_definition_id, conversation_id, status,
                                      step_count, last_sequence, config_version, created_at_utc, updated_at_utc, version)
                                  VALUES ('2f1a0c66-0000-4000-8000-000000000001', 'Seeded session', zeroblob(16), 'General',
                                          '3f1a0c66-0000-4000-8000-000000000001', '{OwnedConversationId}', 'Active', 0, 0, 1, 10, 10, 0);
                                  """).ConfigureAwait(false);

        await probe.MigrateToAsync(targetMigration: null).ConfigureAwait(false);

        AssertEx.Equal("work-session", await KindAsync(probe, OwnedConversationId).ConfigureAwait(false),
            "The transcript a work session owns must stop appearing in the chat list after the upgrade.");
        AssertEx.Equal("chat", await KindAsync(probe, PlainConversationId).ConfigureAwait(false),
            "An ordinary conversation must keep the column default.");

        // Idempotent by construction: re-running sets rows to a value they already hold.
        await probe.ExecuteAsync(BackfillSql).ConfigureAwait(false);
        AssertEx.Equal("work-session", await KindAsync(probe, OwnedConversationId).ConfigureAwait(false));
        AssertEx.Equal("chat", await KindAsync(probe, PlainConversationId).ConfigureAwait(false));
    }

    private static async Task<long> NotNullAsync(MigrationSchemaProbe probe, string table, string column)
    {
        var value = await probe.ScalarAsync($"SELECT \"notnull\" FROM pragma_table_info('{table}') WHERE name = '{column}';").ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string?> KindAsync(MigrationSchemaProbe probe, string conversationId)
    {
        var value = await probe.ScalarAsync($"SELECT kind FROM conversations WHERE conversation_id = '{conversationId}';").ConfigureAwait(false);
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
