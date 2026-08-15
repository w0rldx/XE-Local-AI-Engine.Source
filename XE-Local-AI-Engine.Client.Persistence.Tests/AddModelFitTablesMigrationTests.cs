namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>
///     <c>AddModelFitTables</c> creates the model-advisor triple: a snapshot per probe run, plus its benchmarks and
///     ranked recommendations, both cascading off the snapshot. It also created <c>approved_utility_images</c>, which a
///     later migration drops — see <see cref="DropApprovedUtilityImagesMigrationTests" />.
/// </summary>
public sealed class AddModelFitTablesMigrationTests
{
    [Test]
    public async Task Migrate_ToLatest_CreatesSnapshotBenchmarkAndRecommendationTables()
    {
        await using var probe = await MigrationSchemaProbe.MigrateChatAsync("model-fit-tables.sqlite").ConfigureAwait(false);

        AssertEx.True(await probe.TableExistsAsync("model_fit_snapshots").ConfigureAwait(false), "model_fit_snapshots must exist.");
        AssertEx.True(await probe.TableExistsAsync("model_fit_benchmarks").ConfigureAwait(false), "model_fit_benchmarks must exist.");
        AssertEx.True(await probe.TableExistsAsync("model_fit_recommendations").ConfigureAwait(false), "model_fit_recommendations must exist.");

        AssertEx.True((await probe.ColumnsAsync("model_fit_snapshots").ConfigureAwait(false)).IsSupersetOf(new[]
        {
            "id",
            "operation",
            "use_case",
            "provider_name",
            "model_name",
            "status",
            "raw_json",
            "is_latest_successful",
            "created_at_utc"
        }), "model_fit_snapshots must expose the mapped columns.");

        AssertEx.True((await probe.ColumnsAsync("model_fit_recommendations").ConfigureAwait(false)).IsSupersetOf(new[]
        {
            "id",
            "snapshot_id",
            "rank",
            "model_name",
            "score",
            "fit_level",
            "quantization",
            "required_vram_mb",
            "context_tokens"
        }), "model_fit_recommendations must expose the mapped columns.");

        AssertEx.True(await probe.ForeignKeyExistsAsync("model_fit_benchmarks", "snapshot_id", "model_fit_snapshots").ConfigureAwait(false),
            "Benchmarks must hang off their snapshot.");
        AssertEx.True(await probe.ForeignKeyExistsAsync("model_fit_recommendations", "snapshot_id", "model_fit_snapshots").ConfigureAwait(false),
            "Recommendations must hang off their snapshot.");

        // The advisor reads the ranked list in rank order for one snapshot, and resolves "the current answer" through
        // the is_latest_successful discriminator; both are covered indexes rather than scans.
        AssertEx.True(await probe.IndexExistsAsync("model_fit_recommendations",
                "IX_model_fit_recommendations_snapshot_id_rank",
                unique: false,
                "snapshot_id",
                "rank").ConfigureAwait(false),
            "The ranked-recommendation lookup must be indexed on (snapshot_id, rank).");

        AssertEx.True(await probe.IndexExistsAsync("model_fit_snapshots",
                "IX_model_fit_snapshots_operation_use_case_provider_name_model_name_is_latest_successful",
                unique: false,
                "operation",
                "use_case",
                "provider_name",
                "model_name",
                "is_latest_successful").ConfigureAwait(false),
            "The latest-successful snapshot lookup must be indexed.");
    }
}
