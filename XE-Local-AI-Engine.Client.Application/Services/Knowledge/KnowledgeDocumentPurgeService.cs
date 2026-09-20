namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;
using static Chat.Implementation.NodeChatPersistenceSql;

/// <summary>
///     Default <see cref="IKnowledgeDocumentPurgeService" />: every dependent row is deleted explicitly in
///     child-to-parent order inside one transaction rather than left to the declared cascades.
/// </summary>
/// <remarks>
///     The chunk delete is what fires the FTS sync trigger (and whether a cascade would fire it is unmeasured), and
///     chunks must precede sections because that relationship is SET NULL. The chunk delete keeps the external-content
///     <c>chunk_fts</c> index aligned; the vectors are deleted first because they reference the chunk rows. Only after
///     the rows commit are the on-disk encrypted bytes removed, with the path derived from the document id plus its
///     stored extension — never from the display-only <c>storage_path</c> column.
/// </remarks>
public sealed class KnowledgeDocumentPurgeService : IKnowledgeDocumentPurgeService
{
    private readonly NodeChatDbContext _dbContext;
    private readonly IKnowledgeDocumentBlobStore _blobStore;
    private readonly ILogger<KnowledgeDocumentPurgeService> _logger;

    public KnowledgeDocumentPurgeService(NodeChatDbContext dbContext,
        IKnowledgeDocumentBlobStore blobStore,
        ILogger<KnowledgeDocumentPurgeService> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<bool> PurgeAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        await OpenIfNeededAsync(connection, cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Read the stored extension up front so the server-named bytes file can be located after the row is gone; a
        // missing row means there is nothing to delete.
        var extension = await ReadExtensionAsync(connection, transaction, documentId, cancellationToken);
        if (extension is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // FK cascade is OFF, so delete every dependent row explicitly, child-to-parent, in one transaction:
        // vectors → chunks (fires the FTS delete trigger) → sections → the document row.
        await using (var vectorsCommand = connection.CreateCommand())
        {
            vectorsCommand.Transaction = transaction;
            vectorsCommand.CommandText = "DELETE FROM knowledge_chunk_vectors WHERE document_id = $document_id;";
            AddParameter(vectorsCommand, "$document_id", documentId);
            _ = await vectorsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var chunksCommand = connection.CreateCommand())
        {
            chunksCommand.Transaction = transaction;
            chunksCommand.CommandText = "DELETE FROM knowledge_document_chunks WHERE document_id = $document_id;";
            AddParameter(chunksCommand, "$document_id", documentId);
            _ = await chunksCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var sectionsCommand = connection.CreateCommand())
        {
            sectionsCommand.Transaction = transaction;
            sectionsCommand.CommandText = "DELETE FROM knowledge_document_sections WHERE document_id = $document_id;";
            AddParameter(sectionsCommand, "$document_id", documentId);
            _ = await sectionsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var documentCommand = connection.CreateCommand())
        {
            documentCommand.Transaction = transaction;
            documentCommand.CommandText = "DELETE FROM knowledge_documents WHERE document_id = $document_id;";
            AddParameter(documentCommand, "$document_id", documentId);
            _ = await documentCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        // Remove the bytes only once the rows are gone, so a failure here can never leave a live row without its content.
        // The delete is already done for the caller: a failed file delete is logged as success and swept on the next start.
        try
        {
            await _blobStore.DeleteBytesAsync(documentId, extension, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception,
                "Deleted knowledge document {DocumentId} but could not remove its encrypted bytes; the startup orphan sweep will reclaim them.",
                documentId);
        }

        return true;
    }

    private static async Task<string?> ReadExtensionAsync(DbConnection connection, DbTransaction transaction, Guid documentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT extension FROM knowledge_documents WHERE document_id = $document_id;";
        AddParameter(command, "$document_id", documentId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : result as string ?? string.Empty;
    }
}
