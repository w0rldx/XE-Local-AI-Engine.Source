namespace XE_Local_AI_Engine.Client.Persistence.Tests;

using System.Data;
using System.Data.Common;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     The managed cosine vector search streams stored <c>float32</c> BLOBs from <c>knowledge_chunk_vectors</c>,
///     reinterprets each over a reused pooled buffer, scores it against the query vector, and keeps the top-k in a bounded
///     heap. These tests exercise the real BLOB round-trip on the runtime SQLite connection and assert the optimized
///     search returns the IDENTICAL ranking (ids, order, scores within 1e-5) as a naive reference cosine — on both the
///     cosine path (before the normalization backfill) and the dot-product path (after it), including legacy unnormalized
///     rows, top-k boundaries, ties, zero vectors, and cancellation. Foreign-key enforcement is OFF at runtime, so orphan
///     vector rows are a valid minimal fixture here.
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

        var hits = await RunSearchAsync(databasePath, normalized: false, new[] { 1f, 0f, 0f, 0f }, limit: 10).ConfigureAwait(false);

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

        var hits = await RunSearchAsync(databasePath, normalized: false, new[] { 1f, 0f, 0f, 0f }, limit: 10).ConfigureAwait(false);

        AssertEx.Empty(hits);
    }

    [Test]
    public async Task SearchAsync_CosinePath_MatchesNaiveReferenceRankingOnDeterministicCorpus()
    {
        var databasePath = GetDatabasePath("cosine-equivalence.sqlite");
        var corpus = await SeedDeterministicCorpusAsync(databasePath, count: 200, dimension: 16, seed: 1234).ConfigureAwait(false);
        var query = DeterministicVector(new Random(9999), dimension: 16);

        // Cosine path (state not complete): must equal the naive full-sort reference exactly.
        var hits = await RunSearchAsync(databasePath, normalized: false, query, limit: 10).ConfigureAwait(false);

        AssertRankingMatches(ReferenceCosineTopK(corpus, query, limit: 10), hits);
    }

    [Test]
    public async Task SearchAsync_DotPathAfterMigration_MatchesReferenceOnLegacyUnnormalizedCorpus()
    {
        var databasePath = GetDatabasePath("dot-equivalence.sqlite");
        // Seed LEGACY unnormalized vectors, capture the reference ranking over them, THEN normalize in place and search on
        // the dot-product path — the ranking must be byte-for-rank identical to the pre-migration reference.
        var corpus = await SeedDeterministicCorpusAsync(databasePath, count: 200, dimension: 16, seed: 4242).ConfigureAwait(false);
        var query = DeterministicVector(new Random(1357), dimension: 16);
        var reference = ReferenceCosineTopK(corpus, query, limit: 10);

        await NormalizeInPlaceAsync(databasePath).ConfigureAwait(false);

        var hits = await RunSearchAsync(databasePath, normalized: true, query, limit: 10).ConfigureAwait(false);

        AssertRankingMatches(reference, hits);
    }

    [Test]
    public async Task SearchAsync_TopKBoundaries_MatchReference_WhenLimitMeetsOrExceedsCorpus()
    {
        var databasePath = GetDatabasePath("topk-boundaries.sqlite");
        var corpus = await SeedDeterministicCorpusAsync(databasePath, count: 12, dimension: 8, seed: 77).ConfigureAwait(false);
        var query = DeterministicVector(new Random(5), dimension: 8);
        await NormalizeInPlaceAsync(databasePath).ConfigureAwait(false);

        // K == N and K > N both return every candidate in full-sort order.
        var atCount = await RunSearchAsync(databasePath, normalized: true, query, limit: 12).ConfigureAwait(false);
        AssertRankingMatches(ReferenceCosineTopK(corpus, query, limit: 12), atCount);

        var overCount = await RunSearchAsync(databasePath, normalized: true, query, limit: 50).ConfigureAwait(false);
        AssertRankingMatches(ReferenceCosineTopK(corpus, query, limit: 50), overCount);
        AssertEx.Equal(expected: 12, overCount.Count);
    }

    [Test]
    public async Task SearchAsync_WhenCandidatesTieOnScore_ReturnsTheExpectedTopKSet()
    {
        var databasePath = GetDatabasePath("topk-ties.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);

        // Three identical vectors (a perfect tie at cosine 1) plus one weaker vector. A limit of 2 must return two of the
        // three tied ids and never the weaker one.
        var tied = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var weakId = Guid.NewGuid();
        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            foreach (var id in tied)
            {
                await InsertVectorAsync(connection, id, Guid.NewGuid(), FloatBytes(1f, 0f, 0f, 0f)).ConfigureAwait(false);
            }

            await InsertVectorAsync(connection, weakId, Guid.NewGuid(), FloatBytes(0.1f, 1f, 0f, 0f)).ConfigureAwait(false);
        }

        await NormalizeInPlaceAsync(databasePath).ConfigureAwait(false);
        var hits = await RunSearchAsync(databasePath, normalized: true, new[] { 1f, 0f, 0f, 0f }, limit: 2).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, hits.Count);
        AssertEx.True(hits.All(hit => tied.Contains(hit.ChunkId)), "The top-2 of a 3-way tie must all come from the tied set, never the weaker vector.");
        AssertEx.True(hits[0].Score > 0.999f && hits[1].Score > 0.999f, "Both tied hits should score cosine ~1.");
    }

    [Test]
    public async Task SearchAsync_ZeroVector_IsExcluded_OnBothPaths()
    {
        var databasePath = GetDatabasePath("zero-vector.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);

        var zeroId = Guid.NewGuid();
        var realId = Guid.NewGuid();
        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            await InsertVectorAsync(connection, zeroId, Guid.NewGuid(), FloatBytes(0f, 0f, 0f, 0f)).ConfigureAwait(false);
            await InsertVectorAsync(connection, realId, Guid.NewGuid(), FloatBytes(0.2f, 0.9f, 0.1f, 0f)).ConfigureAwait(false);
        }

        // Cosine path: a zero-magnitude vector scores NaN and is skipped.
        var cosineHits = await RunSearchAsync(databasePath, normalized: false, new[] { 1f, 0f, 0f, 0f }, limit: 10).ConfigureAwait(false);
        AssertEx.True(cosineHits.All(hit => hit.ChunkId != zeroId), "The zero vector must be excluded on the cosine path.");

        // After migration the zero vector stays exactly zero; the dot path must skip it too (not score it a false 0).
        await NormalizeInPlaceAsync(databasePath).ConfigureAwait(false);
        var dotHits = await RunSearchAsync(databasePath, normalized: true, new[] { 1f, 0f, 0f, 0f }, limit: 10).ConfigureAwait(false);
        AssertEx.True(dotHits.All(hit => hit.ChunkId != zeroId), "The zero vector must be excluded on the dot path too.");
        AssertEx.True(dotHits.Any(hit => hit.ChunkId == realId), "The real vector should still be returned.");
    }

    [Test]
    public async Task SearchAsync_WhenQueryVectorIsZero_ReturnsEmptyOnDotPath()
    {
        var databasePath = GetDatabasePath("zero-query.sqlite");
        await MigrateAsync(databasePath).ConfigureAwait(false);
        await using (var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false))
        {
            await InsertVectorAsync(connection, Guid.NewGuid(), Guid.NewGuid(), FloatBytes(0.2f, 0.9f, 0.1f, 0f)).ConfigureAwait(false);
        }

        await NormalizeInPlaceAsync(databasePath).ConfigureAwait(false);

        // A zero-magnitude query has no direction — cosine would be NaN for every candidate (an empty result); the dot
        // path mirrors that by returning nothing rather than scoring everything 0.
        var hits = await RunSearchAsync(databasePath, normalized: true, new[] { 0f, 0f, 0f, 0f }, limit: 10).ConfigureAwait(false);
        AssertEx.Empty(hits);
    }

    [Test]
    public async Task SearchAsync_WhenTokenIsAlreadyCancelled_Throws()
    {
        var databasePath = GetDatabasePath("cancellation.sqlite");
        await SeedDeterministicCorpusAsync(databasePath, count: 20, dimension: 8, seed: 3).ConfigureAwait(false);

        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
        var search = new ManagedCosineVectorSearch(context, CompleteState());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync().ConfigureAwait(false);

        var threw = false;
        try
        {
            await search.SearchAsync(DeterministicVector(new Random(1), 8), EmbeddingModel, limit: 10, documentId: null, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        AssertEx.True(threw, "A pre-cancelled token must abort the search with OperationCanceledException.");
    }

    // ---- helpers ----

    private async Task<IReadOnlyList<VectorSearchHit>> RunSearchAsync(string databasePath, bool normalized, float[] query, int limit)
    {
        await using var context = AgentDefinitionTestContextFactory.CreateForMigration(databasePath, _keyHolder);
        await EnsureForeignKeysOffAsync(context.Database.GetDbConnection()).ConfigureAwait(false);
        var search = new ManagedCosineVectorSearch(context, normalized ? CompleteState() : new KnowledgeVectorNormalizationState());
        return await search.SearchAsync(query, EmbeddingModel, limit, documentId: null, CancellationToken.None).ConfigureAwait(false);
    }

    private static IKnowledgeVectorNormalizationState CompleteState()
    {
        var state = new KnowledgeVectorNormalizationState();
        state.MarkComplete();
        return state;
    }

    private async Task<List<(Guid ChunkId, float[] Vector)>> SeedDeterministicCorpusAsync(string databasePath, int count, int dimension, int seed)
    {
        await MigrateAsync(databasePath).ConfigureAwait(false);
        var random = new Random(seed);
        var corpus = new List<(Guid, float[])>(count);

        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            var chunkId = Guid.NewGuid();
            var vector = DeterministicVector(random, dimension);
            corpus.Add((chunkId, vector));
            await InsertVectorAsync(connection, chunkId, Guid.NewGuid(), MemoryMarshal.AsBytes<float>(vector).ToArray(), transaction).ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
        return corpus;
    }

    // Runs the real normalization backfill core against the stored rows, in place.
    private static async Task NormalizeInPlaceAsync(string databasePath)
    {
        await using var connection = await OpenConnectionAsync(databasePath).ConfigureAwait(false);
        _ = await KnowledgeVectorNormalizationBackfillService.NormalizeVectorsAsync(connection, batchSize: 64, CancellationToken.None).ConfigureAwait(false);
    }

    // Naive reference: cosine over the RAW stored vectors, skip zero-magnitude (NaN), order by score desc with a stable
    // tie-break on insertion order, take limit. This is the semantics the optimized search must reproduce exactly.
    private static List<(Guid ChunkId, float Score)> ReferenceCosineTopK(IReadOnlyList<(Guid ChunkId, float[] Vector)> corpus, float[] query, int limit)
    {
        var scored = new List<(Guid ChunkId, float Score, int Order)>();
        for (var index = 0; index < corpus.Count; index++)
        {
            var (chunkId, vector) = corpus[index];
            if (vector.Length != query.Length)
            {
                continue;
            }

            var score = TensorPrimitives.CosineSimilarity(query, vector);
            if (float.IsNaN(score))
            {
                continue;
            }

            scored.Add((chunkId, score, index));
        }

        return scored
               .OrderByDescending(entry => entry.Score)
               .ThenBy(entry => entry.Order)
               .Take(limit)
               .Select(entry => (entry.ChunkId, entry.Score))
               .ToList();
    }

    private static void AssertRankingMatches(IReadOnlyList<(Guid ChunkId, float Score)> reference, IReadOnlyList<VectorSearchHit> actual)
    {
        // Guard against accidental score ties in the random fixture that would make the order ambiguous (and the test
        // flaky): the reference scores must be strictly decreasing.
        for (var i = 1; i < reference.Count; i++)
        {
            AssertEx.True(reference[i - 1].Score > reference[i].Score,
                "The deterministic fixture must produce strictly-decreasing reference scores so the ranking order is unambiguous.");
        }

        AssertEx.Equal(reference.Count, actual.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            AssertEx.Equal(reference[i].ChunkId, actual[i].ChunkId);
            AssertEx.True(Math.Abs(reference[i].Score - actual[i].Score) < 1e-5f,
                $"Optimized score {actual[i].Score} must match reference {reference[i].Score} within 1e-5 at rank {i}.");
        }
    }

    private static float[] DeterministicVector(Random random, int dimension)
    {
        var vector = new float[dimension];
        for (var i = 0; i < dimension; i++)
        {
            // Centered in [-1, 1) so vectors point in varied directions (non-trivial cosine spread), never all-zero.
            vector[i] = (float)((random.NextDouble() * 2.0) - 1.0);
        }

        return vector;
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

    private static async Task InsertVectorAsync(SqliteConnection connection, Guid chunkId, Guid documentId, byte[] embedding, DbTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = (SqliteTransaction)transaction;
        }

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
