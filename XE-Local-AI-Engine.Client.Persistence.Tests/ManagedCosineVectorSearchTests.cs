namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Data;
using System.Data.Common;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     The managed cosine vector search streams stored <c>float32</c> BLOBs from <c>knowledge_chunk_vectors</c>,
///     reinterprets each as a span without a copy, and scores it against the query vector. These tests exercise the real
///     BLOB round-trip on the runtime SQLite connection: an identical stored vector scores ~1, and a vector whose
///     dimension differs from the query (a model/dim mismatch that slips past the model filter) is skipped rather than
///     ranked. Foreign-key enforcement is OFF at runtime, so orphan vector rows are a valid minimal fixture here.
/// </summary>
public sealed class ManagedCosineVectorSearchTests : IDisposable
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
    public async Task SearchAsync_WhenStoredVectorMatchesTheQuery_ReturnsItWithScoreNearOne()
    {
        var databasePath = GetDatabasePath("cosine-match.sqlite");
        var chunkId = Guid.NewGuid();
        await MigrateAsync(databasePath).ConfigureAwait(false);

        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            await InsertVectorAsync(connection, chunkId, Guid.NewGuid(), FloatBytes(1f, 0f, 0f, 0f)).ConfigureAwait(false);
        }

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
        var search = new ManagedCosineVectorSearch(context);

        var hits = await search.SearchAsync(new float[]
                               {
                                   1f,
                                   0f,
                                   0f,
                                   0f
                               }, EmbeddingModel, limit: 10, documentId: null, CancellationToken.None)
                               .ConfigureAwait(false);

        AssertEx.Equal(expected: 1, hits.Count);
        AssertEx.True(hits[0].ChunkId == chunkId && hits[0].Score > 0.999f,
            "An identical stored vector should round-trip through the BLOB and score cosine ~1.");
    }

    [Test]
    public async Task SearchAsync_WhenStoredVectorDimensionDiffersFromQuery_SkipsIt()
    {
        var databasePath = GetDatabasePath("cosine-dim-mismatch.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);

        // The stored vector keeps the same embedding model so it passes the model filter, but it has three dimensions
        // against a four-dimension query and must be skipped rather than scored.
        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            await InsertVectorAsync(connection, Guid.NewGuid(), Guid.NewGuid(), FloatBytes(1f, 0f, 0f)).ConfigureAwait(false);
        }

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
        var search = new ManagedCosineVectorSearch(context);

        var hits = await search.SearchAsync(new float[]
                               {
                                   1f,
                                   0f,
                                   0f,
                                   0f
                               }, EmbeddingModel, limit: 10, documentId: null, CancellationToken.None)
                               .ConfigureAwait(false);

        AssertEx.Empty(hits);
    }

    private static byte[] FloatBytes(params float[] values)
    {
        return MemoryMarshal.AsBytes<float>(values).ToArray();
    }

    private async Task MigrateAsync(string databasePath)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await context.Database.MigrateAsync().ConfigureAwait(false);
    }

    private static async Task InsertVectorAsync(SqliteConnection connection, Guid chunkId, Guid documentId, byte[] embedding)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO knowledge_chunk_vectors (chunk_id, document_id, dim, embedding, embedding_model) VALUES ($cid, $did, $dim, $blob, $model);";
        command.Parameters.AddWithValue("$cid", chunkId);
        command.Parameters.AddWithValue("$did", documentId);
        command.Parameters.AddWithValue("$dim", embedding.Length / sizeof(float));
        command.Parameters.AddWithValue("$blob", embedding);
        command.Parameters.AddWithValue("$model", EmbeddingModel);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await EnsureForeignKeysOffAsync(connection).ConfigureAwait(false);
        return connection;
    }

    // Microsoft.Data.Sqlite enables foreign-key enforcement by default; the node-sqlite runtime connection does not,
    // and an orphan vector row (no parent document/chunk) is a valid minimal fixture only under that runtime mode.
    private static async Task EnsureForeignKeysOffAsync(DbConnection connection)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF;";
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }
}
