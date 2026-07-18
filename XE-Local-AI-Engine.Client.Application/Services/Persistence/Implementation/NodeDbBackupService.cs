namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Default <see cref="INodeDbBackupService" />. Before pending node-chat migrations run, takes a <c>VACUUM INTO</c>
///     snapshot of the node database and prunes older snapshots to the retention count.
/// </summary>
/// <remarks>
///     The node database is a plain SQLite file (bundle <c>e_sqlite3</c>): sensitive columns are protected with
///     application-level encryption via <c>INodeSqliteKeyHolder</c>, so a <c>VACUUM INTO</c> copy carries the same
///     ciphertext columns and needs no separate key — there is no SQLCipher whole-file <c>PRAGMA key</c> on this connection.
///     <c>VACUUM INTO</c> produces a single consistent snapshot (it also folds any WAL content into the copy) and cannot run
///     inside a transaction, so it is executed directly on the connection rather than through an EF transaction scope.
/// </remarks>
public sealed class NodeDbBackupService : INodeDbBackupService
{
    private const string BackupFilePrefix = "node-chat-";
    private const string BackupFileExtension = ".sqlite";
    private const string DefaultBackupSubdirectory = "backups";

    private readonly ILogger<NodeDbBackupService> _logger;
    private readonly INodeDataDirectory _nodeDataDirectory;
    private readonly NodeDbBackupOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public NodeDbBackupService(IServiceScopeFactory scopeFactory,
        INodeDataDirectory nodeDataDirectory,
        TimeProvider timeProvider,
        IOptions<NodeDbBackupOptions> options,
        ILogger<NodeDbBackupService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _nodeDataDirectory = nodeDataDirectory ?? throw new ArgumentNullException(nameof(nodeDataDirectory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task BackupBeforeMigrationAsync(CancellationToken cancellationToken = default)
    {
        // Availability over the guarantee (BE-06): a backup is a safety net, never a gate. Every failure below — an
        // unreachable DB, an unwritable backup dir, a VACUUM error — is logged at Error and swallowed so migration and
        // startup proceed regardless. Only genuine cancellation is allowed to propagate.
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

            var pendingMigrations = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false);
            if (!pendingMigrations.Any())
            {
                _logger.LogDebug("Node database has no pending migrations; skipping the pre-migration backup.");
                return;
            }

            var backupDirectory = ResolveBackupDirectory();
            Directory.CreateDirectory(backupDirectory);

            var destinationPath = BuildSnapshotPath(backupDirectory);
            await VacuumIntoAsync(dbContext, destinationPath, cancellationToken).ConfigureAwait(false);

            var snapshotBytes = new FileInfo(destinationPath).Length;
            _logger.LogInformation("Snapshotted the node database to {BackupPath} ({BackupBytes} bytes) before applying {PendingCount} pending migration(s).",
                destinationPath,
                snapshotBytes,
                pendingMigrations.Count());

            PruneOldSnapshots(backupDirectory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Deliberately broad and non-rethrowing: the backup must never block migration or brick startup.
            _logger.LogError(exception, "Pre-migration node database backup failed; continuing with migration without a fresh snapshot.");
        }
    }

    private string ResolveBackupDirectory()
    {
        return string.IsNullOrWhiteSpace(_options.BackupDirectory)
            ? Path.Combine(_nodeDataDirectory.Root, DefaultBackupSubdirectory)
            : _options.BackupDirectory;
    }

    private string BuildSnapshotPath(string backupDirectory)
    {
        // Filename-safe, invariant, lexicographically-sortable UTC timestamp — the sort order is also the chronological
        // order, which the retention prune relies on.
        var timestamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        return Path.Combine(backupDirectory, $"{BackupFilePrefix}{timestamp}{BackupFileExtension}");
    }

    private static async Task VacuumIntoAsync(NodeChatDbContext dbContext, string destinationPath, CancellationToken cancellationToken)
    {
        // VACUUM INTO cannot bind parameters and cannot run inside a transaction, so build the SQL literal ourselves and run
        // it directly on the connection. The path is derived from INodeDataDirectory + a sanitized timestamp (never user
        // input); we still escape single quotes so an unusual directory can't break out of the string literal.
        var escapedPath = destinationPath.Replace("'", "''", StringComparison.Ordinal);

        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            // VACUUM INTO takes no bind parameters (SQLite rejects a parameterized target path), so the destination must be an
            // inlined string literal. It is safe: the path is internally derived (never user input) and single-quote-escaped.
#pragma warning disable CA2100, S2077
            command.CommandText = $"VACUUM INTO '{escapedPath}';";
#pragma warning restore CA2100, S2077
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private void PruneOldSnapshots(string backupDirectory)
    {
        var snapshots = Directory.EnumerateFiles(backupDirectory, $"{BackupFilePrefix}*{BackupFileExtension}")
                                 .OrderByDescending(static path => Path.GetFileName(path), StringComparer.Ordinal)
                                 .ToList();

        var retain = Math.Max(1, _options.RetainCount);
        if (snapshots.Count <= retain)
        {
            return;
        }

        foreach (var stalePath in snapshots.Skip(retain))
        {
            try
            {
                File.Delete(stalePath);
                _logger.LogDebug("Pruned old node database snapshot {BackupPath}.", stalePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort prune: a snapshot we could not delete stays on disk until the next successful prune. Never fatal.
                _logger.LogWarning(exception, "Could not prune old node database snapshot {BackupPath}.", stalePath);
            }
        }
    }
}
