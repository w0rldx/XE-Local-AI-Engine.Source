namespace XE_Local_AI_Engine.Client.Services.Persistence;

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Stages a node database restore from a snapshot and applies it at the next start, while no connection holds the database.
/// </summary>
/// <remarks>
///     The running node only writes a marker naming the snapshot. The next start, after the single-instance lease and before
///     the node key or database is read, checks the snapshot again, sets the live database and its sidecars aside as
///     <c>node.sqlite.prerestore-&lt;utc&gt;</c> and copies the snapshot into its place. A failure leaves the marker.
/// </remarks>
public static partial class NodeDbRestoreStaging
{
    public const string MarkerFileName = "restore-pending.json";
    public const string SnapshotFilePrefix = "node-chat-";
    public const string SnapshotFileExtension = ".sqlite";

    /// <summary>EF Core's default history table, which the node-chat context keeps.</summary>
    public const string ChatMigrationsHistoryTable = "__EFMigrationsHistory";

    private const string DefaultBackupSubdirectory = "backups";
    private const string QuickCheckPassed = "ok";
    private const string WalSuffix = "-wal";
    private const string ShmSuffix = "-shm";

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>> ShippedMigrations = new(ReadShippedMigrationIds);

    /// <summary>The snapshot folder: the configured override, else <c>backups</c> under the node data root.</summary>
    public static string ResolveBackupDirectory(string dataRoot, string? configuredDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        return string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(dataRoot, DefaultBackupSubdirectory)
            : configuredDirectory;
    }

    /// <summary>True only for <c>node-chat-&lt;digits&gt;T&lt;digits&gt;Z.sqlite</c>, the name the snapshot writer produces.</summary>
    public static bool IsValidSnapshotName(string? name)
    {
        return name is not null && SnapshotNamePattern().IsMatch(name);
    }

    /// <summary>
    ///     Every migration id this binary ships, keyed by the history table that records it, read from the persistence assembly's
    ///     <c>[Migration]</c>/<c>[DbContext]</c> attributes so it needs no container.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ShippedMigrationIds => ShippedMigrations.Value;

    /// <summary>True when the path exists as a symbolic link or reparse point; a missing path is never one.</summary>
    public static bool IsLink(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            return true;
        }

        // A missing path reports every attribute bit set, so its attributes are read only once it is known to exist.
        return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>Why the backups folder or the snapshot cannot be trusted as a plain file, or null when both are.</summary>
    public static string? DescribeLinkedSnapshot(string backupDirectory, string snapshotPath)
    {
        ArgumentNullException.ThrowIfNull(backupDirectory);
        ArgumentNullException.ThrowIfNull(snapshotPath);

        if (IsLink(backupDirectory))
        {
            return "the backups folder is a link, not a folder";
        }

        return IsLink(snapshotPath) ? "the snapshot is a link, not a file" : null;
    }

    /// <summary>
    ///     Runs <c>PRAGMA quick_check</c> on the snapshot over a fresh read-only connection, then refuses one whose migration history
    ///     names a migration this binary does not ship. Null when it passes, else the reason.
    /// </summary>
    /// <param name="knownMigrationsByHistoryTable">Every shipped migration id, keyed by the history table that records it.</param>
    public static async Task<string?> CheckSnapshotIntegrityAsync(string snapshotPath,
        IReadOnlyDictionary<string, IReadOnlySet<string>> knownMigrationsByHistoryTable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshotPath);
        ArgumentNullException.ThrowIfNull(knownMigrationsByHistoryTable);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        try
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (!string.Equals(result, QuickCheckPassed, StringComparison.Ordinal))
            {
                return $"The snapshot failed its integrity check: {result}";
            }

            foreach (var history in knownMigrationsByHistoryTable)
            {
                var unknown = await FindUnknownMigrationAsync(connection, history.Key, history.Value, cancellationToken);
                if (unknown is not null)
                {
                    return $"The snapshot was written by a newer version of the engine (migration {unknown} is unknown to this version), so this version cannot open it.";
                }
            }

