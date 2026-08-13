namespace XE_Local_AI_Engine.Client.Services.Persistence.Implementation;

using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>SQLite implementation of the knowledge downgrade preflight and explicit backup export.</summary>
public sealed class KnowledgeDowngradeSafetyService : IKnowledgeDowngradeSafetyService
{
    private const string BackupDirectoryName = "backups";
    private const string ExportDirectoryName = "knowledge-downgrade";
    private const string ExportFilePrefix = "node-chat-before-knowledge-downgrade-";

    private readonly INodeDataDirectory _nodeDataDirectory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public KnowledgeDowngradeSafetyService(IServiceScopeFactory scopeFactory,
        INodeDataDirectory nodeDataDirectory,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _nodeDataDirectory = nodeDataDirectory ?? throw new ArgumentNullException(nameof(nodeDataDirectory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public async Task<KnowledgeDowngradePreflightResult> PreflightAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var sourceConnectionString = dbContext.Database.GetConnectionString()
                                     ?? throw new InvalidOperationException("The node database connection string is unavailable.");
        await using var connection = CreateReadOnlyConnection(sourceConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await HasCollectionMigrationSchemaAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return new KnowledgeDowngradePreflightResult(false, true, 0, 0, 0, []);
        }

        return await ReadConflictsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<KnowledgeDowngradeExportResult> ExportAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exportDirectory = ResolveSafeExportDirectory();
        Directory.CreateDirectory(exportDirectory);
        RejectReparsePoint(new DirectoryInfo(exportDirectory));

        var timestamp = _timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        var destinationPath = Path.Combine(exportDirectory, $"{ExportFilePrefix}{timestamp}.sqlite");
        RejectExistingDestination(destinationPath);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        // Revalidate immediately before SQLite opens the destination. This closes ordinary same-user replacement races
        // between directory preparation and export; a fully race-free guarantee would require handle-relative creation,
        // which Microsoft.Data.Sqlite's VACUUM INTO surface does not expose.
        RejectReparsePoint(new DirectoryInfo(exportDirectory));
        RejectExistingDestination(destinationPath);
        await VacuumIntoAsync(dbContext, destinationPath, cancellationToken).ConfigureAwait(false);

        try
        {
            var preflight = await PreflightArtifactAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            var bytes = new FileInfo(destinationPath).Length;
            var sha256 = await HashFileAsync(destinationPath, cancellationToken).ConfigureAwait(false);
            return new KnowledgeDowngradeExportResult(destinationPath, bytes, sha256, preflight);
        }
        catch
        {
            TryDeleteIncompleteArtifact(destinationPath);
            throw;
        }
    }

    private static async Task<KnowledgeDowngradePreflightResult> PreflightArtifactAsync(string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateReadOnlyConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await HasCollectionMigrationSchemaAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return new KnowledgeDowngradePreflightResult(false, true, 0, 0, 0, []);
        }

        return await ReadConflictsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static SqliteConnection CreateReadOnlyConnection(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            Mode = SqliteOpenMode.ReadOnly
        };
        return new SqliteConnection(builder.ToString());
    }

    private static async Task<bool> HasCollectionMigrationSchemaAsync(DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('knowledge_documents') WHERE name = 'collection_id';";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<KnowledgeDowngradePreflightResult> ReadConflictsAsync(DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document.content_hash, document.document_id
            FROM knowledge_documents AS document
            INNER JOIN (
                SELECT content_hash
                FROM knowledge_documents
                GROUP BY content_hash
                HAVING COUNT(*) > 1
            ) AS duplicate ON duplicate.content_hash = document.content_hash
            ORDER BY document.content_hash COLLATE BINARY, document.document_id COLLATE BINARY;
            """;

        var groups = new List<KnowledgeDowngradeConflict>();
        var documentIdentifiers = new List<string>();
        string? currentHash = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var contentHash = reader.GetString(0);
            if (currentHash is not null && !string.Equals(currentHash, contentHash, StringComparison.Ordinal))
            {
                AddConflict(groups, documentIdentifiers);
                documentIdentifiers = [];
            }

            currentHash = contentHash;
            documentIdentifiers.Add(ToOpaqueDocumentIdentifier(reader.GetString(1)));
        }

        if (currentHash is not null)
        {
            AddConflict(groups, documentIdentifiers);
        }

        var conflictingDocumentCount = groups.Sum(static group => group.DocumentIdentifiers.Count);
        return new KnowledgeDowngradePreflightResult(
            true,
            groups.Count == 0,
            groups.Count,
            conflictingDocumentCount,
            conflictingDocumentCount - groups.Count,
            groups);
    }

    private static void AddConflict(List<KnowledgeDowngradeConflict> groups, IReadOnlyList<string> documentIdentifiers)
    {
        var conflictId = string.Create(CultureInfo.InvariantCulture, $"conflict-{groups.Count + 1:D6}");
        groups.Add(new KnowledgeDowngradeConflict(conflictId, documentIdentifiers));
    }

    private static string ToOpaqueDocumentIdentifier(string documentId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat("knowledge-downgrade-document-v1\n", documentId)));
        return string.Concat("document-", Convert.ToHexStringLower(digest));
    }

    private string ResolveSafeExportDirectory()
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_nodeDataDirectory.Root));
        RejectReparsePoint(new DirectoryInfo(root));

        var backupDirectory = Path.Combine(root, BackupDirectoryName);
        RejectReparsePointIfPresent(new DirectoryInfo(backupDirectory));
        Directory.CreateDirectory(backupDirectory);
        RejectReparsePoint(new DirectoryInfo(backupDirectory));

        var exportDirectory = Path.GetFullPath(Path.Combine(backupDirectory, ExportDirectoryName));
        var expectedPrefix = backupDirectory + Path.DirectorySeparatorChar;
        if (!exportDirectory.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The downgrade export directory is outside the node backup directory.");
        }

        RejectReparsePointIfPresent(new DirectoryInfo(exportDirectory));
        return exportDirectory;
    }

    private static void RejectExistingDestination(string destinationPath)
    {
        var destination = new FileInfo(destinationPath);
        if (destination.Exists || destination.LinkTarget is not null)
        {
            throw new IOException("The downgrade export destination already exists and will not be overwritten.");
        }
    }

    private static void RejectReparsePointIfPresent(FileSystemInfo info)
    {
        if (info.Exists || info.LinkTarget is not null)
        {
            RejectReparsePoint(info);
        }
    }

    private static void RejectReparsePoint(FileSystemInfo info)
    {
        info.Refresh();
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnauthorizedAccessException("The downgrade export path must not traverse a symbolic link or reparse point.");
        }
    }

    private static async Task VacuumIntoAsync(NodeChatDbContext dbContext,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var escapedPath = destinationPath.Replace("'", "''", StringComparison.Ordinal);
        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = dbContext.Database.GetDbConnection().CreateCommand();
#pragma warning disable CA2100, S2077
            command.CommandText = $"VACUUM INTO '{escapedPath}';";
#pragma warning restore CA2100, S2077
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteIncompleteArtifact(destinationPath);
            throw;
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void TryDeleteIncompleteArtifact(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Preserve the original export/hash exception. A partial artifact is never returned as successful.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original export/hash exception. A partial artifact is never returned as successful.
        }
    }
}
