namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Startup background service that L2-normalizes any legacy, pre-normalization chunk vectors in place, so the managed
///     cosine search can score with a plain dot product.
/// </summary>
/// <remarks>
///     New writes are normalized at ingestion; this is the one-time backfill for rows written before that, and it never
///     alters ranking. Completion is recorded by a durable marker row in <c>chat_maintenance_state</c>, the
///     one-shot-maintenance table the content-encryption backfill uses — plain, so <c>VACUUM</c> preserves it. Batches are
///     bounded, paged by <c>rowid</c> and committed independently, so an interrupted run leaves earlier batches normalized,
///     the marker unset, and the next startup re-runs; re-normalizing a unit vector is a no-op within a float ULP.
/// </remarks>
public sealed class KnowledgeVectorNormalizationBackfillService : BackgroundService
{
    internal const int DefaultBatchSize = 500;

    // One-shot completion marker, mirroring NodeChatContentEncryptionBackfillService's use of the same table: the row's
    // presence means every stored vector is normalized. Suffixed v1 so a new normalization definition can take a new key.
    private const string MarkerName = "knowledge_vector_normalization_v1";

    private const string IsMarkerSetSql = "SELECT EXISTS(SELECT 1 FROM chat_maintenance_state WHERE name = $name);";

    private const string SetMarkerSql =
        "INSERT INTO chat_maintenance_state (name, value) VALUES ($name, '1') ON CONFLICT(name) DO UPDATE SET value = '1';";

    private const string SelectBatchSql = """
                                          SELECT rowid, embedding
                                          FROM knowledge_chunk_vectors
                                          WHERE rowid > $cursor
                                          ORDER BY rowid
                                          LIMIT $limit;
                                          """;

    private const string UpdateEmbeddingSql = "UPDATE knowledge_chunk_vectors SET embedding = $embedding WHERE rowid = $rowid;";
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKnowledgeVectorNormalizationState _normalizationState;
    private readonly ILogger<KnowledgeVectorNormalizationBackfillService> _logger;

    public KnowledgeVectorNormalizationBackfillService(IServiceScopeFactory scopeFactory,
        IKnowledgeVectorNormalizationState normalizationState,
        ILogger<KnowledgeVectorNormalizationBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _normalizationState = normalizationState;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return RunOnceAsync(stoppingToken);
    }

    /// <summary>
    ///     Runs one startup pass: latches the in-memory state when the durable marker shows a prior run finished,
    ///     otherwise normalizes every stored vector in batches, sets the marker, and latches the state.
    /// </summary>
    /// <remarks>
    ///     Internal so a test can drive one deterministic pass. Never throws: cancellation is swallowed and unexpected
    ///     errors are logged, with the marker left unset so the next startup retries.
    /// </remarks>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await IsMarkerSetAsync(cancellationToken))
            {
                _normalizationState.MarkComplete();
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var connection = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection();
            await OpenIfNeededAsync(connection, cancellationToken);

            var normalized = await NormalizeVectorsAsync(connection, DefaultBatchSize, cancellationToken);

            await SetMarkerAsync(cancellationToken);
            _normalizationState.MarkComplete();

            if (normalized > 0)
            {
                _logger.LogInformation("KnowledgeVectorNormalizationBackfillService: normalized {Count} stored chunk vector(s).", normalized);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — committed batches persist, the marker stays unset, and the pass resumes on the next startup.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "KnowledgeVectorNormalizationBackfillService: unexpected error during vector normalization backfill.");
        }
    }

    /// <summary>
    ///     Normalizes every row of <c>knowledge_chunk_vectors</c> to unit L2 length in place, in
    ///     <paramref name="batchSize" /> batches paged by <c>rowid</c>, each committed in its own transaction.
    /// </summary>
    /// <remarks>
    ///     Zero-magnitude vectors are left exactly as they are, carrying no direction to normalize. Returns the number of
    ///     rows written. Internal and static so a test can drive it directly against a raw connection.
    /// </remarks>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every statement is a fixed internal constant with bound parameters; no value is concatenated into the command text.")]
    internal static async Task<long> NormalizeVectorsAsync(DbConnection connection, int batchSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var effectiveBatch = Math.Max(1, batchSize);

        long cursor = 0;
        long written = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await ReadBatchAsync(connection, cursor, effectiveBatch, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var (rowId, embedding) in batch)
            {
                cursor = rowId;
                if (!KnowledgeVectorMath.NormalizeBytesInPlace(embedding))
                {
                    // Zero-magnitude or malformed (non-float-width) blob: nothing to rescale, leave the row untouched.
                    continue;
                }

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = UpdateEmbeddingSql;
                AddParameter(update, "$embedding", embedding);
                AddParameter(update, "$rowid", rowId);
                _ = await update.ExecuteNonQueryAsync(cancellationToken);
                written++;
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return written;
    }

    private static async Task<List<EmbeddingRow>> ReadBatchAsync(DbConnection connection,
        long cursor,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SelectBatchSql;
        AddParameter(command, "$cursor", cursor);
        AddParameter(command, "$limit", batchSize);

        var rows = new List<EmbeddingRow>(batchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rowId = reader.GetInt64(0);
            var embedding = await reader.GetFieldValueAsync<byte[]>(ordinal: 1, cancellationToken);
            rows.Add(new EmbeddingRow(rowId, embedding));
        }

        return rows;
    }

    private async Task<bool> IsMarkerSetAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = IsMarkerSetSql;
        AddParameter(command, "$name", MarkerName);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;
    }

    private async Task SetMarkerAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = SetMarkerSql;
        AddParameter(command, "$name", MarkerName);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // One stored chunk vector read for rescaling: its rowid (also the paging cursor) and the raw float blob, which is
    // normalized in place before the row is written back.
    private sealed record EmbeddingRow(long RowId, byte[] Embedding);
}
