namespace XE_Local_AI_Engine.Client.Persistence.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkRunLaunchReceipts</c> adds what a benchmark run intended to launch with and what it actually
///     launched. Every column is nullable on purpose: runs frozen before launch evidence existed keep reading, and
///     the receipt/environment blocks stay NULL until a spawn records them.
/// </summary>
public sealed class AddBenchmarkRunLaunchReceiptsMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_AddsTheIntendedAndEffectiveLaunchColumns()
    {
        // Pinned AT this migration, not at head: the judge half of the block moved to benchmark_judge_attempts when
        // the 1-5 judge was retired, so head no longer carries it and only this point in the chain can assert it.
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-run-launch-receipts.sqlite",
                                          "20260816174029_AddBenchmarkRunLaunchReceipts")
                                      .ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("benchmark_runs").ConfigureAwait(false);
        foreach (var phase in new[]
                 {
                     "primary",
                     "judge"
                 })
        {
            AssertEx.True(columns.IsSupersetOf(new[]
                {
                    $"{phase}_variant",
                    $"{phase}_kv_cache_type",
                    $"{phase}_kv_cache_type_source",
                    $"{phase}_kv_auto_reason",
                    $"{phase}_flash_attention_mode",
                    $"{phase}_intended_launch_identity",
                    $"{phase}_intended_executable_sha256",
                    $"{phase}_launch_receipt_json",
                    $"{phase}_environment_facts_json",
                    $"{phase}_receipt_hash",
                    $"{phase}_environment_facts_hash",
                    $"{phase}_effective_launch_identity",
                    $"{phase}_effective_backend",
                    $"{phase}_placement_offloaded",
                    $"{phase}_placement_total",
                    $"{phase}_launch_executable_sha256",
                    $"{phase}_launch_has_aux_assets",
                    $"{phase}_launch_kv_cache_type_source"
                }),
                $"benchmark_runs must carry the full {phase} launch-evidence block.");
        }

        AssertEx.True(await probe.IndexExistsAsync("benchmark_runs",
                "ix_benchmark_runs_project_primary_kv_cache_type",
                unique: false,
                "project_id",
                "primary_kv_cache_type").ConfigureAwait(false),
            "Comparing a project's runs by KV-cache type must be indexed.");
        AssertEx.Null(await probe.ColumnDefaultAsync("benchmark_runs", "primary_kv_cache_type").ConfigureAwait(false),
            "Legacy rows must stay NULL rather than be backfilled with a type they never launched with.");
    }
}
