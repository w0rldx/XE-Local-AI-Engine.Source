namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IKnowledgeDocumentPurgeService" />. Because foreign-key enforcement is OFF on the runtime
///     connection (C1), the schema cascade cannot be relied upon: every dependent row is deleted explicitly in
///     child-to-parent order inside one transaction. The chunk delete fires the FTS delete trigger so the external-content
///     <c>chunk_fts</c> index stays aligned; the vectors are deleted first because they reference the chunk rows. Only
///     after the rows commit are the on-disk encrypted bytes removed, with the path derived from the document id plus its
///     stored extension — never from the display-only <c>storage_path</c> column.
/// </summary>
public sealed class KnowledgeDocumentPurgeService : IKnowledgeDocumentPurgeService
{
    private readonly NodeChatDbContext _dbContext;
    private readonly IKnowledgeDocumentBlobStore _blobStore;

    public KnowledgeDocumentPurgeService(NodeChatDbContext dbContext, IKnowledgeDocumentBlobStore blobStore)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    }

    public async Task<bool> PurgeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Read the stored extension up front so the server-named bytes file can be located after the row is gone; a
        // missing row means there is nothing to delete.
        var extension = await ReadExtensionAsync(connection, transaction, documentId, cancellationToken).ConfigureAwait(false);
        if (extension is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        // FK cascade is OFF, so delete every dependent row explicitly, child-to-parent, in one transaction:
        // vectors → chunks (fires the FTS delete trigger) → sections → the document row.
        await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_chunk_vectors WHERE document_id = $document_id;", documentId, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_document_chunks WHERE document_id = $document_id;", documentId, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_document_sections WHERE document_id = $document_id;", documentId, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, transaction, "DELETE FROM knowledge_documents WHERE document_id = $document_id;", documentId, cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Only after the rows are gone remove the encrypted bytes from disk; a best-effort failure leaves an orphan blob
        // that a later purge also covers rather than a live row without its content.
        await _blobStore.DeleteBytesAsync(documentId, extension, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<string?> ReadExtensionAsync(DbConnection connection, DbTransaction transaction, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT extension FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : result as string ?? string.Empty;
    }

    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string commandText, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        AddParameter(command, "$document_id", documentId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
