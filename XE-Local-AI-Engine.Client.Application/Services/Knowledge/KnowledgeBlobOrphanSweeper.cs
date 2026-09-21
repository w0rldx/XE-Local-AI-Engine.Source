namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Reclaims knowledge-base document blobs whose <c>knowledge_documents</c> row is gone: enumerate the on-disk ids,
///     probe for the owning row, delete what nothing owns.
/// </summary>
/// <remarks>
///     The purge service commits the row deletes first and removes the bytes afterwards, so a failed or never-reached file
///     delete strands a file nothing can read again, and a repeat purge cannot collect it because the row it keys on is
///     gone. It is the knowledge counterpart of <see cref="Chat.Implementation.RetentionSweeperService" />'s orphaned
///     upload-directory resweep, runs once per start, and cannot race an upload because
///     <see cref="KnowledgeDocumentBlobStore.AddAsync" /> commits the row first. Best-effort: a failure never blocks startup.
/// </remarks>
public sealed class KnowledgeBlobOrphanSweeper : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKnowledgeDocumentBlobStore _blobStore;
    private readonly ILogger<KnowledgeBlobOrphanSweeper> _logger;

    public KnowledgeBlobOrphanSweeper(IServiceScopeFactory scopeFactory,
        IKnowledgeDocumentBlobStore blobStore,
        ILogger<KnowledgeBlobOrphanSweeper> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield first so host startup is never blocked on a directory walk.
        await Task.Yield();

        try
        {
            _ = ReconcileInterruptedWrites();

            var reclaimed = await SweepOnceAsync(stoppingToken);
            if (reclaimed > 0)
            {
                _logger.LogInformation("Reclaimed {OrphanCount} orphaned knowledge document blob(s).", reclaimed);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down during startup; the sweep retries on the next start.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The orphaned knowledge blob sweep failed; the affected files stay on disk until the next start.");
        }
    }

    /// <summary>
    ///     Recovers the blobs an interrupted reindex left behind a backup suffix and reclaims the aged litter beside
    ///     the live ones. Internal so a test can drive it without the hosted lifecycle.
    /// </summary>
    /// <remarks>
    ///     Runs ahead of <see cref="SweepOnceAsync" /> so a restored blob is judged by the row probe in the same pass:
    ///     a backup whose document was purged meanwhile is restored here and reclaimed as an orphan there.
    /// </remarks>
    internal KnowledgeBlobReconciliationResult ReconcileInterruptedWrites()
    {
        var reconciled = _blobStore.ReconcileInterruptedWrites();

        foreach (var restoredBlobName in reconciled.RestoredBlobNames)
        {
            // A restore means the process died between an old blob being renamed aside and the reindex committing,
            // so the operator should know the bytes came back and which document they belong to.
            _logger.LogWarning("Restored knowledge document blob {BlobName} from the backup an interrupted reindex left behind.", restoredBlobName);
        }

        if (reconciled.RemovedLitterCount > 0)
        {
            _logger.LogInformation("Reclaimed {OrphanCount} interrupted-write sibling(s) of live knowledge document blob(s).", reconciled.RemovedLitterCount);
        }

        return reconciled;
    }

    // One deterministic sweep pass. Internal so a test can drive it without the hosted lifecycle.
    internal async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        var documentIds = _blobStore.ListStoredDocumentIds();
        if (documentIds.Count == 0)
        {
            return 0;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();

        var reclaimed = 0;
        foreach (var documentId in documentIds)
        {
            try
            {
                // A per-id existence probe stays bounded and avoids materializing every document id; the stored blobs
                // are one per document, so this set is the size of the corpus, not of the chunk/vector lanes.
                var documentExists = await dbContext.Database
                                                    .SqlQueryRaw<Guid>("SELECT document_id FROM knowledge_documents WHERE document_id = {0}", documentId)
                                                    .AnyAsync(cancellationToken);
                if (documentExists)
                {
                    continue;
                }

                await _blobStore.DeleteAllBytesAsync(documentId, cancellationToken);
                reclaimed++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One unreadable file or failed probe must not cost the remaining candidates their sweep.
                _logger.LogWarning(exception, "Could not reclaim the orphaned knowledge blob of document {DocumentId}.", documentId);
            }
        }

        return reclaimed;
    }
}
