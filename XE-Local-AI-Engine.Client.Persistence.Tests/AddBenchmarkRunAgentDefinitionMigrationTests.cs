namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>AddBenchmarkRunAgentDefinition</c> migration: every existing run is backfilled with its project's
///     agent definition before the NOT NULL column is relied on, a fresh migrate-to-head carries it, and a one-step
///     rollback drops only it.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddBenchmarkRunAgentDefinitionMigrationTests
{
    private const string Column = "agent_definition_id";
    private const string PreviousMigrationId = "20261021191127_AddRunEnvelopeExecutionParent";

    private static readonly Guid FirstProjectId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondProjectId = new("11111111-1111-1111-1111-111111111112");
    private static readonly Guid FirstAgentId = new("33333333-3333-3333-3333-333333333331");
    private static readonly Guid SecondAgentId = new("33333333-3333-3333-3333-333333333332");
    private static readonly Guid FirstRunId = new("22222222-2222-2222-2222-222222222221");
    private static readonly Guid SecondRunId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ThirdRunId = new("22222222-2222-2222-2222-222222222223");

    [Test]
    public async Task Migrate_FromPreviousMigration_BackfillsEveryRunWithItsProjectsAgent()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("run-agent-backfill.sqlite", PreviousMigrationId);
        AssertEx.False((await probe.ColumnsAsync("benchmark_runs")).Contains(Column), "benchmark_runs must not carry agent_definition_id before this migration.");
        await SeedAsync(probe);

        await probe.MigrateToAsync(targetMigration: null);

        AssertEx.Equal(FirstAgentId, await RunAgentAsync(probe, FirstRunId));
        AssertEx.Equal(FirstAgentId, await RunAgentAsync(probe, SecondRunId));
        AssertEx.Equal(SecondAgentId, await RunAgentAsync(probe, ThirdRunId), "Each run takes ITS project's agent, not the first project's.");
        var unstamped = await probe.LongsAsync("SELECT COUNT(*) FROM benchmark_runs WHERE agent_definition_id IS NULL OR agent_definition_id = '00000000-0000-0000-0000-000000000000';");
        AssertEx.True(unstamped[0] == 0, "No run may leave this migration without an agent definition.");
        var notNull = await probe.LongsAsync("SELECT \"notnull\" FROM pragma_table_info('benchmark_runs') WHERE name = 'agent_definition_id';");
        AssertEx.True(notNull.Single() == 1, "The column is NOT NULL: every run names the agent it was frozen against.");
    }

    [Test]
    public async Task MigrateToHead_OnAFreshDatabase_HasTheColumn()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("run-agent-fresh.sqlite");

        AssertEx.True((await probe.ColumnsAsync("benchmark_runs")).Contains(Column), "A fresh migrate-to-head should expose agent_definition_id.");
    }

    [Test]
    public async Task Migrate_WhenRolledBackOneStep_DropsOnlyTheColumn()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("run-agent-rollback.sqlite");

        await probe.MigrateToAsync(PreviousMigrationId);

        var columns = await probe.ColumnsAsync("benchmark_runs");
        AssertEx.False(columns.Contains(Column), "Rolling back one migration should drop agent_definition_id.");

        // SQLite rebuilds the table to drop a column, from the PRECEDING migration's model: pin the neighbours it must keep.
        AssertEx.True(columns.Contains("agent_version"), "Rolling back must not disturb the run's other agent column.");
        AssertEx.True(columns.Contains("cell_key"), "Rolling back must not disturb the cell identity stamp.");
    }

    private static async Task<Guid> RunAgentAsync(MigrationSchemaProbe probe, Guid runId)
    {
        var value = await probe.ScalarAsync("SELECT agent_definition_id FROM benchmark_runs WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", runId));
        return Guid.Parse(AssertEx.NotNull(Convert.ToString(value, CultureInfo.InvariantCulture), "The probed run must exist."));
    }

    /// <summary>Two projects with different agents and three runs, written as SQL because the entity model describes the schema at head.</summary>
    private static async Task SeedAsync(MigrationSchemaProbe probe)
    {
        await probe.ExecuteAsync("""
                                 INSERT INTO benchmark_projects (id, name, core_task_json, context_tokens, agent_definition_id, version, created_at_utc, updated_at_utc)
                                 VALUES ($p1, 'agent-probe-1', x'0badc0de', 4096, $a1, 1, 1, 1),
                                        ($p2, 'agent-probe-2', x'0badc0de', 4096, $a2, 1, 1, 1);
                                 INSERT INTO benchmark_runs (id, project_id, runtime_snapshot_json, primary_model_name, model_content_fingerprint,
                                                             agent_name, agent_version, requested_context_tokens, primary_status, last_stream_sequence,
                                                             is_warmup, version, created_at_utc, updated_at_utc, cell_key)
                                 VALUES ($r1, $p1, x'0badbeef', 'probe-model', 'v1:0', 'probe-agent', 1, 4096, 'Succeeded', 0, 0, 3, 1, 1, 'run:1'),
                                        ($r2, $p1, x'0badbeef', 'probe-model', 'v1:0', 'probe-agent', 1, 4096, 'Succeeded', 0, 0, 3, 1, 1, 'run:2'),
                                        ($r3, $p2, x'0badbeef', 'probe-model', 'v1:0', 'other-agent', 2, 4096, 'Succeeded', 0, 0, 3, 1, 1, 'run:3');
                                 """, command =>
        {
            command.Parameters.AddWithValue("$p1", FirstProjectId);
            command.Parameters.AddWithValue("$p2", SecondProjectId);
            command.Parameters.AddWithValue("$a1", FirstAgentId);
            command.Parameters.AddWithValue("$a2", SecondAgentId);
            command.Parameters.AddWithValue("$r1", FirstRunId);
            command.Parameters.AddWithValue("$r2", SecondRunId);
            command.Parameters.AddWithValue("$r3", ThirdRunId);
        });
    }
}
