namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static XE_Local_AI_Engine.Client.Services.Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Startup background service that upgrades legacy plaintext message rows to the encrypted at-rest envelope. Before
///     content encryption shipped, the raw-ADO persistence path wrote the <c>content</c> and <c>metadata_json</c>
///     columns as plaintext UTF-8; this service re-writes every such row as an authenticated-encrypted envelope so no
///     recognizable chat text remains on disk. Encrypted rows are read-both, so a partially-migrated table stays fully
///     readable throughout.
/// </summary>
/// <remarks>
///     Work is done in bounded batches, each committed in its own transaction, so an interrupted run leaves earlier
///     batches durably migrated and the remaining plaintext rows are picked up on the next batch or the next startup.
///     The candidate query filters on the two-byte envelope header, so a re-run over an already-migrated table selects
///     nothing (idempotent). Runs once per startup, mirroring <see cref="NodeChatTitleEncryptionBackfillService" />.
/// </remarks>
public sealed class NodeChatContentEncryptionBackfillService(
    IServiceScopeFactory scopeFactory,
    ILogger<NodeChatContentEncryptionBackfillService> logger) : BackgroundService
{
    internal const int DefaultBatchSize = 200;

    // A row still needs migrating when its content OR its (present) metadata blob lacks the 0xFE 0x01 envelope header.
    // A blob shorter than two bytes (e.g. an empty placeholder) is treated as unencrypted. x'FE01' is the header bytes.
    private const string CandidateSelectSql = """
                                              SELECT message_id AS MessageId, conversation_id AS ConversationId, content AS Content, metadata_json AS MetadataJson
                                              FROM messages
                                              WHERE (length(content) < 2 OR substr(content, 1, 2) <> x'FE01')
                                                 OR (metadata_json IS NOT NULL AND (length(metadata_json) < 2 OR substr(metadata_json, 1, 2) <> x'FE01'))
                                              LIMIT {0}
                                              """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var total = await MigrateAllAsync(DefaultBatchSize, stoppingToken).ConfigureAwait(false);
            if (total > 0)
            {
                logger.LogInformation("NodeChatContentEncryptionBackfillService: encrypted {Count} legacy plaintext message row(s).", total);

                // Rewriting a row in place leaves the old plaintext bytes lingering in the SQLite journal/WAL and in
                // freed pages of the main file. Reclaim that residue once, only when this run actually migrated rows:
                // a WAL checkpoint-truncate collapses the log back into the main DB, then VACUUM rebuilds the file so
                // the freed pages holding plaintext are physically dropped, then a final checkpoint flushes VACUUM's
                // own writes to the main file. If retention leaves nothing to migrate on a later start, this is skipped.
                await CheckpointAndVacuumAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — committed batches persist; the rest resume on the next startup.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NodeChatContentEncryptionBackfillService: unexpected error during content-encryption backfill.");
        }
    }

    /// <summary>
    ///     Migrates every remaining legacy plaintext row in successive batches until none remain (or cancellation).
    ///     Returns the total number of rows migrated.
    /// </summary>
    internal async Task<int> MigrateAllAsync(int batchSize, CancellationToken cancellationToken)
    {
        var total = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var migrated = await MigrateBatchAsync(batchSize, cancellationToken).ConfigureAwait(false);
            if (migrated == 0)
            {
                break;
            }

            total += migrated;
        }

        return total;
    }

    /// <summary>
    ///     Migrates a single batch of legacy plaintext rows inside one transaction. Returns the number of rows migrated;
    ///     0 means no legacy rows remain. Each row is re-written idempotently via the db context's ensure-encrypted
    ///     helpers, so a row already carrying the envelope is left byte-identical.
    /// </summary>
    internal async Task<int> MigrateBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var rows = await dbContext.Database
                                  .SqlQueryRaw<LegacyMessageRow>(CandidateSelectSql, batchSize)
                                  .ToListAsync(cancellationToken)
                                  .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return 0;
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            var encryptedContent = dbContext.EnsureMessageContentEncrypted(row.Content, row.ConversationId, row.MessageId);
            var encryptedMetadata = dbContext.EnsureMessageMetadataEncrypted(row.MetadataJson, row.ConversationId, row.MessageId);

            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = "UPDATE messages SET content = $content, metadata_json = $metadata_json WHERE message_id = $message_id;";
            AddParameter(command, "$content", encryptedContent);
            AddParameter(command, "$metadata_json", encryptedMetadata);
            AddParameter(command, "$message_id", row.MessageId);
            await OpenIfNeededAsync(command.Connection, cancellationToken).ConfigureAwait(false);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }

    /// <summary>
    ///     Reclaims on-disk plaintext residue left behind by the in-place row rewrites: checkpoint-truncate the WAL,
    ///     VACUUM the database (which cannot run inside a transaction, so it uses the raw connection with no ambient
    ///     transaction), then checkpoint again so VACUUM's rebuild lands in the main file. A failure here must never
    ///     crash the background service — the rows are already encrypted; only the residue cleanup is skipped, and it is
    ///     retried on the next startup that migrates at least one row. Internal so a test can drive it deterministically.
    /// </summary>
    internal async Task CheckpointAndVacuumAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            var connection = dbContext.Database.GetDbConnection();
            await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

            await ExecuteRawAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
            await ExecuteRawAsync(connection, "VACUUM;", cancellationToken).ConfigureAwait(false);
            await ExecuteRawAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — the encrypted rows are durable; residue reclamation retries on the next qualifying start.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "NodeChatContentEncryptionBackfillService: post-backfill checkpoint/vacuum failed; encrypted rows are durable but plaintext residue reclamation was skipped and will retry on the next startup that migrates rows.");
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every call site passes a fixed internal maintenance statement (PRAGMA/VACUUM) — never user input.")]
    private static async Task ExecuteRawAsync(System.Data.Common.DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Minimal projection for the raw candidate query. Content is NOT NULL; metadata_json is nullable.
    private sealed record LegacyMessageRow(Guid MessageId, Guid ConversationId, byte[] Content, byte[]? MetadataJson);
}
