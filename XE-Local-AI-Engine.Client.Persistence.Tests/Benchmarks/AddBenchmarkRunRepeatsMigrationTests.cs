namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkRunRepeats</c> adds the three columns that turn a set of runs into a repeat group. The two
///     nullable ones must stay NULL by default and <c>is_warmup</c> must default to FALSE, because that is what an
///     already-stored run truthfully is: a single, non-warm-up run. A default that backfilled a group id, or that made
///     every historical run read as a warm-up, would silently drop the whole existing ranking.
/// </summary>
public sealed class AddBenchmarkRunRepeatsMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsTheRepeatColumnsWithHistorySafeDefaults()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-repeats.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        AssertEx.True(columns.Contains("repeat_group_id"), "benchmark_runs must record the repeat group.");
        AssertEx.True(columns.Contains("repeat_index"), "benchmark_runs must record the position inside the group.");
        AssertEx.True(columns.Contains("is_warmup"), "benchmark_runs must record whether a run is a warm-up.");
        AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_runs", "repeat_group_id").ConfigureAwait(false),
            "A run frozen before repeats existed belongs to no group.");
        AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_runs", "repeat_index").ConfigureAwait(false),
            "A run that belongs to no group has no position in one.");

        // SQLite renders the boolean default as the literal 0 it stores it as.
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("benchmark_runs", "is_warmup").ConfigureAwait(false),
            "Every existing run must read as a measured run, never as a warm-up.");
    }

    [Test]
    public async Task Migrate_ToLatest_IndexesTheRepeatGroup()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-repeats-index.sqlite").ConfigureAwait(false);

        AssertEx.True(
            await probe.IndexExistsAsync("benchmark_runs", "ix_benchmark_runs_repeat_group_id", unique: false, "repeat_group_id")
                       .ConfigureAwait(false),
            "Reading one group's runs back must not scan the project's whole run history.");
    }
}