            return null;
        }
        catch (SqliteException exception)
        {
            return $"The snapshot could not be read as a SQLite database: {exception.Message}";
        }
    }

    /// <summary>Writes the marker the next start applies; replaces an earlier marker.</summary>
    public static async Task WriteMarkerAsync(string backupDirectory, string snapshotName, DateTimeOffset requestedUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backupDirectory);
        if (!IsValidSnapshotName(snapshotName))
        {
            throw new ArgumentException("Not a snapshot file name.", nameof(snapshotName));
        }

        await WriteMarkerFileAsync(Path.Combine(backupDirectory, MarkerFileName),
            new NodeDbRestoreMarker
            {
                SnapshotName = snapshotName,
                RequestedUtc = requestedUtc
            },
            cancellationToken);
    }

    /// <summary>The snapshot name a marker in this folder points at, or null when there is no readable marker.</summary>
    public static string? ReadStagedSnapshotName(string backupDirectory)
    {
        ArgumentNullException.ThrowIfNull(backupDirectory);
        try
        {
            var markerPath = Path.Combine(backupDirectory, MarkerFileName);
            return File.Exists(markerPath) ? JsonSerializer.Deserialize<NodeDbRestoreMarker>(File.ReadAllText(markerPath))?.SnapshotName : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Applies a staged restore when a marker exists. Never throws for an I/O, marker or snapshot problem; it reports it.</summary>
    public static async Task<NodeDbRestoreApplyResult> ApplyPendingAsync(string backupDirectory,
        string databasePath,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(backupDirectory);
        ArgumentNullException.ThrowIfNull(databasePath);

        var markerPath = Path.Combine(backupDirectory, MarkerFileName);
        if (IsLink(markerPath))
        {
            return Failed(markerPath, "the restore marker is a link, not a file", asidePath: null);
        }

        if (!File.Exists(markerPath))
        {
            return NodeDbRestoreApplyResult.None;
        }

        NodeDbRestoreMarker? marker;
        try
        {
            marker = JsonSerializer.Deserialize<NodeDbRestoreMarker>(await File.ReadAllTextAsync(markerPath, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return Failed(markerPath, exception.Message, asidePath: null);
        }

        var name = marker?.SnapshotName;
        var stamp = marker?.SetAsideStamp;
        if (!IsValidSnapshotName(name) || (stamp is not null && !StampPattern().IsMatch(stamp)))
        {
            return Failed(markerPath, "the marker does not name a snapshot file", asidePath: null);
        }

        // An earlier attempt recorded where the database goes aside; reusing it keeps the database and its WAL under one name.
        var asidePath = stamp is null ? null : AsidePath(databasePath, stamp);
        if (asidePath is not null && File.Exists(databasePath) && File.Exists(asidePath))
        {
            return CompleteEarlierRestore(markerPath, name!, asidePath);
        }

        var snapshotPath = Path.Combine(backupDirectory, name!);
        var copyPath = databasePath + ".restoring";
        var refusal = DescribeLinkedSnapshot(backupDirectory, snapshotPath)
                      ?? (IsLink(copyPath) ? "a link stands where the restore copies the snapshot" : null);
        if (refusal is not null)
        {
            return Failed(markerPath, refusal, asidePath);
        }

        if (!File.Exists(snapshotPath) || new FileInfo(snapshotPath).Length == 0)
        {
            return Failed(markerPath, $"the snapshot '{name}' does not exist in '{backupDirectory}'", asidePath);
        }

        // Checked again here: the previous process checked it, but the file may have changed and this binary may be another version.
        var problem = await CheckSnapshotIntegrityAsync(snapshotPath, ShippedMigrationIds, cancellationToken);
        if (problem is not null)
        {
            return Failed(markerPath, problem, asidePath);
        }

        var movedDatabase = false;
        try
        {
            if (stamp is null)
            {
                stamp = nowUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                asidePath = AsidePath(databasePath, stamp);
                await WriteMarkerFileAsync(markerPath,
                    new NodeDbRestoreMarker
                    {
                        SnapshotName = name,
                        RequestedUtc = marker!.RequestedUtc,
                        SetAsideStamp = stamp
                    },
                    cancellationToken);
            }

            // The database goes aside with its sidecars, so the kept copy holds any change not yet checkpointed. Sidecars without a
            // database (an earlier attempt that died between the moves) go aside too, so the snapshot never meets a stale WAL.
            if (File.Exists(databasePath))
            {
                File.Move(databasePath, asidePath!);
                movedDatabase = true;
            }

            MoveIfExists(databasePath + WalSuffix, asidePath + WalSuffix);
            MoveIfExists(databasePath + ShmSuffix, asidePath + ShmSuffix);
            // Any entry counts, not only a file: a directory under a sidecar name cannot go aside and must not meet the snapshot either.
            if (Path.Exists(databasePath + WalSuffix) || Path.Exists(databasePath + ShmSuffix))
            {
                // Thrown so the handler below puts the database back.
                throw new IOException("a -wal or -shm file is still next to the database");
            }

            await CopyExclusiveAsync(snapshotPath, copyPath, cancellationToken);
            File.Move(copyPath, databasePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Put the live database back when the copy never landed, so a failed restore changes nothing; say so when it cannot.
            var reason = exception.Message;
            if (movedDatabase && !File.Exists(databasePath))
            {
                reason += TryMove(asidePath!, databasePath)
                          + TryMove(asidePath + WalSuffix, databasePath + WalSuffix)
                          + TryMove(asidePath + ShmSuffix, databasePath + ShmSuffix);
            }

            return Failed(markerPath, reason, asidePath);
        }

        // The snapshot is installed from here on. An undeletable marker is a warning while a set-aside database lets the next
        // start recognise the finished restore; without one the start must stop, or the snapshot would be applied twice.
        var deleteProblem = TryDelete(markerPath);
        if (deleteProblem is not null && !File.Exists(asidePath))
        {
            return new NodeDbRestoreApplyResult
            {
                Applied = false,
                Installed = true,
                SnapshotName = name,
                MarkerPath = markerPath,
                Error = $"the restore of {name} was applied, but its marker could not be deleted ({deleteProblem}) and no replaced database was set aside"
            };
        }

        return new NodeDbRestoreApplyResult
        {
            Applied = true,
            Installed = true,
            SnapshotName = name,
            // Also when an earlier, interrupted attempt moved it: the log then names the copy this restore replaced.
            SetAsidePath = File.Exists(asidePath) ? asidePath : null,
            MarkerPath = markerPath,
            Warning = deleteProblem is null
                ? null
                : $"The restore was applied, but its marker could not be deleted ({deleteProblem}); the next start recognises the finished restore and deletes it."
        };
    }

    /// <summary>The marker of a restore an earlier start installed but could not clear: never applied twice, only cleared.</summary>
    private static NodeDbRestoreApplyResult CompleteEarlierRestore(string markerPath, string name, string asidePath)
    {
        var deleteProblem = TryDelete(markerPath);
        return new NodeDbRestoreApplyResult
        {
            Applied = false,
            SnapshotName = name,
            SetAsidePath = asidePath,
            MarkerPath = markerPath,
            Warning = deleteProblem is null
                ? $"An earlier start already restored {name} and kept the replaced database at '{asidePath}'; its leftover marker was deleted now."
                : $"An earlier start already restored {name} and kept the replaced database at '{asidePath}'; its leftover marker still cannot be deleted ({deleteProblem})."
        };
    }

    private static string AsidePath(string databasePath, string stamp) => $"{databasePath}.prerestore-{stamp}";

    /// <summary>Writes the marker through a temporary file and a rename, so a crash never leaves half a marker; refuses a link.</summary>
    private static async Task WriteMarkerFileAsync(string markerPath, NodeDbRestoreMarker marker, CancellationToken cancellationToken)
    {
        var temporaryPath = markerPath + ".tmp";
        if (IsLink(markerPath) || IsLink(temporaryPath))
        {
            throw new IOException("the restore marker is a link, not a file");
        }

        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(marker), cancellationToken);
        File.Move(temporaryPath, markerPath, overwrite: true);
    }

    private static string? TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return exception.Message;
        }
    }

    private static async Task CopyExclusiveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        // A leftover plain file from an interrupted copy is replaced; CreateNew then refuses anything that appears in between.
        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);

        // On disk before the rename makes it the database: a power cut must not leave a renamed but empty file. FlushAsync has no
        // flush-to-disk form, and this runs once at start before the host exists, so the blocking call is the only correct one.
#pragma warning disable CA1849
        destination.Flush(flushToDisk: true);
#pragma warning restore CA1849
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> ReadShippedMigrationIds()
    {
        var chat = new HashSet<string>(StringComparer.Ordinal);
        var identity = new HashSet<string>(StringComparer.Ordinal);
        Type?[] types;
        try
        {
            types = typeof(NodeChatDbContext).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // A type whose dependency cannot load is no migration; the ones that did load still are.
            types = exception.Types;
        }

        foreach (var type in types.OfType<Type>())
        {
            var migrationId = type.GetCustomAttribute<MigrationAttribute>()?.Id;
            var contextType = type.GetCustomAttribute<DbContextAttribute>()?.ContextType;
            if (migrationId is null || contextType is null)
            {
                continue;
            }

            if (contextType == typeof(NodeChatDbContext))
            {
                chat.Add(migrationId);
            }
            else if (contextType == typeof(NodeIdentityDbContext))
            {
                identity.Add(migrationId);
            }
        }

        return new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [ChatMigrationsHistoryTable] = chat,
            [NodeIdentityDbContext.IdentityMigrationsHistoryTable] = identity
        };
    }

    private static async Task<string?> FindUnknownMigrationAsync(SqliteConnection connection,
        string historyTable,
        IReadOnlySet<string> known,
        CancellationToken cancellationToken)
    {
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            exists.Parameters.AddWithValue("$name", historyTable);
            if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 0)
            {
                return null;
            }
        }

        await using var command = connection.CreateCommand();
        // A table name cannot be bound; it comes from the caller's shipped DbContext configuration, never from the request.
