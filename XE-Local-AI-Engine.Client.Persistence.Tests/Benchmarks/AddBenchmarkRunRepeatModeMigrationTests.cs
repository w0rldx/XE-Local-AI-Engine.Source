namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkRunRepeatMode</c> records what a repeat group measures plus the two sampling inputs that make an
///     answer-variance group readable without decrypting a snapshot. The default matters in two directions: every run
///     recorded before this WAS a throughput repeat, so <c>repeat_mode</c> is backfilled truthfully, while the seed and
///     the temperature stay NULL — knowable is not the same as recorded, and only recorded belongs in a measurement
///     table. The rollback test guards the usual SQLite trap: dropping a column rebuilds the table from this
///     migration's target model, so a stale Down deletes columns it never mentions.
/// </summary>
public sealed class AddBenchmarkRunRepeatModeMigrationTests
{
    private const string PreviousMigration = "20260825171917_AddBenchmarkProjectReasoningBudget";

    private static readonly string[] AddedColumns = ["repeat_mode", "sampling_seed", "sampling_temperature"];

    [Test]
    public async Task Migrate_ToLatest_BackfillsTheModeAndLeavesTheSamplingUnrecorded()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-repeat-mode.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        foreach (var column in AddedColumns)
        {
            AssertEx.True(columns.Contains(column), $"benchmark_runs must record {column}.");
        }

        AssertEx.Equal("'Throughput'", await probe.ColumnDefaultAsync("benchmark_runs", "repeat_mode").ConfigureAwait(false),
            "Every run frozen before this was a throughput repeat, so the backfill is a fact rather than a guess.");
        AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_runs", "sampling_seed").ConfigureAwait(false),
            "The seed those runs used is knowable, but it was never recorded — and those are different facts.");
        AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_runs", "sampling_temperature").ConfigureAwait(false));
    }

    [Test]
    public async Task Migrate_Down_RemovesTheColumnsAndKeepsTheRepeatColumnsItSitsBeside()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-repeat-mode-down.sqlite").ConfigureAwait(false);

        await probe.MigrateToAsync(PreviousMigration).ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        foreach (var column in AddedColumns)
        {
            AssertEx.False(columns.Contains(column), $"Down must drop benchmark_runs.{column}.");
        }

        AssertEx.True(columns.Contains("repeat_group_id"), "Down must not take the repeat-group columns with it.");
        AssertEx.True(columns.Contains("repeat_index"));
        AssertEx.True(columns.Contains("is_warmup"));
        AssertEx.True(columns.Contains("primary_stop_reason"), "Nor the stop reason the table rebuild has to carry through.");
    }
}
