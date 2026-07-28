namespace XE_Local_AI_Engine.Tests.Inference;

using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LaunchPolicyFingerprintMigrationTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task Migration_DemotesLegacyProfilesAndRollbackRemovesFingerprintColumns()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var migrations = dbContext.Database.GetMigrations().ToList();
        var migrationIndex = migrations.FindIndex(
            id => id.EndsWith("AddLaunchPolicyFingerprintAndBenchmarkResources", StringComparison.Ordinal));
        AssertEx.True(migrationIndex > 0, "The fingerprint migration must exist and have a predecessor.");
        var migration = migrations[migrationIndex];
        var previousMigration = migrations[migrationIndex - 1];
        var migrator = dbContext.GetInfrastructure().GetRequiredService<IMigrator>();

        await migrator.MigrateAsync(previousMigration).ConfigureAwait(false);
        var profileId = Guid.NewGuid();
        var benchmarkId = Guid.NewGuid();
        await InsertLegacyProfileAsync(dbContext, profileId, benchmarkId).ConfigureAwait(false);

        await migrator.MigrateAsync(migration).ConfigureAwait(false);

        var (status, benchmarkSnapshotId, fingerprintVersion, fingerprint) =
            await ReadProfileStateAsync(dbContext, profileId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, status);
        AssertEx.Null(benchmarkSnapshotId);
        AssertEx.Null(fingerprintVersion);
        AssertEx.Null(fingerprint);
        var migratedColumns = await ReadInferenceProfileColumnNamesAsync(dbContext).ConfigureAwait(false);
        AssertEx.False(migratedColumns.Contains("free_vram_at_freeze_bytes", StringComparer.Ordinal));
        AssertEx.True(migratedColumns.Contains("global_free_vram_at_freeze_bytes", StringComparer.Ordinal));
        AssertEx.True(migratedColumns.Contains("process_budget_vram_at_freeze_bytes", StringComparer.Ordinal));

        await migrator.MigrateAsync(previousMigration).ConfigureAwait(false);

        var columns = await ReadInferenceProfileColumnNamesAsync(dbContext).ConfigureAwait(false);
        AssertEx.False(columns.Contains("launch_policy_fingerprint_version", StringComparer.Ordinal));
        AssertEx.False(columns.Contains("launch_policy_fingerprint", StringComparer.Ordinal));
        AssertEx.True(columns.Contains("status", StringComparer.Ordinal));
        AssertEx.True(columns.Contains("free_vram_at_freeze_bytes", StringComparer.Ordinal));
    }

    private ServiceProvider BuildProvider()
    {
        Directory.CreateDirectory(_rootPath);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(
            options => options.UseSqlite($"Data Source={Path.Combine(_rootPath, "fingerprint-migration.sqlite")}"));
        return services.BuildServiceProvider(true);
    }

    private static async Task InsertLegacyProfileAsync(
        NodeChatDbContext dbContext,
        Guid profileId,
        Guid benchmarkId)
    {
        var connection = await GetOpenConnectionAsync(dbContext).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO inference_profiles (
                                  id, machine_key, model_name, role, backend, llamacpp_build, quant, ctx_size,
                                  n_gpu_layers, tensor_split, override_tensor, kv_type_k, kv_type_v, flash_attn,
                                  n_params, is_moe, expert_count, free_vram_at_freeze_bytes, status,
                                  benchmark_snapshot_id, created_at_utc, updated_at_utc)
                              VALUES (
                                  $id, 'machine', 'owner/model:Q4_K_M', 0, 'cuda', 'b9999', 'Q4_K_M', 4096,
                                  33, NULL, NULL, 'q8_0', 'q8_0', 1,
                                  7000000000, 0, NULL, 8589934592, 1,
                                  $benchmark_id, 1, 1);
                              """;
        AddParameter(command, "$id", profileId.ToString());
        AddParameter(command, "$benchmark_id", benchmarkId.ToString());
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<(
        int Status,
        string? BenchmarkSnapshotId,
        int? FingerprintVersion,
        string? Fingerprint)> ReadProfileStateAsync(
        NodeChatDbContext dbContext,
        Guid profileId)
    {
        var connection = await GetOpenConnectionAsync(dbContext).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT status, benchmark_snapshot_id, launch_policy_fingerprint_version,
                                     launch_policy_fingerprint
                              FROM inference_profiles
                              WHERE id = $id;
                              """;
        AddParameter(command, "$id", profileId.ToString());
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        AssertEx.True(await reader.ReadAsync().ConfigureAwait(false));
        return (await reader.GetFieldValueAsync<int>(0).ConfigureAwait(false),
            await reader.IsDBNullAsync(1).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<string>(1).ConfigureAwait(false),
            await reader.IsDBNullAsync(2).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<int>(2).ConfigureAwait(false),
            await reader.IsDBNullAsync(3).ConfigureAwait(false)
                ? null
                : await reader.GetFieldValueAsync<string>(3).ConfigureAwait(false));
    }

    private static async Task<IReadOnlySet<string>> ReadInferenceProfileColumnNamesAsync(
        NodeChatDbContext dbContext)
    {
        var connection = await GetOpenConnectionAsync(dbContext).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('inference_profiles');";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add(await reader.GetFieldValueAsync<string>(1).ConfigureAwait(false));
        }

        return columns;
    }

    private static async Task<DbConnection> GetOpenConnectionAsync(NodeChatDbContext dbContext)
    {
        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        return connection;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
