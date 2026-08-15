namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using Microsoft.Data.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddInferenceProfilesAndBenchmarkMetrics</c> creates the frozen-launch-args store and widens
///     <c>model_fit_benchmarks</c> with the measurements that justify a freeze. The unique index is the load-bearing
///     part: one profile per (machine, model, role, backend), so a second explore on the same box replays or replaces
///     the existing profile instead of silently accumulating rivals that spawn would then pick between arbitrarily.
/// </summary>
public sealed class AddInferenceProfilesAndBenchmarkMetricsMigrationTests
{
    private const string ThisMigrationId = "20260626234754_AddInferenceProfilesAndBenchmarkMetrics";

    [Test]
    public async Task Migrate_ToThisMigration_CreatesInferenceProfilesWithTheFreezeColumns()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("inference-profiles.sqlite", ThisMigrationId).ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("inference_profiles").ConfigureAwait(false), "inference_profiles must exist.");

        AssertEx.True((await probe.ColumnsAsync("inference_profiles").ConfigureAwait(false)).SetEquals(new[]
        {
            "id",
            "machine_key",
            "model_name",
            "role",
            "backend",
            "llamacpp_build",
            "quant",
            "ctx_size",
            "n_gpu_layers",
            "tensor_split",
            "override_tensor",
            "kv_type_k",
            "kv_type_v",
            "flash_attn",
            "n_params",
            "is_moe",
            "expert_count",
            "free_vram_at_freeze_bytes",
            "status",
            "benchmark_snapshot_id",
            "created_at_utc",
            "updated_at_utc"
        }), "inference_profiles must expose exactly the columns this migration created.");

        AssertEx.True(await probe.IndexExistsAsync("inference_profiles", "IX_inference_profiles_status", unique: false, "status").ConfigureAwait(false),
            "Spawn scans profiles by status, so status must be indexed.");
    }

    [Test]
    public async Task Migrate_ToThisMigration_AddsTheBenchmarkMeasurementColumns()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("inference-profiles-benchmarks.sqlite", ThisMigrationId).ConfigureAwait(false);

        AssertEx.True((await probe.ColumnsAsync("model_fit_benchmarks").ConfigureAwait(false)).IsSupersetOf(new[]
        {
            "backend",
            "cache_hit_rate",
            "ctx_size",
            "kv_type",
            "llamacpp_build",
            "machine_key",
            "n_gpu_layers",
            "override_tensor",
            "pp_tokens_per_second",
            "quant",
            "tensor_split",
            "tool_loop_ms",
            "vram_after_bytes",
            "vram_load_bytes"
        }), "model_fit_benchmarks must carry the launch-configuration and measurement columns a freeze is justified by.");
    }

    [Test]
    public async Task Migrate_ToThisMigration_AllowsOnlyOneProfilePerMachineModelRoleAndBackend()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("inference-profiles-unique.sqlite", ThisMigrationId).ConfigureAwait(false);

        AssertEx.True(await probe.IndexExistsAsync("inference_profiles",
                "IX_inference_profiles_machine_key_model_name_role_backend",
                unique: true,
                "machine_key",
                "model_name",
                "role",
                "backend").ConfigureAwait(false),
            "The profile identity must be uniquely indexed.");

        await InsertProfileAsync(probe, backend: "cuda").ConfigureAwait(false);

        // A different backend on the same box and model is a different profile and must be allowed.
        await InsertProfileAsync(probe, backend: "vulkan").ConfigureAwait(false);

        await AssertEx.ThrowsAsync<SqliteException>(() => InsertProfileAsync(probe, backend: "cuda"),
            "A second profile with the same identity must be rejected.").ConfigureAwait(false);
    }

    private static Task InsertProfileAsync(MigrationSchemaProbe probe, string backend)
    {
        return probe.ExecuteAsync("""
                                  INSERT INTO inference_profiles
                                      (id, machine_key, model_name, role, backend, llamacpp_build, quant, ctx_size,
                                       flash_attn, is_moe, status, created_at_utc, updated_at_utc)
                                  VALUES ($id, 'box-1', 'qwen3.5:0.8b', 0, $backend, 'b10201', 'Q4_K_M', 4096, 1, 0, 1, 1234, 1234);
                                  """,
            command =>
            {
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
                command.Parameters.AddWithValue("$backend", backend);
            });
    }
}
