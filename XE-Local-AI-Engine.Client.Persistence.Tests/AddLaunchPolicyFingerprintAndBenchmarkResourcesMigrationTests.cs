namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddLaunchPolicyFingerprintAndBenchmarkResources</c> splits the single "free VRAM" figure into the two
///     measurements that actually decide a spawn — what the whole device has free, and what this process is budgeted —
///     and binds every profile to the launch-policy fingerprint it was frozen under. A profile frozen before the
///     fingerprint existed cannot be shown to still match the policy that produced it, so the migration
///     <b>invalidates</b> it: status goes Stale and the benchmark that justified the freeze is unbound. Replaying such
///     a profile verbatim is exactly the failure this trades away, so the invalidation — not just the new columns — is
///     what this suite pins.
/// </summary>
public sealed class AddLaunchPolicyFingerprintAndBenchmarkResourcesMigrationTests
{
    private const string PreFingerprintMigrationId = "20260722192133_BindDevelopmentProjectsToSelectedFolders";
    private const string ThisMigrationId = "20260726192021_AddLaunchPolicyFingerprintAndBenchmarkResources";

    private const long StaleStatus = 2;

    [Test]
    public async Task Migrate_OverAFrozenProfile_MarksItStaleAndUnbindsItsBenchmark()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("launch-policy-fingerprint.sqlite", PreFingerprintMigrationId).ConfigureAwait(false);

        var profileId = Guid.NewGuid().ToString();
        await probe.ExecuteAsync("""
                                 INSERT INTO inference_profiles
                                     (id, machine_key, model_name, role, backend, llamacpp_build, quant, ctx_size,
                                      flash_attn, is_moe, free_vram_at_freeze_bytes, status, benchmark_snapshot_id,
                                      created_at_utc, updated_at_utc)
                                 VALUES ($id, 'box-1', 'qwen3.5:0.8b', 0, 'cuda', 'b10201', 'Q4_K_M', 4096, 1, 0,
                                         17179869184, 1, $snapshot_id, 1234, 1234);
                                 """,
            command =>
            {
                command.Parameters.AddWithValue("$id", profileId);
                command.Parameters.AddWithValue("$snapshot_id", Guid.NewGuid().ToString());
            }).ConfigureAwait(false);

        await probe.MigrateToAsync(ThisMigrationId).ConfigureAwait(false);

        AssertEx.Equal(StaleStatus,
            (await probe.LongsAsync("SELECT status FROM inference_profiles WHERE id = $id;",
                command => command.Parameters.AddWithValue("$id", profileId)).ConfigureAwait(false)).Single(),
            "A profile frozen without a launch-policy fingerprint must be invalidated, never replayed as still-frozen.");

        AssertEx.Null(await probe.ScalarAsync("SELECT benchmark_snapshot_id FROM inference_profiles WHERE id = $id;",
                command => command.Parameters.AddWithValue("$id", profileId)).ConfigureAwait(false),
            "The benchmark that justified the old freeze must be unbound along with it.");
    }

    [Test]
    public async Task Migrate_ToThisMigration_ReplacesTheSingleFreeVramFigureWithTheDeviceAndProcessPair()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("launch-policy-columns.sqlite", ThisMigrationId).ConfigureAwait(false);

        var profileColumns = await probe.ColumnsAsync("inference_profiles").ConfigureAwait(false);

        AssertEx.False(profileColumns.Contains("free_vram_at_freeze_bytes"),
            "The ambiguous single free-VRAM column must be gone — keeping it alongside the pair invites reading the wrong one.");
        AssertEx.True(profileColumns.IsSupersetOf(new[]
        {
            "launch_policy_fingerprint",
            "launch_policy_fingerprint_version",
            "global_free_vram_at_freeze_bytes",
            "process_budget_vram_at_freeze_bytes"
        }), "inference_profiles must carry the fingerprint and both freeze-time VRAM figures.");

        AssertEx.True((await probe.ColumnsAsync("model_fit_benchmarks").ConfigureAwait(false)).IsSupersetOf(new[]
        {
            "external_pressure_detected",
            "global_free_vram_after_bytes",
            "global_free_vram_load_bytes",
            "launch_policy_fingerprint",
            "launch_policy_fingerprint_version",
            "minimum_global_free_vram_bytes",
            "minimum_process_budget_vram_bytes",
            "peak_process_ram_bytes",
            "process_budget_vram_after_bytes",
            "process_budget_vram_load_bytes"
        }), "A benchmark must record the resource envelope and the policy it was measured under.");

        // A run taken while something else was competing for the device is not comparable to a clean one, so the flag
        // has to default to "no pressure detected" rather than to null-means-maybe.
        AssertEx.Equal("0", await probe.ColumnDefaultAsync("model_fit_benchmarks", "external_pressure_detected").ConfigureAwait(false));
    }
}
