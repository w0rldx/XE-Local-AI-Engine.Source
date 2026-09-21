namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Sqlite;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Raw-ADO <see cref="IConversationUploadedFileRowStore" />, scoped to the DbContext lifetime.
/// </summary>
/// <remarks>
///     Raw SQL rather than EF, as the node chat rows beside it are: the display-name column is encrypted through the
///     context's own <c>EncryptUploadedFileName</c>/<c>DecryptUploadedFileName</c> helpers (same protector and AAD as
///     the save-changes interceptor), so the row never needs to be tracked to be written correctly.
/// </remarks>
public sealed class ConversationUploadedFileRowStore : IConversationUploadedFileRowStore
{
    private readonly NodeChatDbContext _dbContext;

    public ConversationUploadedFileRowStore(NodeChatDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        _dbContext = dbContext;
    }

    public async Task InsertAsync(ConversationUploadedFileRow row, string storagePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);

        var encryptedName = _dbContext.EncryptUploadedFileName(row.OriginalFileName, row.ConversationId, row.FileId);

        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              INSERT INTO conversation_uploaded_files (file_id, conversation_id, original_file_name, mime_type, extension, size_bytes, extraction_status, extracted_chars, storage_path, created_at_utc)
                              VALUES ($file_id, $conversation_id, $original_file_name, $mime_type, $extension, $size_bytes, $extraction_status, $extracted_chars, $storage_path, $created_at_utc);
                              """;
        AddParameter(command, "$file_id", row.FileId);
        AddParameter(command, "$conversation_id", row.ConversationId);
        AddParameter(command, "$original_file_name", encryptedName);
        AddParameter(command, "$mime_type", row.MimeType);
        AddParameter(command, "$extension", row.Extension);
        AddParameter(command, "$size_bytes", row.SizeBytes);
        AddParameter(command, "$extraction_status", row.ExtractionStatus);
        AddParameter(command, "$extracted_chars", row.ExtractedChars);
        AddParameter(command, "$storage_path", storagePath);
        AddParameter(command, "$created_at_utc", row.CreatedAtUtc);
        await OpenIfNeededAsync(command.Connection, cancellationToken);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationUploadedFileRow>> ListAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
                              SELECT file_id, conversation_id, original_file_name, mime_type, extension, size_bytes, extraction_status, extracted_chars, created_at_utc
                              FROM conversation_uploaded_files
                              WHERE conversation_id = $conversation_id
                              ORDER BY created_at_utc ASC, file_id ASC;
                              """;
        AddParameter(command, "$conversation_id", conversationId);
        await OpenIfNeededAsync(command.Connection, cancellationToken);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ConversationUploadedFileRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var fileId = Guid.Parse(reader.GetString(0));
            var ownerConversationId = Guid.Parse(reader.GetString(1));
            var nameBytes = await reader.GetFieldValueAsync<byte[]>(ordinal: 2, cancellationToken);

            rows.Add(new ConversationUploadedFileRow
            {
                FileId = fileId,
                ConversationId = ownerConversationId,
                OriginalFileName = _dbContext.DecryptUploadedFileName(nameBytes, ownerConversationId, fileId),
                MimeType = reader.GetString(3),
                Extension = reader.GetString(4),
                SizeBytes = reader.GetInt64(5),
                ExtractionStatus = reader.GetString(6),
                ExtractedChars = await reader.IsDBNullAsync(ordinal: 7, cancellationToken) ? null : reader.GetInt32(7),
                CreatedAtUtc = reader.GetInt64(8)
            });
        }

        return rows;
    }

    public async Task<string?> DeleteAsync(Guid conversationId, Guid fileId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        // Read the stored extension first so the caller can name the server-named bytes file precisely; a missing row
        // means there is nothing to delete, and the two commands share the one connection exactly as they always have.
        string extension;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.CommandText = "SELECT extension FROM conversation_uploaded_files WHERE conversation_id = $conversation_id AND file_id = $file_id;";
            AddParameter(lookup, "$conversation_id", conversationId);
            AddParameter(lookup, "$file_id", fileId);
            var result = await lookup.ExecuteScalarAsync(cancellationToken);
            if (result is null or DBNull)
            {
                return null;
            }

            extension = result as string ?? string.Empty;
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM conversation_uploaded_files WHERE conversation_id = $conversation_id AND file_id = $file_id;";
            AddParameter(delete, "$conversation_id", conversationId);
            AddParameter(delete, "$file_id", fileId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        return extension;
    }

    private static Task OpenIfNeededAsync(DbConnection? connection, CancellationToken cancellationToken)
    {
        // Open-if-needed AND apply the shared WAL/busy_timeout/synchronous pragmas on the open.
        return NodeSqlitePragmas.OpenAndConfigureAsync(connection, cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
