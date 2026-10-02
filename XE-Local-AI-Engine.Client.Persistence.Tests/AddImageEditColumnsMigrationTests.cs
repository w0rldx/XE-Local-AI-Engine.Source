namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

/// <summary>Pins the image-edit migration on seeded rows with foreign keys ON, as production runs.</summary>
/// <remarks>
///     Both tables are rebuilt; a rebuild that dropped <c>image_jobs</c> with enforcement live would cascade-delete
///     every generated image.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class AddImageEditColumnsMigrationTests
{
    private const string PreviousMigrationId = "20260930182455_AddMcpServerSlugSessionScopeHeaders";
    private const string ThisMigrationId = "20261002082214_AddImageEditColumns";

    private const string JobA = "0A1E0000-0000-4000-8000-00000000000A";
    private const string JobB = "0A1E0000-0000-4000-8000-00000000000B";
    private const string ImageA = "1A1E0000-0000-4000-8000-00000000000A";
    private const string ImageB = "1A1E0000-0000-4000-8000-00000000000B";

    [Test]
    public async Task Migrate_SeededJobsAndImages_KeepsEveryImageAndAddsNullableEditColumns()
    {
        await using var probe = await SeededProbeAsync("image-edit-columns.sqlite");

        AssertEx.Equal(2L, (await probe.LongsAsync("SELECT COUNT(*) FROM image_jobs;")).Single(), "Every job must survive the image_jobs rebuild.");
        AssertEx.Equal(
            "/data/generated-images/a/a.png,/data/generated-images/b/b.png",
            await ConcatAsync(probe, "SELECT storage_path AS v FROM generated_images ORDER BY storage_path"),
            "Every generated image row and its storage path must survive both rebuilds.");
        AssertEx.Equal(
            $"{JobA},{JobB}",
            await ConcatAsync(probe, "SELECT job_id AS v FROM generated_images ORDER BY job_id"),
            "Each image keeps its owning job.");

        var columns = await probe.ColumnsAsync("image_jobs");
        AssertEx.True(columns.Contains("edit_mode") && columns.Contains("source_image_id") && columns.Contains("strength"),
            "The migration must add edit_mode, source_image_id and strength.");
        AssertEx.Equal(0L, (await probe.LongsAsync("SELECT COUNT(*) FROM image_jobs WHERE edit_mode IS NOT NULL OR source_image_id IS NOT NULL OR strength IS NOT NULL;")).Single(),
            "Existing jobs migrate as plain text-to-image jobs.");
        AssertEx.Equal(0L, (await probe.LongsAsync(
                "SELECT COUNT(*) FROM pragma_table_info('image_jobs') WHERE name IN ('edit_mode', 'source_image_id', 'strength') AND (\"notnull\" <> 0 OR dflt_value IS NOT NULL);")).Single(),
            "The edit columns are nullable with no default.");
        AssertEx.Equal(0L, (await probe.LongsAsync("SELECT \"notnull\" FROM pragma_table_info('generated_images') WHERE name = 'job_id';")).Single(),
            "generated_images.job_id must become nullable so an upload can exist without a job.");

        AssertEx.Equal("SET NULL", await OnDeleteAsync(probe, "image_jobs", "source_image_id", "generated_images"));
        AssertEx.Equal("CASCADE", await OnDeleteAsync(probe, "generated_images", "job_id", "image_jobs"));
        AssertEx.True(await probe.IndexExistsAsync("image_jobs", "IX_image_jobs_source_image_id", unique: false, "source_image_id"),
            "The lineage FK is indexed.");

        // An upload is a job-less row.
        await probe.ExecuteAsync("""
                                 INSERT INTO generated_images (image_id, job_id, mime_type, width, height, size_bytes, storage_path, created_at_utc)
                                 VALUES ('1A1E0000-0000-4000-8000-0000000000CC', NULL, 'image/png', 64, 64, 10, '/data/generated-images/uploads/c.png', 3);
                                 """);
    }

    [Test]
    public async Task DeletingTheSourceJob_CascadesItsImage_AndKeepsTheDerivedJobWithANullSource()
    {
        await using var probe = await SeededProbeAsync("image-edit-source-delete.sqlite");
        await probe.ExecuteAsync($"""
                                  INSERT INTO image_jobs (id, model_name, prompt, seed, width, height, steps, sampler, cfg_scale, status, created_at_utc, edit_mode, source_image_id, strength)
                                  VALUES ('0A1E0000-0000-4000-8000-00000000000D', 'sd15', X'00', -1, 512, 512, 20, 'euler_a', 7.0, 0, 3, 'img2img', '{ImageA}', 0.6);
                                  """);

        await probe.ExecuteAsync($"DELETE FROM image_jobs WHERE id = '{JobA}';");

        AssertEx.Equal(0L, (await probe.LongsAsync($"SELECT COUNT(*) FROM generated_images WHERE image_id = '{ImageA}';")).Single(),
            "Deleting a job still cascades to its image.");
        AssertEx.Equal(1L, (await probe.LongsAsync(
                "SELECT COUNT(*) FROM image_jobs WHERE id = '0A1E0000-0000-4000-8000-00000000000D' AND source_image_id IS NULL AND edit_mode = 'img2img';")).Single(),
            "The derived job survives its source's deletion with source_image_id cleared.");
    }

    [Test]
    public async Task RollBack_WithAnUpload_DropsTheUploadAndRestoresANonNullJobForeignKey()
    {
        await using var probe = await SeededProbeAsync("image-edit-rollback.sqlite");
        await probe.ExecuteAsync("""
                                 INSERT INTO generated_images (image_id, job_id, mime_type, width, height, size_bytes, storage_path, created_at_utc)
                                 VALUES ('1A1E0000-0000-4000-8000-0000000000CC', NULL, 'image/png', 64, 64, 10, '/data/generated-images/uploads/c.png', 3);
                                 """);

        await MigrateWithForeignKeysOnAsync(probe.DatabasePath, PreviousMigrationId);

        AssertEx.Equal(0L, (await probe.LongsAsync("SELECT COUNT(*) FROM pragma_foreign_key_check;")).Single(),
            "The rollback must leave no foreign-key violation behind.");
        AssertEx.Equal(
            $"{ImageA},{ImageB}",
            await ConcatAsync(probe, "SELECT image_id AS v FROM generated_images ORDER BY image_id"),
            "Job-backed images survive the rollback and the upload is dropped.");
        AssertEx.Equal(1L, (await probe.LongsAsync("SELECT \"notnull\" FROM pragma_table_info('generated_images') WHERE name = 'job_id';")).Single(),
            "generated_images.job_id must be NOT NULL again.");
    }

    private static async Task<MigrationSchemaProbe> SeededProbeAsync(string fileName)
    {
        var probe = await MigrationSchemaProbe.FromChatTemplateAsync(fileName, PreviousMigrationId);
        try
        {
            AssertEx.Equal(1L, (await probe.LongsAsync("PRAGMA foreign_keys;")).Single(), "The seed connection must enforce foreign keys, as production does.");
            await probe.ExecuteAsync($"""
                                      INSERT INTO image_jobs (id, model_name, prompt, seed, width, height, steps, sampler, cfg_scale, status, created_at_utc, image_id)
                                      VALUES
                                        ('{JobA}', 'sd15', X'01', 1, 512, 512, 20, 'euler_a', 7.0, 2, 1, '{ImageA}'),
                                        ('{JobB}', 'sd15', X'02', 2, 512, 512, 20, 'euler_a', 7.0, 2, 2, '{ImageB}');
                                      INSERT INTO generated_images (image_id, job_id, mime_type, width, height, size_bytes, storage_path, created_at_utc)
                                      VALUES
                                        ('{ImageA}', '{JobA}', 'image/png', 512, 512, 100, '/data/generated-images/a/a.png', 1),
                                        ('{ImageB}', '{JobB}', 'image/png', 512, 512, 200, '/data/generated-images/b/b.png', 2);
                                      """);

            await MigrateWithForeignKeysOnAsync(probe.DatabasePath, ThisMigrationId);
            return probe;
        }
        catch
        {
            await probe.DisposeAsync();
            throw;
        }
    }

    private static async Task MigrateWithForeignKeysOnAsync(string databasePath, string targetMigrationId)
    {
        var options = new DbContextOptionsBuilder<NodeChatDbContext>()
                      .UseSqlite($"Data Source={databasePath}")
                      .ConfigureWarnings(static warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
                      .Options;
        using var keyHolder = new NullNodeSqliteKeyHolder();
        await using var context = new NodeChatDbContext(options, keyHolder);

        // Enforcement is switched on explicitly on the connection the migrator then uses, as NodeSqlitePragmas does in
        // production, rather than resting on the bundled SQLite's compile-time default.
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");
        AssertEx.Equal(1L, await context.Database.SqlQueryRaw<long>("SELECT foreign_keys AS Value FROM pragma_foreign_keys").SingleAsync(),
            "The migration must run with foreign keys enforced.");

        await context.Database.GetService<IMigrator>().MigrateAsync(targetMigrationId);
    }

    private static async Task<string?> ConcatAsync(MigrationSchemaProbe probe, string orderedQuery)
    {
        // orderedQuery is a fixed literal in this suite, never user input.
        return Convert.ToString(await probe.ScalarAsync($"SELECT group_concat(v, ',') FROM ({orderedQuery});"), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> OnDeleteAsync(MigrationSchemaProbe probe, string table, string column, string principal)
    {
        return Convert.ToString(
            await probe.ScalarAsync(
                "SELECT on_delete FROM pragma_foreign_key_list($table) WHERE \"from\" = $column AND \"table\" = $principal;",
                command =>
                {
                    command.Parameters.AddWithValue("$table", table);
                    command.Parameters.AddWithValue("$column", column);
                    command.Parameters.AddWithValue("$principal", principal);
                }),
            CultureInfo.InvariantCulture);
    }
}
