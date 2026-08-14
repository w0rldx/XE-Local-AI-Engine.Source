namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence;

public interface ICoordinatedModelProviderMapStore
{
    Task<ModelProviderMapRecord?> ReadWithRevisionAsync(IModelProviderMapReadLease lease,
        string modelName,
        CancellationToken cancellationToken = default);
    Task<ProviderMapClaimResult> TryClaimLlamaCppAsync(IModelProviderMapMutationLease lease,
        string modelName,
        CancellationToken cancellationToken = default);
    Task<ProviderMapMutationResult> TryUpsertAsync(IModelProviderMapMutationLease lease,
        string modelName,
        string providerName,
        string? expectedRevision = null,
        CancellationToken cancellationToken = default);
    Task<ProviderMapRestoreResult> TryRestoreAsync(IModelProviderMapMutationLease lease,
        ProviderMapMutationReceipt receipt,
        CancellationToken cancellationToken = default);
    Task<ProviderMapRemovalResult> TryRemoveIfMatchAsync(IModelProviderMapMutationLease lease,
        string modelName,
        string expectedProvider,
        string expectedRevision,
        CancellationToken cancellationToken = default);
}

public sealed record ProviderMapMutationReceipt(
    string ModelName,
    ModelProviderMapRecord? Prior,
    ModelProviderMapRecord? Mutation,
    bool WasRemoval);

public abstract record ProviderMapClaimResult
{
    public sealed record Created(ProviderMapMutationReceipt Receipt) : ProviderMapClaimResult;
    public sealed record CompatibleExisting(ModelProviderMapRecord Mapping) : ProviderMapClaimResult;
    public sealed record Conflict(string ExistingProvider) : ProviderMapClaimResult;
}

public abstract record ProviderMapMutationResult
{
    public sealed record Mutated(ProviderMapMutationReceipt Receipt) : ProviderMapMutationResult;
    public sealed record Superseded(ModelProviderMapRecord? Current) : ProviderMapMutationResult;
}

public enum ProviderMapRestoreResult
{
    Restored,
    Superseded
}

public abstract record ProviderMapRemovalResult
{
    public sealed record Removed(ProviderMapMutationReceipt Receipt) : ProviderMapRemovalResult;
    public sealed record Absent : ProviderMapRemovalResult;
    public sealed record Superseded(ModelProviderMapRecord Current) : ProviderMapRemovalResult;
}
