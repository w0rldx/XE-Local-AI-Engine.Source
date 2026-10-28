namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkProjectReasoningBudget</c> adds a project's pinned thinking budget. It defaults to NULL (a
///     backfill would silently cap later runs) and stays below the context window, which no budget can fill.
/// </summary>
/// <remarks>
///     The rollback test is load-bearing: SQLite drops a column by rebuilding the table from this migration's target
///     model, so a Down generated against a stale model deletes columns it never mentions.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class AddBenchmarkProjectReasoningBudgetMigrationTests
{
    private const string PreviousMigration = "20260824151335_AddAgentWorkSessions";
    private const string Column = "reasoning_budget_tokens";

    [Test]
    public async Task Migrate_ToLatest_AddsTheColumnWithNoBackfilledDefault()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("benchmark-reasoning-budget.sqlite");

        var columns = await probe.ColumnsAsync("benchmark_projects");

        AssertEx.True(columns.Contains(Column), $"benchmark_projects must record {Column}.");
        AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_projects", Column),
            "A project created before the budget existed pinned nothing, and NULL is the only honest way to say so.");
    }

    [Test]
    public async Task ReasoningBudget_MustStayInsideTheProjectContext()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("benchmark-reasoning-budget-constraint.sqlite");

        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, reasoningBudgetTokens: 4096));
        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, reasoningBudgetTokens: 0));

        await InsertProjectAsync(probe, reasoningBudgetTokens: 4095);
        await InsertProjectAsync(probe, reasoningBudgetTokens: null);
    }

    [Test]
    public async Task Migrate_Down_RemovesTheColumnAndKeepsTheRows()
    {
        await using var probe = await MigrationSchemaProbe.FromChatTemplateAsync("benchmark-reasoning-budget-down.sqlite");
        await InsertProjectAsync(probe, reasoningBudgetTokens: 2048);

        await probe.MigrateToAsync(PreviousMigration);

        var columns = await probe.ColumnsAsync("benchmark_projects");

        AssertEx.False(columns.Contains(Column), $"Down must drop benchmark_projects.{Column}.");
        AssertEx.True(columns.Contains("max_output_tokens"), "Down must not take the sibling budget column with it.");
        AssertEx.Equal(expected: 1L, await probe.ScalarAsync("SELECT COUNT(*) FROM benchmark_projects;"));
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
