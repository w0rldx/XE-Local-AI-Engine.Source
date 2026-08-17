namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkReadiness</c> is the one migration the benchmark-readiness work landed as, after five separate
///     ones had to be regenerated at the end of the merged chain. It adds the stop reason, the prompt/generation
///     throughput split, the repeat group and the two generation budgets. Every column must default to NULL except
///     <c>is_warmup</c>, which defaults to false — a run frozen before any of this existed was a single, non-warm-up
///     run with no measured split, and backfilling it with a zero would stand an invented measurement next to a real
///     one in the same table. The rollback test is the one that matters most: SQLite drops a column by rebuilding the
///     table, so a Down written against a stale target model silently deletes columns it never mentioned.
/// </summary>
public sealed class AddBenchmarkReadinessMigrationTests
{
    private const string PreviousMigration = "20260817140837_AddTrainingArtifactDiscardCleanupState";

    private static readonly string[] RunColumns =
    [
        "primary_stop_reason",
        // The throughput split: what one blended tokens_per_second used to conflate, plus how many provider requests
        // the sums are made of. Added once a live run showed prompt + cached + generated summing exactly to the usage
        // total — two requests, one recorded.
        "ttft_ms",
        "prompt_tokens",
        "prompt_ms",
        "generation_tokens",
        "generation_ms",
        "cached_prompt_tokens",
        "segment_count",
        "repeat_group_id",
        "repeat_index",
        "is_warmup",
        "invocation_timeout_seconds"
    ];

    private static readonly string[] ProjectColumns = ["max_output_tokens", "invocation_timeout_seconds"];

    [Test]
    public async Task Migrate_ToLatest_AddsEveryReadinessColumn()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-readiness.sqlite").ConfigureAwait(false);

        var runColumns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        foreach (var column in RunColumns)
        {
            AssertEx.True(runColumns.Contains(column), $"benchmark_runs must record {column}.");
        }

        var projectColumns = await probe.ColumnsAsync("benchmark_projects").ConfigureAwait(false);
        foreach (var column in ProjectColumns)
        {
            AssertEx.True(projectColumns.Contains(column), $"benchmark_projects must record {column}.");
        }

        // The blended columns are kept, not replaced: every existing reader of a run keeps working, and the fallback
        // for a runtime that reports no timings still has somewhere to land.
        AssertEx.True(runColumns.Contains("tokens_per_second"), "The existing blended throughput column must survive.");
        AssertEx.True(runColumns.Contains("duration_ms"), "The existing wall-clock duration column must survive.");
        AssertEx.True(runColumns.Contains("total_tokens"), "The existing total-token column must survive.");
    }

    [Test]
    public async Task Migrate_ToLatest_LeavesEveryNewColumnHistorySafe()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-readiness-defaults.sqlite").ConfigureAwait(false);

        foreach (var column in RunColumns.Where(static column => !string.Equals(column, "is_warmup", StringComparison.Ordinal)))
        {
            AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_runs", column).ConfigureAwait(false),
                $"{column} must stay NULL on rows frozen before it was measured, never be backfilled with an invented value.");
        }

        foreach (var column in ProjectColumns)
        {
            AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_projects", column).ConfigureAwait(false),
                $"An absent {column} means the frozen default, which is NULL — never a defaulted number.");
        }

        // SQLite renders the boolean default as the literal 0 it stores it as.
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("benchmark_runs", "is_warmup").ConfigureAwait(false),
            "Every existing run must read as a measured run, never as a warm-up.");
    }

    [Test]
    public async Task Migrate_ToLatest_IndexesTheRepeatGroup()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-readiness-index.sqlite").ConfigureAwait(false);

        AssertEx.True(
            await probe.IndexExistsAsync("benchmark_runs", "ix_benchmark_runs_repeat_group_id", unique: false, "repeat_group_id")
                       .ConfigureAwait(false),
            "Reading one group's runs back must not scan the project's whole run history.");
    }

    [Test]
    public async Task OutputBudget_MustStayInsideTheProjectContext()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-output-budget-constraint.sqlite").ConfigureAwait(false);

        // A budget at or above the window could never be honoured, so the database refuses it rather than letting it
        // masquerade as "no budget".
        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, maxOutputTokens: 4096)).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, maxOutputTokens: 0)).ConfigureAwait(false);

        await InsertProjectAsync(probe, maxOutputTokens: 4095).ConfigureAwait(false);
        await InsertProjectAsync(probe, maxOutputTokens: null).ConfigureAwait(false);
    }

    [Test]
    public async Task GenerationTimeout_MustStayInsideItsBounds()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-invocation-timeout-bounds.sqlite").ConfigureAwait(false);

        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, timeoutSeconds: 59)).ConfigureAwait(false);
        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProjectAsync(probe, timeoutSeconds: 7201)).ConfigureAwait(false);

        await InsertProjectAsync(probe, timeoutSeconds: 60).ConfigureAwait(false);
        await InsertProjectAsync(probe, timeoutSeconds: 7200).ConfigureAwait(false);
        await InsertProjectAsync(probe, timeoutSeconds: null).ConfigureAwait(false);
    }

    [Test]
    public async Task Migrate_Down_RemovesEveryReadinessColumnAndKeepsTheRows()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-readiness-down.sqlite").ConfigureAwait(false);
        await InsertProjectAsync(probe, maxOutputTokens: 2048, timeoutSeconds: 600).ConfigureAwait(false);

        await probe.MigrateToAsync(PreviousMigration).ConfigureAwait(false);

        var runColumns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        foreach (var column in RunColumns)
        {
            AssertEx.False(runColumns.Contains(column), $"Down must drop benchmark_runs.{column}.");
        }

        var projectColumns = await probe.ColumnsAsync("benchmark_projects").ConfigureAwait(false);
        foreach (var column in ProjectColumns)
        {
            AssertEx.False(projectColumns.Contains(column), $"Down must drop benchmark_projects.{column}.");
        }

        AssertEx.False(
            await probe.IndexExistsAsync("benchmark_runs", "ix_benchmark_runs_repeat_group_id", unique: false, "repeat_group_id")
                       .ConfigureAwait(false),
            "Down must drop the repeat-group index with the column it covers.");

        // SQLite drops a column by rebuilding the table from the migration's target model, so a row surviving the
        // rebuild is what proves the model this Down was generated against is the merged one.
        AssertEx.Equal(expected: 1L, await probe.ScalarAsync("SELECT COUNT(*) FROM benchmark_projects;").ConfigureAwait(false));
    }

    private static Task InsertProjectAsync(MigrationSchemaProbe probe, int? maxOutputTokens = null, int? timeoutSeconds = null) =>
        probe.ExecuteAsync("""
                           INSERT INTO benchmark_projects
                               (id, name, core_task_json, context_tokens, max_output_tokens, invocation_timeout_seconds,
                                agent_definition_id, version, created_at_utc, updated_at_utc)
                           VALUES ($id, 'p', X'7B7D', 4096, $budget, $timeout, $agent, 1, 1, 1);
                           """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$agent", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$budget", maxOutputTokens is null ? DBNull.Value : maxOutputTokens.Value);
                command.Parameters.AddWithValue("$timeout", timeoutSeconds is null ? DBNull.Value : timeoutSeconds.Value);
            });
}
