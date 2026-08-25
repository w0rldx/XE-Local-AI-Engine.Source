namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkProjectReasoningBudget</c> adds the one column a project needs to pin a thinking budget onto
///     every run it freezes. It must default to NULL — a project created before it existed pinned nothing, and
///     backfilling a number would silently cap the reasoning of every run it freezes from then on — and it must be
///     bounded inside the project's own context, because a budget at or above the window can never be honoured and
///     would masquerade as "no budget". The rollback test is the load-bearing one: SQLite drops a column by rebuilding
///     the table from this migration's target model, so a Down generated against a stale model deletes columns it
///     never mentions.
/// </summary>
public sealed class AddBenchmarkProjectReasoningBudgetMigrationTests
{
    private const string PreviousMigration = "20260824151335_AddAgentWorkSessions";
    private const string Column = "reasoning_budget_tokens";

    [Test]
    public async Task Migrate_ToLatest_AddsTheColumnWithNoBackfilledDefault()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-reasoning-budget.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("benchmark_projects").ConfigureAwait(false);

        AssertEx.True(columns.Contains(Column), $"benchmark_projects must record {Column}.");
        AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_projects", Column).ConfigureAwait(false),
            "A project created before the budget existed pinned nothing, and NULL is the only honest way to say so.");
    }

    [Test]
    public async Task ReasoningBudget_MustStayInsideTheProjectContext()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-reasoning-budget-constraint.sqlite").ConfigureAwait(false);

        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, reasoningBudgetTokens: 4096)).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, reasoningBudgetTokens: 0)).ConfigureAwait(false);

        await InsertProjectAsync(probe, reasoningBudgetTokens: 4095).ConfigureAwait(false);
        await InsertProjectAsync(probe, reasoningBudgetTokens: null).ConfigureAwait(false);
    }

    [Test]
    public async Task Migrate_Down_RemovesTheColumnAndKeepsTheRows()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-reasoning-budget-down.sqlite").ConfigureAwait(false);
        await InsertProjectAsync(probe, reasoningBudgetTokens: 2048).ConfigureAwait(false);

        await probe.MigrateToAsync(PreviousMigration).ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("benchmark_projects").ConfigureAwait(false);

        AssertEx.False(columns.Contains(Column), $"Down must drop benchmark_projects.{Column}.");
        AssertEx.True(columns.Contains("max_output_tokens"), "Down must not take the sibling budget column with it.");
        AssertEx.Equal(expected: 1L, await probe.ScalarAsync("SELECT COUNT(*) FROM benchmark_projects;").ConfigureAwait(false));
    }

    private static Task InsertProjectAsync(MigrationSchemaProbe probe, int? reasoningBudgetTokens) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_projects
                               (id, name, core_task_json, context_tokens, reasoning_budget_tokens,
                                agent_definition_id, version, created_at_utc, updated_at_utc)
                           VALUES ($id, 'p', X'7B7D', 4096, $budget, $agent, 1, 1, 1);
                           """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$agent", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$budget", reasoningBudgetTokens is null ? DBNull.Value : reasoningBudgetTokens.Value);
            });
}
