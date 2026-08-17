namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Startup background service that L2-normalizes any legacy (pre-normalization) chunk vectors in place so the managed
///     cosine search can score with a plain dot product. New writes are normalized at ingestion unconditionally; this is
///     the one-time backfill for rows written before that shipped. Because cosine similarity is scale-invariant,
///     rescaling a stored vector to unit length changes NO cosine result, so the pass is safe to run against a live corpus
///     and never alters ranking — it only enables the faster scoring path once complete.
/// </summary>
/// <remarks>
///     Completion is tracked by a durable marker row in <c>chat_maintenance_state</c> (the same one-shot-maintenance table
///     the content-encryption backfill uses; a plain table so <c>VACUUM</c> preserves it). The pass runs in bounded,
///     independently-committed batches paged by <c>rowid</c>, so an interrupted run leaves earlier batches durably
///     normalized and the marker unset; the next startup re-runs, and re-normalizing an already-unit vector is a no-op
///     within a float ULP (idempotent). Until the marker is set the in-memory
///     <see cref="IKnowledgeVectorNormalizationState" /> latch stays false and the search stays on the scale-invariant
///     cosine path, which is correct regardless of whether a given row is normalized yet. Safe on an empty database (no
///     rows → marker set immediately). Runs once per startup.
/// </remarks>
public sealed class KnowledgeVectorNormalizationBackfillService(
    IServiceScopeFactory scopeFactory,
    IKnowledgeVectorNormalizationState normalizationState,
    ILogger<KnowledgeVectorNormalizationBackfillService> logger) : BackgroundService
{
    internal const int DefaultBatchSize = 500;

    // One-shot completion marker, mirroring NodeChatContentEncryptionBackfillService's use of the same table. The row's
    // presence means "every stored vector for this database has been normalized"; its absence means the backfill still
    // has to run. Suffixed v1 so a future re-normalization (e.g. a different normalization definition) can use a new key.
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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return RunOnceAsync(stoppingToken);
    }

    /// <summary>
    ///     Runs one startup pass: if the durable marker shows a prior run already finished, just latch the in-memory state
    ///     and return; otherwise normalize every stored vector in batches, set the marker, and latch the state. Internal so
    ///     a test can drive one deterministic pass. Never throws: cancellation and unexpected errors are logged (or
    ///     swallowed for cancellation) and the marker is left unset so the next startup retries.
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await IsMarkerSetAsync(cancellationToken).ConfigureAwait(false))
            {
                normalizationState.MarkComplete();
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var connection = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection();
            await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

            var normalized = await NormalizeVectorsAsync(connection, DefaultBatchSize, cancellationToken).ConfigureAwait(false);

            await SetMarkerAsync(cancellationToken).ConfigureAwait(false);
            normalizationState.MarkComplete();

            if (normalized > 0)
            {
                logger.LogInformation("KnowledgeVectorNormalizationBackfillService: normalized {Count} stored chunk vector(s).", normalized);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — committed batches persist, the marker stays unset, and the pass resumes on the next startup.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "KnowledgeVectorNormalizationBackfillService: unexpected error during vector normalization backfill.");
        }
    }

    /// <summary>
    ///     Normalizes every row of <c>knowledge_chunk_vectors</c> to unit L2 length in place, in <paramref name="batchSize" />
    ///     batches paged by <c>rowid</c> and each committed in its own transaction. Zero-magnitude vectors are left exactly
    ///     as they are (they carry no direction). Returns the number of rows written. Internal + static so a test can drive
    ///     it directly against a raw connection.
    /// </summary>
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

            var batch = await ReadBatchAsync(connection, cursor, effectiveBatch, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
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
                _ = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                written++;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var rowId = reader.GetInt64(0);
            var embedding = await reader.GetFieldValueAsync<byte[]>(ordinal: 1, cancellationToken).ConfigureAwait(false);
            rows.Add(new EmbeddingRow(rowId, embedding));
        }

        return rows;
    }

    private async Task<bool> IsMarkerSetAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = IsMarkerSetSql;
        AddParameter(command, "$name", MarkerName);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;
    }

    private async Task SetMarkerAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = SetMarkerSql;
        AddParameter(command, "$name", MarkerName);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // One stored chunk vector read for rescaling: its rowid (also the paging cursor) and the raw float blob, which is
    // normalized in place before the row is written back.
    private sealed record EmbeddingRow(long RowId, byte[] Embedding);
}
