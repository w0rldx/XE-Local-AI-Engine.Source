namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

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
    // The same WHERE predicate is mirrored in HasCandidatesSql (an EXISTS probe) below — keep the two in sync.
    private const string CandidateSelectSql = """
                                              SELECT message_id AS MessageId, conversation_id AS ConversationId, content AS Content, metadata_json AS MetadataJson
                                              FROM messages
                                              WHERE (length(content) < 2 OR substr(content, 1, 2) <> x'FE01')
                                                 OR (metadata_json IS NOT NULL AND (length(metadata_json) < 2 OR substr(metadata_json, 1, 2) <> x'FE01'))
                                              LIMIT {0}
                                              """;

    // Durable node-local state lives in the modeled `chat_maintenance_state` key/value table (entity
    // ChatMaintenanceState + migration AddChatMaintenanceState), so the "reclamation still owed" fact survives a restart
    // and is consistent with the data it guards. It is a plain table (not PRAGMA user_version) so VACUUM preserves it
    // deterministically. Reads/writes stay raw-SQL to match this service's raw-connection style; the table is guaranteed
    // to exist because chat migrations are applied at startup before any hosted service runs (Program.cs).
    private const string MaintenanceStateName = "content_encryption_reclaim_pending";

    private const string SetMarkerSql = "INSERT INTO chat_maintenance_state (name, value) VALUES ($name, '1') ON CONFLICT(name) DO UPDATE SET value = '1';";

    private const string ClearMarkerSql = "DELETE FROM chat_maintenance_state WHERE name = $name;";

    private const string IsMarkerSetSql = "SELECT EXISTS(SELECT 1 FROM chat_maintenance_state WHERE name = $name);";

    // EXISTS probe using the same predicate as CandidateSelectSql (kept in sync deliberately): are there legacy rows to
    // migrate? Drives whether the reclamation marker is set this run.
    private const string HasCandidatesSql = """
                                            SELECT EXISTS(
                                                SELECT 1 FROM messages
                                                WHERE (length(content) < 2 OR substr(content, 1, 2) <> x'FE01')
                                                   OR (metadata_json IS NOT NULL AND (length(metadata_json) < 2 OR substr(metadata_json, 1, 2) <> x'FE01'))
                                            );
                                            """;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return RunOnceAsync(stoppingToken);
    }

    /// <summary>
    ///     Runs one startup pass: migrate any legacy plaintext rows, then reclaim the on-disk plaintext residue those
    ///     rewrites leave behind. Reclamation is guarded by a durable "reclamation pending" marker so a failed or
    ///     interrupted cleanup is retried on every subsequent startup until it succeeds — not silently abandoned once the
    ///     rows are encrypted and there are no candidates left to trigger it. Internal so a test can drive one
    ///     deterministic pass. Never throws: shutdown and unexpected errors are logged and swallowed.
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reclamationPendingFromPreviousRun = await IsReclamationPendingAsync(cancellationToken).ConfigureAwait(false);
            var hasLegacyCandidates = await HasLegacyCandidatesAsync(cancellationToken).ConfigureAwait(false);

            // Set the durable marker BEFORE any legacy row is re-encrypted, so a failure or shutdown during the single
            // reclamation pass below cannot lose the fact that plaintext residue still has to be reclaimed. It is
            // committed on its own connection, so it is durable independently of (and prior to) the migration commits.
            if (hasLegacyCandidates)
            {
                await SetReclamationPendingAsync(cancellationToken).ConfigureAwait(false);
            }

            var total = await MigrateAllAsync(DefaultBatchSize, cancellationToken).ConfigureAwait(false);
            if (total > 0)
            {
                logger.LogInformation("NodeChatContentEncryptionBackfillService: encrypted {Count} legacy plaintext message row(s).", total);
            }

            // Reclaim residue whenever this run migrated rows OR a previous run's reclamation never completed (marker
            // still set) — an idempotent retry until the checkpoint/VACUUM pass finally succeeds. Clear the marker only
            // on success; a failure/cancellation leaves it set so the next startup retries.
            if ((hasLegacyCandidates || reclamationPendingFromPreviousRun)
                && await CheckpointAndVacuumAsync(cancellationToken).ConfigureAwait(false))
            {
                await ClearReclamationPendingAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — committed batches and the durable marker persist; migration and reclamation resume on
            // the next startup.
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
    ///     transaction), then checkpoint again so VACUUM's rebuild lands in the main file. Returns <see langword="true" />
    ///     only when the whole pass succeeded; on any failure or cancellation it logs and returns <see langword="false" />
    ///     so the caller leaves the durable "reclamation pending" marker set and retries next startup. A failure here
    ///     must never crash the background service — the rows are already encrypted. Internal so a test can drive it.
    /// </summary>
    internal async Task<bool> CheckpointAndVacuumAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            var connection = dbContext.Database.GetDbConnection();
            await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

            // A checkpoint that reports busy or leaves frames behind must NOT be treated as success: proceeding to
            // VACUUM and clearing the marker on an incomplete truncate could leave plaintext-bearing WAL frames on disk
            // with the marker permanently cleared. Bail out (keep the marker) so the next startup retries.
            if (!await CheckpointTruncatedFullyAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                LogIncompleteCheckpoint("before VACUUM");
                return false;
            }

            await ExecuteRawAsync(connection, "VACUUM;", cancellationToken).ConfigureAwait(false);

            // The second checkpoint flushes VACUUM's own rebuild into the main file; if it does not fully truncate, the
            // rebuilt (residue-free) pages may still be sitting in the WAL, so the reclamation is not yet complete.
            if (!await CheckpointTruncatedFullyAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                LogIncompleteCheckpoint("after VACUUM");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — the encrypted rows are durable and the marker stays set, so reclamation retries on the
            // next startup.
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "NodeChatContentEncryptionBackfillService: post-backfill checkpoint/vacuum failed; encrypted rows are durable, the reclamation-pending marker remains set, and reclamation will be retried on the next startup.");
            return false;
        }
    }

    // Runs PRAGMA wal_checkpoint(TRUNCATE) and inspects its result row (busy, log, checkpointed). The pragma does NOT
    // reliably throw when it cannot complete: busy != 0 means SQLITE_BUSY (a concurrent reader blocked the truncate) and
    // log > 0 with checkpointed < log means WAL frames were left behind — both are cleanup failures. The node default is
    // now WAL (AUD4-08 / NodeSqlitePragmas), so this truncate actually reclaims the plaintext-bearing WAL frames; if WAL
    // could not be enabled and the file is still in a non-WAL journal, the pragma is a no-op returning (0, -1, -1) — also
    // treated as success.
    private static async Task<bool> CheckpointTruncatedFullyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var busy = reader.GetInt64(0);
        var log = reader.GetInt64(1);
        var checkpointed = reader.GetInt64(2);
        return busy == 0 && !(log > 0 && checkpointed < log);
    }

    private void LogIncompleteCheckpoint(string phase)
    {
        logger.LogWarning(
            "NodeChatContentEncryptionBackfillService: WAL checkpoint {Phase} did not fully truncate (busy or frames remaining); the reclamation-pending marker remains set and reclamation will be retried on the next startup.",
            phase);
    }

    private Task<bool> HasLegacyCandidatesAsync(CancellationToken cancellationToken)
    {
        return ExistsAsync(HasCandidatesSql, addMarkerName: false, cancellationToken);
    }

    private Task<bool> IsReclamationPendingAsync(CancellationToken cancellationToken)
    {
        return ExistsAsync(IsMarkerSetSql, addMarkerName: true, cancellationToken);
    }

    private Task SetReclamationPendingAsync(CancellationToken cancellationToken)
    {
        return ExecuteMarkerNonQueryAsync(SetMarkerSql, cancellationToken);
    }

    private Task ClearReclamationPendingAsync(CancellationToken cancellationToken)
    {
        return ExecuteMarkerNonQueryAsync(ClearMarkerSql, cancellationToken);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every call site passes a fixed internal maintenance query constant — never user input.")]
    private async Task<bool> ExistsAsync(string sql, bool addMarkerName, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (addMarkerName)
        {
            AddParameter(command, "$name", MaintenanceStateName);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture) != 0;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every call site passes a fixed internal maintenance statement constant — never user input.")]
    private async Task ExecuteMarkerNonQueryAsync(string sql, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "$name", MaintenanceStateName);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every call site passes a fixed internal maintenance statement (PRAGMA/VACUUM) — never user input.")]
    private static async Task ExecuteRawAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // Minimal projection for the raw candidate query. Content is NOT NULL; metadata_json is nullable.
    private sealed record LegacyMessageRow(Guid MessageId, Guid ConversationId, byte[] Content, byte[]? MetadataJson);
}
