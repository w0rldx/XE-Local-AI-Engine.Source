namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkRunStopReasonAndOutputBudget</c> adds two plaintext, nullable columns: why the primary generation
///     stopped, and the project's optional output budget. Both stay NULL on existing rows — a run frozen before this
///     migration must never be relabelled as having stopped for a reason nobody measured.
/// </summary>
public sealed class AddBenchmarkRunStopReasonAndOutputBudgetMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsTheStopReasonAndOutputBudgetColumns()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-stop-reason.sqlite").ConfigureAwait(false);

        var runColumns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        AssertEx.True(runColumns.Contains("primary_stop_reason"), "benchmark_runs must record why the primary generation stopped.");
        AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_runs", "primary_stop_reason").ConfigureAwait(false),
            "Rows frozen before the column existed must stay NULL rather than be backfilled with an invented reason.");

        var projectColumns = await probe.ColumnsAsync("benchmark_projects").ConfigureAwait(false);
        AssertEx.True(projectColumns.Contains("max_output_tokens"), "benchmark_projects must carry the optional output budget.");
        AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_projects", "max_output_tokens").ConfigureAwait(false),
            "An absent budget means context-limited, which is NULL — never a defaulted number.");
    }

    [Test]
    public async Task OutputBudget_MustStayInsideTheProjectContext()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-output-budget-constraint.sqlite").ConfigureAwait(false);

        // A budget at or above the window could never be honoured, so the database refuses it rather than letting it
        // masquerade as "no budget".
        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, maxOutputTokens: 4096))
                      .ConfigureAwait(false);
        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, maxOutputTokens: 0)).ConfigureAwait(false);

        await InsertProjectAsync(probe, maxOutputTokens: 4095).ConfigureAwait(false);
        await InsertProjectAsync(probe, maxOutputTokens: null).ConfigureAwait(false);
    }

    private static Task InsertProjectAsync(MigrationSchemaProbe probe, int? maxOutputTokens) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_projects
                               (id, name, core_task_json, context_tokens, max_output_tokens, agent_definition_id, version, created_at_utc, updated_at_utc)
                           VALUES ($id, 'p', X'7B7D', 4096, $budget, $agent, 1, 1, 1);
                           """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$agent", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$budget", maxOutputTokens is null ? DBNull.Value : maxOutputTokens.Value);
            });
}
