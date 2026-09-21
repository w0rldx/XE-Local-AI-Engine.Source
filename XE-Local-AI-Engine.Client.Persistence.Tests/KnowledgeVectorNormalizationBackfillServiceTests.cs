namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     The vector-normalization backfill rescales every stored chunk vector to unit L2 length in place. Because cosine is
///     scale-invariant this changes no ranking; it only enables the dot-product scoring path. These tests assert the core
///     pass normalizes non-zero rows (and leaves zero rows exactly zero), is idempotent, is resumable across small
///     batches, is a no-op on an empty database, and that the hosted service flips the in-memory latch and sets the
///     durable marker so a second run skips the work but still latches.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class KnowledgeVectorNormalizationBackfillServiceTests : IDisposable
{
    private const string EmbeddingModel = "nomic-embed-text";

    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
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
    public async Task NormalizeVectorsAsync_MakesEveryNonZeroRowUnitLength_AndLeavesZeroRowsUntouched()
    {
        var databasePath = GetDatabasePath("normalize-core.sqlite");
        await MigrateAsync(databasePath);

        var ids = new List<Guid>();
        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            ids.Add(await InsertAsync(connection, new[]
            {
                3f,
                4f,
                0f,
                0f
            })); // norm 5
            ids.Add(await InsertAsync(connection, new[]
            {
                -2f,
                2f,
                1f,
                0f
            }));
            ids.Add(await InsertAsync(connection, new[]
            {
                0f,
                0f,
                0f,
                0f
            })); // zero: untouched
        }

        long written;
        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            // Small batch to exercise rowid paging across multiple transactions.
            written = await KnowledgeVectorNormalizationBackfillService.NormalizeVectorsAsync(connection, batchSize: 2, CancellationToken.None);
        }

        AssertEx.Equal(expected: 2L, written);

        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            AssertEx.True(Math.Abs(await NormOfAsync(connection, ids[0]) - 1f) < 1e-5f, "First non-zero row must be unit length.");
            AssertEx.True(Math.Abs(await NormOfAsync(connection, ids[1]) - 1f) < 1e-5f, "Second non-zero row must be unit length.");
            AssertEx.True(await NormOfAsync(connection, ids[2]) == 0f, "The zero-magnitude row must be left exactly zero.");
        }
    }

    [Test]
    public async Task NormalizeVectorsAsync_IsIdempotent_WhenRunTwice()
    {
        var databasePath = GetDatabasePath("normalize-idempotent.sqlite");
        await MigrateAsync(databasePath);
        Guid id;
        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            id = await InsertAsync(connection, new[]
            {
                6f,
                8f,
                0f,
                0f
            });
        }

        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            _ = await KnowledgeVectorNormalizationBackfillService.NormalizeVectorsAsync(connection, batchSize: 64, CancellationToken.None);
            _ = await KnowledgeVectorNormalizationBackfillService.NormalizeVectorsAsync(connection, batchSize: 64, CancellationToken.None);
            AssertEx.True(Math.Abs(await NormOfAsync(connection, id) - 1f) < 1e-5f,
                "Re-normalizing an already-unit vector must leave it unit length (idempotent in effect).");
        }
    }

    [Test]
    public async Task NormalizeVectorsAsync_OnEmptyDatabase_WritesNothing()
    {
        var databasePath = GetDatabasePath("normalize-empty.sqlite");
        await MigrateAsync(databasePath);

        await using var connection = await OpenConnectionAsync(databasePath);
        var written = await KnowledgeVectorNormalizationBackfillService.NormalizeVectorsAsync(connection, batchSize: 64, CancellationToken.None);

        AssertEx.Equal(expected: 0L, written);
    }

    [Test]
    public async Task RunOnceAsync_NormalizesRows_SetsMarker_AndLatchesState()
    {
        var databasePath = GetDatabasePath("runonce.sqlite");
        await MigrateAsync(databasePath);
        Guid id;
        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            id = await InsertAsync(connection, new[]
            {
                5f,
                12f,
                0f,
                0f
            }); // norm 13
        }

        await using var provider = BuildProvider(databasePath);
        var state = new KnowledgeVectorNormalizationState();
        using var service = new KnowledgeVectorNormalizationBackfillService(provider.GetRequiredService<IServiceScopeFactory>(), state,
            NullLogger<KnowledgeVectorNormalizationBackfillService>.Instance);

        await service.RunOnceAsync(CancellationToken.None);

        AssertEx.True(state.IsComplete, "The state latch must flip after a completed pass.");
        await using (var connection = await OpenConnectionAsync(databasePath))
        {
            AssertEx.True(Math.Abs(await NormOfAsync(connection, id) - 1f) < 1e-5f, "The row must be normalized after RunOnce.");
            AssertEx.True(await IsMarkerSetAsync(connection), "The durable completion marker must be set.");
        }

        // Second run: marker already set → the state still latches (fresh state), proving the skip-but-latch path.
        var secondState = new KnowledgeVectorNormalizationState();
        using var secondService = new KnowledgeVectorNormalizationBackfillService(
            provider.GetRequiredService<IServiceScopeFactory>(), secondState, NullLogger<KnowledgeVectorNormalizationBackfillService>.Instance);
        await secondService.RunOnceAsync(CancellationToken.None);
        AssertEx.True(secondState.IsComplete, "A run that finds the marker already set must still latch the state.");
    }


    private ServiceProvider BuildProvider(string databasePath)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_keyHolder);
        services.AddDbContext<NodeChatDbContext>(options => options
                                                            .UseSqlite($"Data Source={databasePath}")
                                                            .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
        return services.BuildServiceProvider();
    }

    private static async Task<Guid> InsertAsync(SqliteConnection connection, float[] vector)
    {
        var chunkId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        await KnowledgeVectorParents.EnsureAsync(connection, chunkId, documentId);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO knowledge_chunk_vectors (chunk_id, document_id, dim, embedding, embedding_model) VALUES ($cid, $did, $dim, $blob, $model);";
        command.Parameters.AddWithValue("$cid", chunkId);
        command.Parameters.AddWithValue("$did", documentId);
        command.Parameters.AddWithValue("$dim", vector.Length);
        command.Parameters.AddWithValue("$blob", MemoryMarshal.AsBytes<float>(vector).ToArray());
        command.Parameters.AddWithValue("$model", EmbeddingModel);
        _ = await command.ExecuteNonQueryAsync();
        return chunkId;
    }

    private static async Task<float> NormOfAsync(SqliteConnection connection, Guid chunkId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT embedding FROM knowledge_chunk_vectors WHERE chunk_id = $cid;";
        command.Parameters.AddWithValue("$cid", chunkId);
        var blob = (byte[])(await command.ExecuteScalarAsync())!;
        return TensorPrimitives.Norm(MemoryMarshal.Cast<byte, float>(blob));
    }

    private static async Task<bool> IsMarkerSetAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM chat_maintenance_state WHERE name = 'knowledge_vector_normalization_v1');";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0;
    }

    // A copy of the shared at-head template, not a replay of the whole declared chain: this suite exercises a service
    // over the schema, never the migrator that produced it. See MigratedDatabaseTemplate.
    private static async Task MigrateAsync(string databasePath)
    {
        await MigratedDatabaseTemplate.CopyChatHeadAsync(databasePath);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        return connection;
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
