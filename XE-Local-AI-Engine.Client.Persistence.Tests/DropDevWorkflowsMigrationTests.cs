namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>DropDevWorkflows</c> over a database that still holds Dev Workflow rows and a Workflow-kind work session: the
///     nine tables go, the Workflow session goes with its conversation and children, and every other session stays.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class DropDevWorkflowsMigrationTests
{
    private const string PreviousMigrationId = "20261112183946_AddGraphWorkflowNodeRunDevelopmentTask";
    private const string ThisMigrationId = "20261113020150_DropDevWorkflows";

    private const string WorkflowSessionId = "4a1a0c66-0000-4000-8000-000000000001";
    private const string WorkflowConversationId = "4a1a0c66-0000-4000-8000-000000000002";
    private const string GeneralSessionId = "4a1a0c66-0000-4000-8000-000000000003";
    private const string GeneralConversationId = "4a1a0c66-0000-4000-8000-000000000004";
    private const string WorkflowRunId = "4a1a0c66-0000-4000-8000-000000000005";
    private const string GeneralRunId = "4a1a0c66-0000-4000-8000-000000000006";

    private static readonly string[] DroppedTables =
    [
        "dev_workflow_artifact_uses",
        "dev_workflow_artifacts",
        "dev_workflow_decisions",
        "dev_workflow_definitions",
        "dev_workflow_node_runs",
        "dev_workflow_rule_sets",
        "dev_workflow_run_events",
        "dev_workflow_runs",
        "dev_workflow_work_items"
    ];

    [Test]
    public async Task Migrate_DropsTheDevWorkflowTablesAndTheWorkflowSessions_AndKeepsEveryOtherSession()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("drop-dev-workflows.sqlite", PreviousMigrationId);
        await SeedSessionAsync(probe, WorkflowSessionId, WorkflowConversationId, "Workflow");
        await SeedSessionAsync(probe, GeneralSessionId, GeneralConversationId, "General");
        await probe.ExecuteAsync("""
                                 INSERT INTO dev_workflow_work_items (id, title, request, status, created_at_utc, updated_at_utc, version)
                                 VALUES ('5a1a0c66-0000-4000-8000-000000000001', 'Ship it', zeroblob(16), 'Active', 10, 10, 0);
                                 INSERT INTO dev_workflow_runs (id, work_item_id, definition_id, definition_version, definition_graph_hash, graph_json,
                                                                graph_revision, status, last_sequence, created_at_utc, updated_at_utc, version)
                                 VALUES ('5a1a0c66-0000-4000-8000-000000000002', '5a1a0c66-0000-4000-8000-000000000001',
                                         '5a1a0c66-0000-4000-8000-000000000003', 1, 'hash', zeroblob(16), 1, 'Running', 0, 10, 10, 0);
                                 """);

        await probe.MigrateToAsync(targetMigration: null);

        foreach (var table in DroppedTables)
        {
            AssertEx.False(await probe.TableExistsAsync(table), $"{table} must be gone after the migration.");
        }

        AssertEx.Equal(0L, await CountAsync(probe, $"agent_work_sessions WHERE id = '{WorkflowSessionId}'"), "the Workflow-kind session is deleted.");
        AssertEx.Equal(0L, await CountAsync(probe, $"agent_work_session_tasks WHERE session_id = '{WorkflowSessionId}'"));
        AssertEx.Equal(0L, await CountAsync(probe, $"agent_work_session_findings WHERE session_id = '{WorkflowSessionId}'"));
        AssertEx.Equal(0L, await CountAsync(probe, $"conversations WHERE conversation_id = '{WorkflowConversationId}'"), "its conversation goes with it.");
        AssertEx.Equal(1L, await CountAsync(probe, $"agent_work_sessions WHERE id = '{GeneralSessionId}'"), "every other kind of session stays.");
        AssertEx.Equal(1L, await CountAsync(probe, $"agent_work_session_findings WHERE session_id = '{GeneralSessionId}'"));
        AssertEx.Equal(1L, await CountAsync(probe, $"conversations WHERE conversation_id = '{GeneralConversationId}'"));
    }

    [Test]
    public async Task Migrate_DeletesTheWorkflowConversationsTranscript_AndUnbindsItsGraphRun()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("drop-dev-workflows-footprint.sqlite", PreviousMigrationId);
        await SeedSessionAsync(probe, WorkflowSessionId, WorkflowConversationId, "Workflow");
        await SeedSessionAsync(probe, GeneralSessionId, GeneralConversationId, "General");
        await SeedTranscriptAsync(probe, WorkflowConversationId, WorkflowRunId);
        await SeedTranscriptAsync(probe, GeneralConversationId, GeneralRunId);

        await probe.MigrateToAsync(targetMigration: null);

        AssertEx.Equal(0L, await CountAsync(probe, $"messages WHERE conversation_id = '{WorkflowConversationId}'"), "the Workflow conversation's messages go with it.");
        AssertEx.Equal(0L, await CountAsync(probe, $"tool_events WHERE conversation_id = '{WorkflowConversationId}'"), "and its tool events.");
        AssertEx.Null(await probe.ScalarAsync($"SELECT conversation_id FROM graph_workflow_runs WHERE id = '{WorkflowRunId}';"),
            "a Graph run bound to the deleted conversation survives, unbound.");
        AssertEx.Equal(1L, await CountAsync(probe, $"graph_workflow_runs WHERE id = '{WorkflowRunId}'"));
        AssertEx.Equal(1L, await CountAsync(probe, $"messages WHERE conversation_id = '{GeneralConversationId}'"), "another session's transcript stays.");
        AssertEx.Equal(1L, await CountAsync(probe, $"tool_events WHERE conversation_id = '{GeneralConversationId}'"));
        AssertEx.Equal(GeneralConversationId,
            await probe.ScalarAsync($"SELECT conversation_id FROM graph_workflow_runs WHERE id = '{GeneralRunId}';") as string,
            "a run bound to a surviving conversation keeps its binding.");
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_RecreatesTheNineTables()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("drop-dev-workflows-rollback.sqlite", ThisMigrationId);

        await probe.MigrateToAsync(PreviousMigrationId);

        foreach (var table in DroppedTables)
        {
            AssertEx.True(await probe.TableExistsAsync(table), $"Down must recreate {table}.");
        }

        AssertEx.True(await probe.ForeignKeyExistsAsync("dev_workflow_runs", "work_item_id", "dev_workflow_work_items"), "and the foreign keys between them.");
    }

    /// <summary>One message and one tool event on the conversation, and a finished Graph run bound to it.</summary>
    private static async Task SeedTranscriptAsync(MigrationSchemaProbe probe, string conversationId, string runId)
    {
        await probe.ExecuteAsync($"""
                                  INSERT INTO messages (message_id, conversation_id, sequence, role, content, created_at_utc, updated_at_utc)
                                  VALUES ('{Guid.NewGuid():D}', '{conversationId}', 0, 'assistant', 'answer', 10, 10);
                                  INSERT INTO tool_events (tool_call_id, conversation_id, tool_name, status, created_at_utc)
                                  VALUES ('{Guid.NewGuid():D}', '{conversationId}', 'list_files', 'completed', 10);
                                  INSERT INTO graph_workflow_runs (id, request_id, definition_id, definition_version, graph_hash, status, failure_class, graph_json, seq, version,
                                                                   created_at_utc, conversation_id)
                                  VALUES ('{runId}', '{Guid.NewGuid():D}', '{Guid.NewGuid():D}', 1, 'hash', 'Completed', 'None', zeroblob(8), 0, 1, 10, '{conversationId}');
                                  """);
    }

    /// <summary>A session with one task and a finding that points at it, the RESTRICT edge a careless delete order trips.</summary>
    private static async Task SeedSessionAsync(MigrationSchemaProbe probe, string sessionId, string conversationId, string kind)
    {
        var taskId = Guid.NewGuid().ToString("D");
        await probe.ExecuteAsync($"""
                                  INSERT INTO conversations (conversation_id, created_at_utc, last_seen_utc, purged) VALUES ('{conversationId}', 10, 10, 0);
                                  INSERT INTO agent_work_sessions (id, title, objective, kind, agent_definition_id, conversation_id, status,
                                                                   step_count, last_sequence, config_version, created_at_utc, updated_at_utc, version)
                                  VALUES ('{sessionId}', 'Seeded session', zeroblob(16), '{kind}', '{Guid.NewGuid():D}', '{conversationId}', 'Completed', 0, 0, 1, 10, 10, 0);
                                  INSERT INTO agent_work_session_tasks (id, session_id, sequence, title, status, origin, created_step, updated_step)
                                  VALUES ('{taskId}', '{sessionId}', 1, zeroblob(16), 'Done', 'Agent', 1, 1);
                                  INSERT INTO agent_work_session_findings (id, session_id, task_id, sequence, kind, text, created_step, superseded)
                                  VALUES ('{Guid.NewGuid():D}', '{sessionId}', '{taskId}', 2, 'Finding', zeroblob(16), 1, 0);
                                  """);
    }

    private static async Task<long> CountAsync(MigrationSchemaProbe probe, string fromWhere) =>
        Convert.ToInt64(await probe.ScalarAsync($"SELECT COUNT(*) FROM {fromWhere};"), CultureInfo.InvariantCulture);
}
