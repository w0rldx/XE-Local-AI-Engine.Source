namespace XE_Local_AI_Engine.Client.Persistence.Tests.Integrations;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddIntegrationGraphTarget</c>: nullable agent ids, the trigger's graph id under a one-target CHECK and the run id.
///     SQLite rebuilds both tables, so agent rows must survive both directions.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddIntegrationGraphTargetMigrationTests
{
    private const string PreviousMigrationId = "20261015113203_AddGraphWorkflowRunOrigin";
    private const string ThisMigrationId = "20261015121011_AddIntegrationGraphTarget";

    [Test]
    public async Task Migrate_KeepsAgentTriggersAndSessions_AndEnforcesExactlyOneTarget()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("integration-graph-target-up.sqlite", PreviousMigrationId);
        var agentId = NewId();
        var triggerId = await SeedAgentTriggerAsync(probe, "sensor-ingest", agentId);
        await SeedSessionAsync(probe, triggerId, agentId);

        await probe.MigrateToAsync(ThisMigrationId);

        AssertEx.True((await probe.ColumnsAsync("integration_triggers")).Contains("target_graph_workflow_definition_id"));
        AssertEx.True((await probe.ColumnsAsync("integration_executions")).Contains("graph_workflow_run_id"));
        AssertEx.Equal(1L, await probe.ScalarAsync(
                "SELECT COUNT(*) FROM integration_triggers WHERE id = $id AND target_kind = 'Agent' AND target_agent_definition_id = $agent AND target_graph_workflow_definition_id IS NULL;",
                command =>
                {
                    command.Parameters.AddWithValue("$id", triggerId);
                    command.Parameters.AddWithValue("$agent", agentId);
                }),
            "the agent trigger survives the table rebuild with its target intact.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM integration_sessions WHERE agent_definition_id = $agent;",
            command => command.Parameters.AddWithValue("$agent", agentId)), "the session survives with its agent.");

        await InsertTriggerAsync(probe, "lights-on", "Graph", agentId: null, graphId: NewId());
        await AssertRefusedAsync(() => InsertTriggerAsync(probe, "both-ids", "Graph", NewId(), NewId()), "a trigger naming both targets");
        await AssertRefusedAsync(() => InsertTriggerAsync(probe, "no-ids", "Graph", agentId: null, graphId: null), "a trigger naming no target");
        await AssertRefusedAsync(() => InsertTriggerAsync(probe, "wrong-kind", "Agent", agentId: null, NewId()), "an agent-kind trigger naming only a graph");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_KeepsAgentRows_AndDropsGraphTriggers()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("integration-graph-target-rollback.sqlite", ThisMigrationId);
        var agentId = NewId();
        var triggerId = await SeedAgentTriggerAsync(probe, "sensor-ingest", agentId);
        await SeedSessionAsync(probe, triggerId, agentId);
        await InsertTriggerAsync(probe, "lights-on", "Graph", agentId: null, graphId: NewId());
        await SeedSessionAsync(probe, triggerId, agentId: null);

        await probe.MigrateToAsync(PreviousMigrationId);

        AssertEx.False((await probe.ColumnsAsync("integration_triggers")).Contains("target_graph_workflow_definition_id"), "Down drops the graph id.");
        AssertEx.False((await probe.ColumnsAsync("integration_executions")).Contains("graph_workflow_run_id"), "Down drops the run id.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM integration_triggers;"), "only the agent trigger survives: the old schema cannot express a graph target.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM integration_triggers WHERE id = $id AND target_agent_definition_id = $agent;",
            command =>
            {
                command.Parameters.AddWithValue("$id", triggerId);
                command.Parameters.AddWithValue("$agent", agentId);
            }), "the agent trigger keeps its target through the rebuild.");
        AssertEx.Equal(1L, await probe.ScalarAsync("SELECT COUNT(*) FROM integration_sessions WHERE agent_definition_id = $agent;",
            command => command.Parameters.AddWithValue("$agent", agentId)), "the session keeps its agent through the rebuild.");
        AssertEx.Equal(2L, await probe.ScalarAsync("SELECT COUNT(*) FROM integration_sessions;"), "a graph session survives too, its agent column filled with the empty id.");
    }

    private static string NewId() =>
        Guid.NewGuid().ToString().ToUpperInvariant();

    private static async Task<string> SeedAgentTriggerAsync(MigrationSchemaProbe probe, string name, string agentId)
    {
        var id = NewId();
        await probe.ExecuteAsync("""
                                 INSERT INTO integration_triggers (id, name, display_name, enabled, target_kind, target_agent_definition_id, session_policy,
                                                                   accepted_input_kinds, created_at_utc, updated_at_utc, version)
                                 VALUES ($id, $name, 'Sensor ingest', 1, 'Agent', $agent, 'PerInvocation', 1, 0, 0, 1);
                                 """,
            command =>
            {
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$agent", agentId);
            });
        return id;
    }

    private static Task InsertTriggerAsync(MigrationSchemaProbe probe, string name, string kind, string? agentId, string? graphId) =>
        probe.ExecuteAsync("""
                           INSERT INTO integration_triggers (id, name, display_name, enabled, target_kind, target_agent_definition_id,
                                                             target_graph_workflow_definition_id, session_policy, accepted_input_kinds, created_at_utc, updated_at_utc, version)
                           VALUES ($id, $name, 'Probe', 1, $kind, $agent, $graph, 'PerInvocation', 1, 0, 0, 1);
                           """,
            command =>
            {
                command.Parameters.AddWithValue("$id", NewId());
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$kind", kind);
                command.Parameters.AddWithValue("$agent", (object?)agentId ?? DBNull.Value);
                command.Parameters.AddWithValue("$graph", (object?)graphId ?? DBNull.Value);
            });

    private static Task SeedSessionAsync(MigrationSchemaProbe probe, string triggerId, string? agentId) =>
        probe.ExecuteAsync("""
                           INSERT INTO integration_sessions (id, trigger_id, principal_id, conversation_id, agent_definition_id, status, created_at_utc,
                                                             last_activity_utc, execution_count, last_sequence)
                           VALUES ($id, $trigger, $principal, $conversation, $agent, 'Active', 0, 0, 1, 1);
                           """,
            command =>
            {
                command.Parameters.AddWithValue("$id", NewId());
                command.Parameters.AddWithValue("$trigger", triggerId);
                command.Parameters.AddWithValue("$principal", NewId());
                command.Parameters.AddWithValue("$conversation", NewId());
                command.Parameters.AddWithValue("$agent", (object?)agentId ?? DBNull.Value);
            });

    private static async Task AssertRefusedAsync(Func<Task> insert, string what)
    {
        var refused = await AssertEx.ThrowsAsync<SqliteException>(insert);
        AssertEx.Equal(275, refused.SqliteExtendedErrorCode, $"{what} must be refused by the one-target CHECK constraint.");
    }
}
