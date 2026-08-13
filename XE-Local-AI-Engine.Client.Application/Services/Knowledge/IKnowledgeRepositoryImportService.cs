namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>Imports a previously registered local Git repository into an isolated knowledge collection.</summary>
public interface IKnowledgeRepositoryImportService
{
    Task<KnowledgeRepositoryImportResult> ImportAsync(Guid selectedFolderId,
        string? collectionId,
        CancellationToken cancellationToken);
}

public sealed record KnowledgeRepositoryImportResult(
    string CollectionId,
    int DiscoveredFiles,
    int AddedDocuments,
    int DeduplicatedDocuments,
    int EnqueuedDocuments,
    int SkippedFiles,
    bool QueueCapacityReached,
    int UpdatedDocuments = 0,
    int RemovedDocuments = 0);
