namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     Verifies the <c>AddBenchmarkRunReasoningBudget</c> migration: the budget backfills from each run's project, the
///     measurements start unknown, the CHECK holds, and a rollback keeps every row or refuses to lose a value.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class AddBenchmarkRunReasoningBudgetMigrationTests
{
    private const string MigrationId = "20261127160337_AddBenchmarkRunReasoningBudget";
    private const string PreviousMigrationId = "20261125213607_AddModelLabPlaygroundRuns";

    private static readonly Guid PinnedProjectId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UnpinnedProjectId = new("11111111-1111-1111-1111-111111111112");
    private static readonly Guid LegacyProjectId = new("11111111-1111-1111-1111-111111111113");
    private static readonly Guid PinnedRunId = new("22222222-2222-2222-2222-222222222221");
    private static readonly Guid UnpinnedRunId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid LegacyRunId = new("22222222-2222-2222-2222-222222222223");
    private static readonly Guid SweepLowRunId = new("22222222-2222-2222-2222-222222222224");
    private static readonly Guid SweepHighRunId = new("22222222-2222-2222-2222-222222222225");

    [Test]
    public async Task Migrate_FromPreviousMigration_BackfillsTheFrozenBudgetFromEachRunsProject()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("run-reasoning-backfill.sqlite", PreviousMigrationId);
        await SeedAsync(probe);

        await probe.MigrateToAsync(MigrationId);

        // Each run's flat budget equals the one its snapshot froze: 2048 for the pinned project, none for the unpinned one
        // and for the legacy snapshot that predates the member (its project could never have been pinned since).
        AssertEx.Equal<long?>(2048, await ColumnAsync(probe, PinnedRunId, "reasoning_budget_tokens"));
        AssertEx.Null(await ColumnAsync(probe, UnpinnedRunId, "reasoning_budget_tokens"));
        AssertEx.Null(await ColumnAsync(probe, LegacyRunId, "reasoning_budget_tokens"));
        foreach (var runId in new[] { PinnedRunId, UnpinnedRunId, LegacyRunId })
        {
            AssertEx.Null(await ColumnAsync(probe, runId, "reasoning_tokens"), "no run was measured before the column existed");
            AssertEx.Null(await ColumnAsync(probe, runId, "applied_reasoning_budget_tokens"));
        }

        var check = Convert.ToString(await probe.ScalarAsync("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'benchmark_runs';"),
            CultureInfo.InvariantCulture);
        AssertEx.True(AssertEx.NotNull(check).Contains("CK_benchmark_runs_reasoning_budget_tokens", StringComparison.Ordinal), "the CHECK is part of the rebuilt table");
        _ = await AssertEx.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
            probe.ExecuteAsync("UPDATE benchmark_runs SET reasoning_budget_tokens = 0 WHERE id = $id;",
                command => command.Parameters.AddWithValue("$id", PinnedRunId)));
    }

    [Test]
    public async Task Migrate_WhenRolledBackOneStep_DropsOnlyTheColumnsAndKeepsEveryRow()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("run-reasoning-rollback.sqlite", PreviousMigrationId);
        await SeedAsync(probe);
        await probe.MigrateToAsync(MigrationId);

        await probe.MigrateToAsync(PreviousMigrationId);

        var columns = await probe.ColumnsAsync("benchmark_runs");
        AssertEx.False(columns.Contains("reasoning_budget_tokens") || columns.Contains("reasoning_tokens") || columns.Contains("applied_reasoning_budget_tokens"),
            "rolling back must drop all three columns");

        // SQLite rebuilds the table to drop a column: pin the rows and the neighbors it must keep.
        AssertEx.True(columns.Contains("agent_definition_id") && columns.Contains("tool_call_count") && columns.Contains("cell_key"));
        var rows = await probe.LongsAsync("SELECT COUNT(*) FROM benchmark_runs;");
        AssertEx.Equal(3L, rows[0], "populated rows survive the rollback");
    }

    [Test]
    [Arguments("override")]
    [Arguments("measured")]
    public async Task Migrate_WhenARunHoldsWhatTheOlderSchemaCannot_RefusesTheRollbackAndKeepsEveryValue(string kind)
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync($"run-reasoning-rollback-{kind}.sqlite", PreviousMigrationId);
        await SeedAsync(probe);
        await probe.MigrateToAsync(MigrationId);
        if (kind == "override")
        {
            // Two sweep runs on the unpinned project: a later Up would backfill both as unpinned.
            await probe.ExecuteAsync("""
                                     INSERT INTO benchmark_runs (id, project_id, runtime_snapshot_json, primary_model_name, model_content_fingerprint,
                                                                 agent_name, agent_version, agent_definition_id, requested_context_tokens, primary_status,
                                                                 last_stream_sequence, is_warmup, version, created_at_utc, updated_at_utc, cell_key, reasoning_budget_tokens)
                                     VALUES ($low, $p, x'7b7d', 'probe-model', 'v1:0', 'agent', 1, $a, 8192, 'Succeeded', 0, 0, 3, 1, 1, 'run:low', 256),
                                            ($high, $p, x'7b7d', 'probe-model', 'v1:0', 'agent', 1, $a, 8192, 'Succeeded', 0, 0, 3, 1, 1, 'run:high', 1024);
                                     """, command =>
            {
                command.Parameters.AddWithValue("$low", SweepLowRunId);
                command.Parameters.AddWithValue("$high", SweepHighRunId);
                command.Parameters.AddWithValue("$p", UnpinnedProjectId);
                command.Parameters.AddWithValue("$a", new Guid("33333333-3333-3333-3333-333333333333"));
            });
        }
        else
        {
            await probe.ExecuteAsync("UPDATE benchmark_runs SET reasoning_tokens = 300 WHERE id = $id;",
                command => command.Parameters.AddWithValue("$id", PinnedRunId));
        }

        var refusal = await AssertEx.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => probe.MigrateToAsync(PreviousMigrationId));
        AssertEx.True(refusal.Message.Contains("Cannot roll back AddBenchmarkRunReasoningBudget", StringComparison.Ordinal), refusal.Message);

        // Still at the migration, so re-upgrading is a no-op and no backfill can relabel a run.
        await probe.MigrateToAsync(MigrationId);
        AssertEx.True((await probe.ColumnsAsync("benchmark_runs")).Contains("reasoning_budget_tokens"), "the refused rollback dropped nothing");
        if (kind == "override")
        {
            AssertEx.Equal<long?>(256, await ColumnAsync(probe, SweepLowRunId, "reasoning_budget_tokens"));
            AssertEx.Equal<long?>(1024, await ColumnAsync(probe, SweepHighRunId, "reasoning_budget_tokens"));
        }
        else
        {
            AssertEx.Equal<long?>(300, await ColumnAsync(probe, PinnedRunId, "reasoning_tokens"));
        }
    }

    private static async Task<long?> ColumnAsync(MigrationSchemaProbe probe, Guid runId, string column)
    {
        var value = await probe.ScalarAsync($"SELECT {column} FROM benchmark_runs WHERE id = $id;",
            command => command.Parameters.AddWithValue("$id", runId));
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>A pinned, an unpinned and a legacy project with one run each, written as SQL at the previous schema.</summary>
    private static async Task SeedAsync(MigrationSchemaProbe probe)
    {
        await probe.ExecuteAsync("""
                                 INSERT INTO benchmark_projects (id, name, core_task_json, context_tokens, agent_definition_id, reasoning_budget_tokens, version, created_at_utc, updated_at_utc)
                                 VALUES ($p1, 'pinned', x'0badc0de', 8192, $a, 2048, 1, 1, 1),
                                        ($p2, 'unpinned', x'0badc0de', 8192, $a, NULL, 1, 1, 1),
                                        ($p3, 'legacy', x'0badc0de', 8192, $a, NULL, 1, 1, 1);
                                 INSERT INTO benchmark_runs (id, project_id, runtime_snapshot_json, primary_model_name, model_content_fingerprint,
                                                             agent_name, agent_version, agent_definition_id, requested_context_tokens, primary_status,
                                                             last_stream_sequence, is_warmup, version, created_at_utc, updated_at_utc, cell_key)
                                 VALUES ($r1, $p1, $s1, 'probe-model', 'v1:0', 'agent', 1, $a, 8192, 'Succeeded', 0, 0, 3, 1, 1, 'run:1'),
                                        ($r2, $p2, $s2, 'probe-model', 'v1:0', 'agent', 1, $a, 8192, 'Succeeded', 0, 0, 3, 1, 1, 'run:2'),
                                        ($r3, $p3, $s3, 'probe-model', 'v1:0', 'agent', 1, $a, 8192, 'Succeeded', 0, 0, 3, 1, 1, 'run:3');
                                 """, command =>
        {
            command.Parameters.AddWithValue("$p1", PinnedProjectId);
            command.Parameters.AddWithValue("$p2", UnpinnedProjectId);
            command.Parameters.AddWithValue("$p3", LegacyProjectId);
            command.Parameters.AddWithValue("$a", new Guid("33333333-3333-3333-3333-333333333333"));
            command.Parameters.AddWithValue("$r1", PinnedRunId);
            command.Parameters.AddWithValue("$r2", UnpinnedRunId);
            command.Parameters.AddWithValue("$r3", LegacyRunId);
            // The snapshot sampling each run froze; the migration never reads it, the assertions mirror it.
            command.Parameters.AddWithValue("$s1", Encoding.UTF8.GetBytes("""{"primarySampling":{"reasoningBudgetTokens":2048,"reasoningBudgetEnforceable":true}}"""));
            command.Parameters.AddWithValue("$s2", Encoding.UTF8.GetBytes("""{"primarySampling":{}}"""));
            command.Parameters.AddWithValue("$s3", Encoding.UTF8.GetBytes("""{"primarySampling":{"seedPolicy":"fixed"}}"""));
        });
    }
}
