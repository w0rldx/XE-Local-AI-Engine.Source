namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddBenchmarkProfileRevisionBinding</c> binds a benchmark row to the inference profile it ran under, and
///     records the two launch knobs that change the number most (<c>flash_attn</c>, the V-cache type). Without the
///     binding a benchmark is an unattributable number that cannot be compared to another run.
/// </summary>
public sealed class AddBenchmarkProfileRevisionBindingMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_BindsBenchmarksToTheirProfile()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("benchmark-profile-revision-binding.sqlite").ConfigureAwait(false);

        var columns = await probe.ColumnsAsync("model_fit_benchmarks").ConfigureAwait(false);
        AssertEx.True(columns.IsSupersetOf(new[]
        {
            "profile_id",
            "flash_attn",
            "kv_type_v"
        }), "model_fit_benchmarks must carry the profile binding and the launch knobs it was measured under.");

        AssertEx.True(await probe.IndexExistsAsync("model_fit_benchmarks",
                "IX_model_fit_benchmarks_profile_id",
                unique: false,
                "profile_id").ConfigureAwait(false),
            "The per-profile benchmark lookup must be indexed.");
    }
}
