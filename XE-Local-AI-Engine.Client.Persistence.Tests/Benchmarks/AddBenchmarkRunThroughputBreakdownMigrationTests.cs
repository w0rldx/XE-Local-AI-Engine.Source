namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkRunThroughputBreakdown</c> adds six plaintext, nullable columns to <c>benchmark_runs</c>: the
///     time to first token, and the prompt-processing (pp) versus generation (tg) split of tokens and milliseconds
///     that one blended <c>tokens_per_second</c> used to conflate. Every one of them must default to NULL — a run
///     measured before the split existed has no split, and backfilling it with a zero would put an invented
///     measurement next to a real one in the same table.
/// </summary>
public sealed class AddBenchmarkRunThroughputBreakdownMigrationTests
{
    private static readonly string[] ThroughputColumns =
    [
        "ttft_ms",
        "prompt_tokens",
        "prompt_ms",
        "generation_tokens",
        "generation_ms",
        "cached_prompt_tokens",
        // AddBenchmarkRunSegmentCount: how many provider requests the sums above are made of. Added once a live run
        // showed prompt + cached + generated summing exactly to the usage total — two requests, one recorded — so the
        // request count is what makes the sums readable rather than merely plausible.
        "segment_count"
    ];

    [Test]
    public async Task Migrate_ToLatest_AddsTheThroughputColumnsAsNullable()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-throughput.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        foreach (var column in ThroughputColumns)
        {
            AssertEx.True(columns.Contains(column), $"benchmark_runs must record {column}.");
            AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_runs", column).ConfigureAwait(false),
                $"{column} must stay NULL on rows frozen before the throughput split was measured.");
        }

        // The blended columns are kept, not replaced: every existing reader of a run keeps working, and the fallback
        // for a runtime that reports no timings still has somewhere to land.
        AssertEx.True(columns.Contains("tokens_per_second"), "The existing blended throughput column must survive.");
        AssertEx.True(columns.Contains("duration_ms"), "The existing wall-clock duration column must survive.");
        AssertEx.True(columns.Contains("total_tokens"), "The existing total-token column must survive.");
    }
}
