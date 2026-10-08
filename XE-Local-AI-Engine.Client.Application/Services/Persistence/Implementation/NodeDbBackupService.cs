namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SQLitePCL;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
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
    private const string BackupFilePrefix = NodeDbRestoreStaging.SnapshotFilePrefix;
    private const string BackupFileExtension = NodeDbRestoreStaging.SnapshotFileExtension;
    private const string TemporaryFileExtension = ".tmp";
    private const string SnapshotTimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    // VACUUM INTO writes a compacted copy, so the database size plus a margin is a safe upper bound for what it needs.
    private const double MinimumFreeSpaceRatio = 1.2;

    private readonly IFreeSpaceProbe _freeSpaceProbe;
    private readonly ILogger<NodeDbBackupService> _logger;
    private readonly INodeDataDirectory _nodeDataDirectory;
    private readonly NodeDbBackupOptions _options;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    private int _creating;
    private volatile NodeDbAutomaticBackupStatus _lastAutomaticBackup = NodeDbAutomaticBackupStatus.NotRun;

    public NodeDbBackupService(IServiceScopeFactory scopeFactory,
        INodeDataDirectory nodeDataDirectory,
        TimeProvider timeProvider,
        IOptions<NodeDbBackupOptions> options,
        INodeRuntimeSettings runtimeSettings,
        IFreeSpaceProbe freeSpaceProbe,
        ILogger<NodeDbBackupService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _nodeDataDirectory = nodeDataDirectory ?? throw new ArgumentNullException(nameof(nodeDataDirectory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _freeSpaceProbe = freeSpaceProbe ?? throw new ArgumentNullException(nameof(freeSpaceProbe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task BackupBeforeMigrationAsync(CancellationToken cancellationToken = default)
    {
        // Availability over the guarantee: a backup is a safety net, never a gate. Every failure below — an unreachable DB, an unwritable backup dir, a
        // VACUUM error — is logged at Error and swallowed so migration and startup proceed regardless. Only genuine cancellation is allowed to propagate.
        string? temporaryPath = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            var identityContext = scope.ServiceProvider.GetRequiredService<NodeIdentityDbContext>();

            // Both contexts share the one SQLite file but keep separate history tables, so an identity-only release needs the snapshot too.
            var pendingCount = (await dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Count()
                               + (await identityContext.Database.GetPendingMigrationsAsync(cancellationToken)).Count();
            if (pendingCount == 0)
            {
                _logger.LogDebug("Node database has no pending migrations; skipping the pre-migration backup.");
                return;
            }

            var backupDirectory = ResolveBackupDirectory();
            Directory.CreateDirectory(backupDirectory);

            if (!HasRoomForSnapshot(dbContext, backupDirectory))
            {
                RecordAutomaticBackup(NodeDbAutomaticBackupOutcome.Skipped, "There was not enough free disk space for the snapshot.");
                return;
            }

            // Written under a temporary name and renamed only when complete, so a failed or killed snapshot never counts as a retained one.
            var destinationPath = BuildSnapshotPath(backupDirectory);
            temporaryPath = destinationPath + TemporaryFileExtension;
            using var budget = new CancellationTokenSource(_options.SnapshotTimeout, _timeProvider);
            using var snapshotCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
            try
            {
                await VacuumIntoAsync(dbContext, temporaryPath, snapshotCancellation.Token);
            }
            catch (Exception exception) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                LogBudgetExceeded(exception);
                return;
            }

            // Checked again right before the rename: an interrupt that landed as the copy finished may have left it incomplete, and the
            // budget protects the shell deadline either way. The finally deletes the temporary file.
            if (budget.IsCancellationRequested)
            {
                LogBudgetExceeded(exception: null);
                return;
            }

            File.Move(temporaryPath, destinationPath);
            temporaryPath = null;

            var snapshotBytes = new FileInfo(destinationPath).Length;
            _logger.LogInformation("Snapshotted the node database to {BackupPath} ({BackupBytes} bytes) before applying {PendingCount} pending migration(s).",
                destinationPath,
                snapshotBytes,
                pendingCount);

            RecordAutomaticBackup(NodeDbAutomaticBackupOutcome.Succeeded, error: null);
            PruneOldSnapshots(backupDirectory, await _runtimeSettings.GetNodeDbBackupRetainCountAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Deliberately broad and non-rethrowing: the backup must never block migration or brick startup.
            _logger.LogError(exception, "Pre-migration node database backup failed; continuing with migration without a fresh snapshot.");
            if (_lastAutomaticBackup.Outcome != NodeDbAutomaticBackupOutcome.Succeeded)
            {
                RecordAutomaticBackup(NodeDbAutomaticBackupOutcome.Failed, $"The snapshot failed ({exception.GetType().Name}); the node log has the details.");
            }
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDelete(temporaryPath);
            }
        }
    }

    /// <inheritdoc />
    public string? FindNewestSnapshot()
    {
        try
        {
            var backupDirectory = ResolveBackupDirectory();
            return Directory.Exists(backupDirectory) ? ListCompleteSnapshots(backupDirectory).FirstOrDefault() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public NodeDbAutomaticBackupStatus LastAutomaticBackup => _lastAutomaticBackup;

    /// <inheritdoc />
    public IReadOnlyList<NodeDbSnapshot> ListSnapshots()
    {
        var backupDirectory = ResolveBackupDirectory();
        if (!Directory.Exists(backupDirectory))
        {
            return [];
        }

        return [.. ListCompleteSnapshots(backupDirectory).Select(ToSnapshot)];
    }

    /// <inheritdoc />
    public async Task<NodeDbRestoreStageResult> StageRestoreAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Interlocked.CompareExchange(ref _creating, 1, 0) != 0)
        {
            return new NodeDbRestoreStageResult
            {
                Status = NodeDbRestoreStageStatus.Busy
            };
        }

        try
        {
            var snapshotPath = ResolveSnapshotPath(name);
            if (snapshotPath is null)
            {
                return new NodeDbRestoreStageResult
                {
                    Status = NodeDbRestoreStageStatus.NotFound
                };
            }

            var backupDirectory = ResolveBackupDirectory();
            var refusal = NodeDbRestoreStaging.DescribeLinkedSnapshot(backupDirectory, snapshotPath)
                          ?? await NodeDbRestoreStaging.CheckSnapshotIntegrityAsync(snapshotPath, NodeDbRestoreStaging.ShippedMigrationIds, cancellationToken);
            if (refusal is not null)
            {
                return new NodeDbRestoreStageResult
                {
                    Status = NodeDbRestoreStageStatus.Refused,
                    Reason = refusal
                };
            }

            await NodeDbRestoreStaging.WriteMarkerAsync(backupDirectory, name, _timeProvider.GetUtcNow(), cancellationToken);
            _logger.LogInformation("Staged a restore of node database snapshot {SnapshotName}; the next start applies it.", name);
            return new NodeDbRestoreStageResult
            {
                Status = NodeDbRestoreStageStatus.Staged
            };
        }
        finally
        {
            Volatile.Write(ref _creating, 0);
        }
    }

    /// <inheritdoc />
    public string? ResolveSnapshotPath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var backupDirectory = ResolveBackupDirectory();
        return Directory.Exists(backupDirectory)
            ? ListCompleteSnapshots(backupDirectory).FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal))
            : null;
    }

    /// <inheritdoc />
    public async Task<NodeDbSnapshotCreateResult> CreateSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _creating, 1, 0) != 0)
        {
            return new NodeDbSnapshotCreateResult
            {
                Status = NodeDbSnapshotCreateStatus.Busy
            };
        }

        string? temporaryPath = null;
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            var dataSource = dbContext.Database.GetDbConnection().DataSource;
            if (string.IsNullOrWhiteSpace(dataSource) || string.Equals(dataSource, ":memory:", StringComparison.Ordinal))
            {
                return new NodeDbSnapshotCreateResult
                {
                    Status = NodeDbSnapshotCreateStatus.Unsupported
                };
            }

            var backupDirectory = ResolveBackupDirectory();
            Directory.CreateDirectory(backupDirectory);
            if (!HasRoomForSnapshot(dbContext, backupDirectory))
            {
                return new NodeDbSnapshotCreateResult
                {
                    Status = NodeDbSnapshotCreateStatus.InsufficientSpace
                };
            }

            var destinationPath = BuildSnapshotPath(backupDirectory);
            temporaryPath = destinationPath + TemporaryFileExtension;
            await VacuumIntoAsync(dbContext, temporaryPath, cancellationToken);
            File.Move(temporaryPath, destinationPath);
            temporaryPath = null;

            var snapshot = ToSnapshot(destinationPath);
            _logger.LogInformation("Snapshotted the node database to {BackupPath} ({BackupBytes} bytes) on the operator's request.", destinationPath, snapshot.SizeBytes);
            PruneOldSnapshots(backupDirectory, await _runtimeSettings.GetNodeDbBackupRetainCountAsync(cancellationToken));
            return new NodeDbSnapshotCreateResult
            {
                Status = NodeDbSnapshotCreateStatus.Created,
                Snapshot = snapshot
            };
        }
        finally
        {
            if (temporaryPath is not null)
            {
                TryDelete(temporaryPath);
            }

            Volatile.Write(ref _creating, 0);
        }
    }

    private static NodeDbSnapshot ToSnapshot(string path)
    {
        var file = new FileInfo(path);
        var stamp = file.Name[BackupFilePrefix.Length..^BackupFileExtension.Length];
        var created = DateTimeOffset.TryParseExact(stamp,
            SnapshotTimestampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        return new NodeDbSnapshot
        {
            Name = file.Name,
            SizeBytes = file.Length,
            CreatedUtc = created
        };
    }

    private void RecordAutomaticBackup(NodeDbAutomaticBackupOutcome outcome, string? error)
    {
        _lastAutomaticBackup = new NodeDbAutomaticBackupStatus
        {
            Outcome = outcome,
            AtUtc = _timeProvider.GetUtcNow(),
            Error = error
        };
    }

    private void LogBudgetExceeded(Exception? exception)
    {
        _logger.LogWarning(exception,
            "Pre-migration node database backup exceeded its {SnapshotTimeout} budget and was skipped; continuing with migration without a fresh snapshot.",
            _options.SnapshotTimeout);
        RecordAutomaticBackup(NodeDbAutomaticBackupOutcome.Skipped, "The snapshot took longer than its time budget.");
    }

    private bool HasRoomForSnapshot(NodeChatDbContext dbContext, string backupDirectory)
    {
        long databaseBytes;
        long freeBytes;
        try
        {
            var databasePath = dbContext.Database.GetDbConnection().DataSource;
            databaseBytes = FileLength(databasePath) + FileLength(databasePath + "-wal");
            freeBytes = _freeSpaceProbe.GetAvailableFreeBytes(backupDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            // An unprobeable volume (a UNC path, a vanished mount) is not a reason to skip; the snapshot itself reports a real failure.
            _logger.LogDebug(exception, "Could not probe free space for the node database backup; attempting the snapshot anyway.");
            return true;
        }

        if (freeBytes >= databaseBytes * MinimumFreeSpaceRatio)
        {
            return true;
        }

        _logger.LogWarning("Skipped the node database backup: {FreeBytes} bytes free in {BackupDirectory}, the {DatabaseBytes}-byte database needs {Ratio} times that.",
            freeBytes,
            backupDirectory,
            databaseBytes,
            MinimumFreeSpaceRatio);
        return false;
    }

    private static long FileLength(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not delete the incomplete node database snapshot {BackupPath}.", path);
        }
    }

    private string ResolveBackupDirectory()
    {
        return NodeDbRestoreStaging.ResolveBackupDirectory(_nodeDataDirectory.Root, _options.BackupDirectory);
    }

    private string BuildSnapshotPath(string backupDirectory)
    {
        // Filename-safe, invariant, lexicographically-sortable UTC timestamp — the sort order is also the chronological
        // order, which the retention prune relies on.
        var timestamp = _timeProvider.GetUtcNow().UtcDateTime.ToString(SnapshotTimestampFormat, CultureInfo.InvariantCulture);
        return Path.Combine(backupDirectory, $"{BackupFilePrefix}{timestamp}{BackupFileExtension}");
    }

    private static async Task VacuumIntoAsync(NodeChatDbContext dbContext, string destinationPath, CancellationToken cancellationToken)
    {
        // VACUUM INTO cannot bind parameters and cannot run inside a transaction, so build the SQL literal ourselves and run it directly on the connection. The path is derived
        // from INodeDataDirectory + a sanitized timestamp (never user input); we still escape single quotes so an unusual directory can't break out of the string literal.
        var escapedPath = destinationPath.Replace("'", "''", StringComparison.Ordinal);

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            // The driver does not abort a running statement on cancellation; sqlite3_interrupt does, and VACUUM INTO honours it.
            await using var interrupt = cancellationToken.Register(static state => raw.sqlite3_interrupt(((SqliteConnection)state!).Handle), connection);
            await using var command = connection.CreateCommand();
            // VACUUM INTO takes no bind parameters (SQLite rejects a parameterized target path), so the destination must be an
            // inlined string literal. It is safe: the path is internally derived (never user input) and single-quote-escaped.
#pragma warning disable CA2100, S2077
            command.CommandText = $"VACUUM INTO '{escapedPath}';";
#pragma warning restore CA2100, S2077
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static IEnumerable<string> ListCompleteSnapshots(string backupDirectory)
    {
        // An exact extension check (the pattern alone would also match legacy-8.3 lookalikes) and no empty file: neither is a restorable snapshot.
        return Directory.EnumerateFiles(backupDirectory, $"{BackupFilePrefix}*{BackupFileExtension}")
                        .Where(static path => path.EndsWith(BackupFileExtension, StringComparison.Ordinal) && new FileInfo(path).Length > 0)
                        .OrderByDescending(static path => Path.GetFileName(path), StringComparer.Ordinal);
    }

    private void PruneOldSnapshots(string backupDirectory, int retainCount)
    {
        // Leftovers of a killed snapshot (a temp file or an empty one) are swept first; they never count toward retention.
        var leftovers = Directory.EnumerateFiles(backupDirectory, $"{BackupFilePrefix}*{BackupFileExtension}{TemporaryFileExtension}")
                                 .Concat(Directory.EnumerateFiles(backupDirectory, $"{BackupFilePrefix}*{BackupFileExtension}")
                                                  .Where(static path => new FileInfo(path).Length == 0))
                                 .ToList();
        var snapshots = ListCompleteSnapshots(backupDirectory).ToList();

        // The snapshot a staged restore names is kept whatever its age: deleting it would turn the restore into a failed start.
        var staged = NodeDbRestoreStaging.ReadStagedSnapshotName(backupDirectory);
        var retain = Math.Max(1, retainCount);
        var stale = snapshots.Skip(retain).Where(path => !string.Equals(Path.GetFileName(path), staged, StringComparison.Ordinal));
        foreach (var stalePath in leftovers.Concat(stale))
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
