namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>Imports a previously registered local Git repository into an isolated knowledge collection.</summary>
public interface IKnowledgeRepositoryImportService
{
    Task<KnowledgeRepositoryImportResult> ImportAsync(Guid selectedFolderId,
        string? collectionId,
        CancellationToken cancellationToken);
}

public sealed class KnowledgeRepositoryImportResult
{
    public required string CollectionId { get; init; }

    public required int DiscoveredFiles { get; init; }

    public required int AddedDocuments { get; init; }

    public required int DeduplicatedDocuments { get; init; }

    public required int EnqueuedDocuments { get; init; }

    public required int SkippedFiles { get; init; }

    public required bool QueueCapacityReached { get; init; }

    public int UpdatedDocuments { get; init; }

    public int RemovedDocuments { get; init; }
}
