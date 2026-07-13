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

    // Minimal projection for the raw candidate query. Content is NOT NULL; metadata_json is nullable.
    private sealed record LegacyMessageRow(Guid MessageId, Guid ConversationId, byte[] Content, byte[]? MetadataJson);
}
