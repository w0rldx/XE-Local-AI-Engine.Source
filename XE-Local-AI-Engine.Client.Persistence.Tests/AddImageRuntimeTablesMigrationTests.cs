namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

public sealed class AddImageRuntimeTablesMigrationTests : IDisposable
{
    private const string PreImageMigrationId = "20260626234754_AddInferenceProfilesAndBenchmarkMetrics";

    private readonly INodeSqliteKeyHolder _keyHolder = new FixedNodeSqliteKeyHolder(CreateKeyMaterial());
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_CreatesAllThreeImageTables()
    {
        var databasePath = GetDatabasePath("image-tables-up.sqlite");
        await MigrateUpAsync(databasePath).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await TableExistsAsync(connection, "image_jobs").ConfigureAwait(false), "Migration should create image_jobs.");
        AssertEx.True(await TableExistsAsync(connection, "generated_images").ConfigureAwait(false), "Migration should create generated_images.");
        AssertEx.True(await TableExistsAsync(connection, "image_model_profiles").ConfigureAwait(false), "Migration should create image_model_profiles.");
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_ImageJobsHasExpectedColumns()
    {
        var databasePath = GetDatabasePath("image-jobs-columns-up.sqlite");
        await MigrateUpAsync(databasePath).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        var columns = await GetTableColumnsAsync(connection, "image_jobs").ConfigureAwait(false);

        AssertEx.True(columns.SetEquals(new[]
        {
            "id",
            "model_name",
            "prompt",
            "negative_prompt",
            "seed",
            "width",
            "height",
            "steps",
            "sampler",
            "cfg_scale",
            "status",
            "created_at_utc",
            "started_at_utc",
            "completed_at_utc",
            "duration_ms",
            "image_id",
            "sanitized_error",
            "cancellation_requested_at_utc"
        }), "image_jobs should expose all mapped columns.");
    }

    [Test]
    public async Task MigrateAsync_WhenApplied_GeneratedImagesHasCascadeForeignKeyToImageJobs()
    {
        var databasePath = GetDatabasePath("generated-images-fk-up.sqlite");
        await MigrateUpAsync(databasePath).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.True(await HasCascadeForeignKeyAsync(connection, "generated_images", "image_jobs").ConfigureAwait(false),
            "generated_images should carry a cascade FK to image_jobs.");
    }

    [Test]
    public async Task ImageJob_RoundTrips_WithPromptEncryptedAtRest()
    {
        var databasePath = GetDatabasePath("image-job-roundtrip.sqlite");
        const string promptText = "an-utterly-distinctive-prompt-phrase-for-encryption-assertion";
        var jobId = Guid.NewGuid();

        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            context.Add(new ImageJob
            {
                Id = jobId,
                ModelName = "leejet/stable-diffusion-1.5-gguf",
                Prompt = Encoding.UTF8.GetBytes(promptText),
                Seed = -1,
                Width = 512,
                Height = 512,
                Steps = 20,
                Sampler = "euler_a",
                CfgScale = 7.0,
                Status = ImageJobStatus.Queued,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        // At-rest: the plaintext prompt must not appear anywhere in the database file.
        AssertEx.False(await DatabaseContainsAsync(databasePath, Encoding.UTF8.GetBytes(promptText)).ConfigureAwait(false),
            "The prompt must be encrypted at rest — its plaintext bytes must not appear in the database file.");

        // Round-trip: a fresh materialization decrypts the prompt back to the original.
        await using (var context = AgentDefinitionTestContextFactory.Create(databasePath, _keyHolder))
        {
            var reloaded = AssertEx.NotNull(await context.ImageJobs.SingleOrDefaultAsync(job => job.Id == jobId).ConfigureAwait(false));
            AssertEx.Equal(promptText, Encoding.UTF8.GetString(reloaded.Prompt));
        }
    }

    [Test]
    public async Task MigrateAsync_WhenRolledBack_DropsAllThreeImageTables()
    {
        var databasePath = GetDatabasePath("image-tables-rollback.sqlite");

        await using (var context = CreateForMigration(databasePath))
        {
            await context.Database.MigrateAsync().ConfigureAwait(false);
            await context.Database.GetService<IMigrator>().MigrateAsync(PreImageMigrationId).ConfigureAwait(false);
        }

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);

        AssertEx.False(await TableExistsAsync(connection, "image_jobs").ConfigureAwait(false), "Rollback should drop image_jobs.");
        AssertEx.False(await TableExistsAsync(connection, "generated_images").ConfigureAwait(false), "Rollback should drop generated_images.");
        AssertEx.False(await TableExistsAsync(connection, "image_model_profiles").ConfigureAwait(false), "Rollback should drop image_model_profiles.");
    }

    private async Task MigrateUpAsync(string databasePath)
    {
        await using var context = CreateForMigration(databasePath);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private NodeChatDbContext CreateForMigration(string databasePath)
    {
        return AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    private static async Task<bool> HasCascadeForeignKeyAsync(SqliteConnection connection, string tableName, string referencedTable)
    {
        await using var command = connection.CreateCommand();
        // tableName is an internal test constant, never user input.
#pragma warning disable CA2100
        command.CommandText = $"SELECT \"table\", \"on_delete\" FROM pragma_foreign_key_list('{tableName}');";
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(0), referencedTable, StringComparison.Ordinal)
                && reader.GetString(1).Contains("CASCADE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<IReadOnlySet<string>> GetTableColumnsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        // tableName is an internal test constant, never user input.
#pragma warning disable CA2100
        command.CommandText = $"SELECT * FROM {tableName} LIMIT 0;";
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        return Enumerable.Range(start: 0, reader.FieldCount)
                         .Select(reader.GetName)
                         .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<bool> DatabaseContainsAsync(string databasePath, byte[] needle)
    {
        var fileBytes = await File.ReadAllBytesAsync(databasePath).ConfigureAwait(false);
        return ContainsSubsequence(fileBytes, needle);
    }

    private static bool ContainsSubsequence(byte[] source, byte[] needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var sourceIndex = 0; sourceIndex <= source.Length - needle.Length; sourceIndex++)
        {
            var matched = true;
            for (var needleIndex = 0; needleIndex < needle.Length; needleIndex++)
            {
                if (source[sourceIndex + needleIndex] == needle[needleIndex])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static byte[] CreateKeyMaterial()
    {
        return Enumerable.Range(start: 0, count: 32).Select(static value => (byte)(value + 11)).ToArray();
    }

    private sealed class FixedNodeSqliteKeyHolder(byte[] key) : INodeSqliteKeyHolder
    {
        private byte[]? _key = key;

        public ReadOnlyMemory<byte> Key
        {
            get
            {
                ObjectDisposedException.ThrowIf(_key is null, this);
                return _key;
            }
        }

        public void Dispose()
        {
            if (_key is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
