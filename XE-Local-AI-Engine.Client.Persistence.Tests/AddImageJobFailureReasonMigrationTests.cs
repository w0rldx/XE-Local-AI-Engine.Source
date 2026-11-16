namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>Pins the additive <c>image_jobs.failure_reason</c> column on seeded rows, forward and back.</summary>
/// <remarks>
///     The rollback drops the column, which SQLite does by rebuilding <c>image_jobs</c>; a rebuild that deleted the
///     parent rows would cascade-delete every generated image.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class AddImageJobFailureReasonMigrationTests
{
    private const string PreviousMigrationId = "20261115160852_AddModelLabExperimentNotesAndPin";
    private const string ThisMigrationId = "20261115230956_AddImageJobFailureReason";

    private const string JobId = "0F1E0000-0000-4000-8000-00000000000A";
    private const string ImageId = "1F1E0000-0000-4000-8000-00000000000A";

    [Test]
    public async Task Migrate_SeededFailedJob_AddsANullableReasonAndKeepsTheEnglishText()
    {
        await using var probe = await SeededProbeAsync("image-job-failure-reason.sqlite");

        await probe.MigrateToAsync(ThisMigrationId);

        AssertEx.True((await probe.ColumnsAsync("image_jobs")).Contains("failure_reason"), "The migration must add failure_reason.");
        AssertEx.Equal(0L,
            (await probe.LongsAsync("SELECT COUNT(*) FROM pragma_table_info('image_jobs') WHERE name = 'failure_reason' AND (\"notnull\" <> 0 OR dflt_value IS NOT NULL);")).Single(),
            "failure_reason is nullable with no default.");
        AssertEx.Equal(1L,
            (await probe.LongsAsync("SELECT COUNT(*) FROM image_jobs WHERE failure_reason IS NULL AND sanitized_error = 'Image generation failed.';")).Single(),
            "An existing failed job keeps its English text and has no reason, so the UI falls back to the text.");
    }

    [Test]
    public async Task RollBack_KeepsTheJobAndItsGeneratedImage()
    {
        await using var probe = await SeededProbeAsync("image-job-failure-reason-rollback.sqlite");
        await probe.MigrateToAsync(ThisMigrationId);
        await probe.ExecuteAsync("UPDATE image_jobs SET failure_reason = 'OutOfMemory';");

        await probe.MigrateToAsync(PreviousMigrationId);

        AssertEx.False((await probe.ColumnsAsync("image_jobs")).Contains("failure_reason"), "The rollback must drop failure_reason.");
        AssertEx.Equal(1L, (await probe.LongsAsync("SELECT COUNT(*) FROM image_jobs;")).Single(), "The job must survive the image_jobs rebuild.");
        AssertEx.Equal(1L, (await probe.LongsAsync("SELECT COUNT(*) FROM generated_images;")).Single(), "The rebuild must not cascade-delete the job's image.");
    }

    private static async Task<MigrationSchemaProbe> SeededProbeAsync(string fileName)
    {
        var probe = await MigrationSchemaProbe.FromChatTemplateAsync(fileName, PreviousMigrationId);
        try
        {
            await probe.ExecuteAsync($"""
                                      INSERT INTO image_jobs (id, model_name, prompt, seed, width, height, steps, sampler, cfg_scale, status, created_at_utc, image_id, sanitized_error)
                                      VALUES ('{JobId}', 'sd15', X'01', 1, 512, 512, 20, 'euler_a', 7.0, 3, 1, '{ImageId}', 'Image generation failed.');
                                      INSERT INTO generated_images (image_id, job_id, mime_type, width, height, size_bytes, storage_path, created_at_utc)
                                      VALUES ('{ImageId}', '{JobId}', 'image/png', 512, 512, 100, '/data/generated-images/a/a.png', 1);
                                      """);
            return probe;
        }
        catch
        {
            await probe.DisposeAsync();
            throw;
        }
    }
}