#pragma warning disable CA2100, S2077
        command.CommandText = $"SELECT \"MigrationId\" FROM \"{historyTable.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
#pragma warning restore CA2100, S2077
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var migrationId = reader.GetString(0);
            if (!known.Contains(migrationId))
            {
                return migrationId;
            }
        }

        return null;
    }

    [GeneratedRegex(@"^node-chat-[0-9]+T[0-9]+Z\.sqlite\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SnapshotNamePattern();

    [GeneratedRegex(@"^[0-9]{8}T[0-9]{6}Z\z", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StampPattern();

    /// <summary>A failed restore; names the set-aside database when one is still there, because then nothing is in its place.</summary>
    private static NodeDbRestoreApplyResult Failed(string markerPath, string reason, string? asidePath)
    {
        return new NodeDbRestoreApplyResult
        {
            Applied = false,
            MarkerPath = markerPath,
            SetAsidePath = asidePath is not null && File.Exists(asidePath) ? asidePath : null,
            Error = reason
        };
    }

    private static void MoveIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination);
        }
    }

    /// <summary>Moves a set-aside file back; empty when it worked, else a sentence for the failure reason naming the file.</summary>
    private static string TryMove(string source, string destination)
    {
        try
        {
            MoveIfExists(source, destination);
            return string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"; '{source}' could not be moved back to '{destination}' ({exception.Message})";
        }
    }

    private sealed class NodeDbRestoreMarker
    {
        public string? SnapshotName { get; init; }

        public DateTimeOffset RequestedUtc { get; init; }

        /// <summary>Recorded before the first move, so every attempt sets the database and its sidecars aside under one name.</summary>
        public string? SetAsideStamp { get; init; }
    }
}
