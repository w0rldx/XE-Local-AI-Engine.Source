namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence;

public interface ICoordinatedModelProviderMapStore
{
    /// <summary>
    ///     Every stored mapping, ordered by model name. Takes NO lease: a lease is keyed per model, so there is no such
    ///     thing as a lease over "the whole map", and holding one over every row would serialize the map against every
    ///     concurrent claim on the node.
    /// </summary>
    /// <remarks>
    ///     This is therefore a point-in-time SNAPSHOT, valid only for discovering candidates. Its one caller — the
    ///     external-provider reconciliation pass, which has to find <c>ext:</c> rows whose registration is gone — takes
    ///     a per-model mutation lease and re-reads the row under it before touching anything, so a row that moved
    ///     between the listing and the repair is acted on at its current revision rather than the snapshot's.
    /// </remarks>
    Task<IReadOnlyList<ModelProviderMapRecord>> ListAsync(CancellationToken cancellationToken = default);

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
