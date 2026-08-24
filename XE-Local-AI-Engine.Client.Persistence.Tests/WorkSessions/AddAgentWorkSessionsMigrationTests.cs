namespace XE_Local_AI_Engine.Client.Persistence.Tests.WorkSessions;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddAgentWorkSessionsMigrationTests
{
    private const string PreviousMigrationId = "20260822041045_AddMcpAgenticRunAuthority";
    private const string ThisMigrationId = "20260824151335_AddAgentWorkSessions";

    private static readonly string[] Tables =
    [
        "agent_work_sessions",
        "agent_work_session_tasks",
        "agent_work_session_findings",
        "agent_work_session_artifacts",
        "agent_work_session_checkpoints",
        "agent_work_session_events"
    ];

    [Test]
    public async Task Migrate_CreatesTheSixTablesWithTheirColumnsAndUniqueIndexes()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("agent-work-sessions.sqlite", PreviousMigrationId).ConfigureAwait(false);

        foreach (var table in Tables)
        {
            AssertEx.False(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must not exist before the migration.");
        }

        await probe.MigrateToAsync(ThisMigrationId).ConfigureAwait(false);

        foreach (var table in Tables)
        {
            AssertEx.True(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must exist after the migration.");
        }

        var sessionColumns = await probe.ColumnsAsync("agent_work_sessions").ConfigureAwait(false);
        foreach (var column in new[] { "id", "title", "objective", "kind", "agent_definition_id", "conversation_id", "status", "current_task_id", "step_count", "last_checkpoint_id", "last_sequence", "config_version", "created_at_utc", "updated_at_utc", "version" })
        {
            AssertEx.True(sessionColumns.Contains(column), $"agent_work_sessions must carry '{column}'.");
        }

        var eventColumns = await probe.ColumnsAsync("agent_work_session_events").ConfigureAwait(false);
        foreach (var column in new[] { "id", "session_id", "sequence", "step", "event_type", "detail_json", "operation_id", "outcome", "occurred_at_utc" })
        {
            AssertEx.True(eventColumns.Contains(column), $"agent_work_session_events must carry '{column}'.");
        }

        AssertEx.True(await probe.IndexExistsAsync("agent_work_sessions", "ux_agent_work_sessions_conversation_id", unique: true, "conversation_id").ConfigureAwait(false),
            "One session per conversation is a unique index, not a convention.");
        AssertEx.True(await probe.IndexExistsAsync("agent_work_session_events", "ux_agent_work_session_events_session_sequence", unique: true, "session_id", "sequence")
                                 .ConfigureAwait(false),
            "The event watermark must be unique per session.");
        AssertEx.True(await probe.IndexExistsAsync("agent_work_session_events", "ux_agent_work_session_events_operation", unique: true, "session_id", "operation_id")
                                 .ConfigureAwait(false),
            "One event per operation id is what makes a replayed step idempotent.");
        AssertEx.True(await probe.IndexExistsAsync("agent_work_session_artifacts", "ux_agent_work_session_artifacts_session_name", unique: true, "session_id", "name")
                                 .ConfigureAwait(false),
            "An artifact name is the replace key, so it must be unique per session.");
    }

    [Test]
    public async Task Rollback_DropsTheSixTablesAndLeavesTheRestOfTheSchemaIntact()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("agent-work-sessions-rollback.sqlite", ThisMigrationId).ConfigureAwait(false);

        await probe.MigrateToAsync(PreviousMigrationId).ConfigureAwait(false);

        foreach (var table in Tables)
        {
            AssertEx.False(await probe.TableExistsAsync(table).ConfigureAwait(false), $"{table} must be gone after the rollback.");
        }

        // Nothing else may go with them: the migration only ever created tables, so `Down` has nothing else to touch.
        AssertEx.True(await probe.TableExistsAsync("mcp_agent_runs").ConfigureAwait(false), "The rollback must not disturb neighbouring tables.");
        AssertEx.True(await probe.TableExistsAsync("development_projects").ConfigureAwait(false), "The rollback must not disturb the Development tables.");
        AssertEx.True(await probe.TableExistsAsync("development_events").ConfigureAwait(false), "The rollback must not disturb the Development event log.");
    }
}
