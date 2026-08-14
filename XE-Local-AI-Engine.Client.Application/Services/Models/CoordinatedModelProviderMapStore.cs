namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.LlamaServer;

public sealed class CoordinatedModelProviderMapStore : ICoordinatedModelProviderMapStore
{
    private readonly IModelProviderMapStore _persistence;

    internal CoordinatedModelProviderMapStore(IModelProviderMapStore persistence)
    {
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    public Task<ModelProviderMapRecord?> ReadWithRevisionAsync(IModelProviderMapReadLease lease,
        string modelName,
        CancellationToken cancellationToken = default)
    {
        ValidateReadLease(lease, modelName);
        return _persistence.ReadAsync(modelName, cancellationToken);
    }

    public async Task<ProviderMapClaimResult> TryClaimLlamaCppAsync(IModelProviderMapMutationLease lease,
        string modelName,
        CancellationToken cancellationToken = default)
    {
        ValidateMutationLease(lease, modelName);
        var current = await _persistence.ReadAsync(modelName, cancellationToken).ConfigureAwait(false);
        if (current is not null)
        {
            return string.Equals(current.ProviderName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase)
                ? new ProviderMapClaimResult.CompatibleExisting(current)
                : new ProviderMapClaimResult.Conflict(current.ProviderName);
        }

        var inserted = await _persistence.TryInsertAsync(modelName, LlamaServerProviderConstants.ProviderName, cancellationToken).ConfigureAwait(false);
        if (inserted is not null)
        {
            return new ProviderMapClaimResult.Created(new ProviderMapMutationReceipt(modelName, Prior: null, Mutation: inserted, WasRemoval: false));
        }

        current = await _persistence.ReadAsync(modelName, cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("The provider-map claim lost a race but no current row is readable.");
        return string.Equals(current.ProviderName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase)
            ? new ProviderMapClaimResult.CompatibleExisting(current)
            : new ProviderMapClaimResult.Conflict(current.ProviderName);
    }

    public async Task<ProviderMapMutationResult> TryUpsertAsync(IModelProviderMapMutationLease lease,
        string modelName,
        string providerName,
        string? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        ValidateMutationLease(lease, modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        var current = await _persistence.ReadAsync(modelName, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            if (expectedRevision is not null)
            {
                return new ProviderMapMutationResult.Superseded(Current: null);
            }

            var inserted = await _persistence.TryInsertAsync(modelName, providerName, cancellationToken).ConfigureAwait(false);
            return inserted is null
                ? new ProviderMapMutationResult.Superseded(await _persistence.ReadAsync(modelName, cancellationToken).ConfigureAwait(false))
                : new ProviderMapMutationResult.Mutated(new ProviderMapMutationReceipt(modelName, Prior: null, Mutation: inserted, WasRemoval: false));
        }

        if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
        {
            return new ProviderMapMutationResult.Superseded(current);
        }

        var updated = await _persistence.TryUpdateAsync(modelName, providerName, current.Revision, cancellationToken).ConfigureAwait(false);
        return updated is null
            ? new ProviderMapMutationResult.Superseded(await _persistence.ReadAsync(modelName, cancellationToken).ConfigureAwait(false))
            : new ProviderMapMutationResult.Mutated(new ProviderMapMutationReceipt(modelName, Prior: current, Mutation: updated, WasRemoval: false));
    }

    public async Task<ProviderMapRestoreResult> TryRestoreAsync(IModelProviderMapMutationLease lease,
        ProviderMapMutationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ValidateMutationLease(lease, receipt.ModelName);

        if (receipt.WasRemoval)
        {
            if (receipt.Prior is null || receipt.Mutation is not null)
            {
                throw new ArgumentException("The provider-map removal receipt is malformed.", nameof(receipt));
            }

            var restored = await _persistence.TryInsertAsync(receipt.Prior.ModelName, receipt.Prior.ProviderName, cancellationToken).ConfigureAwait(false);
            return restored is null ? ProviderMapRestoreResult.Superseded : ProviderMapRestoreResult.Restored;
        }

        if (receipt.Mutation is null)
        {
            throw new ArgumentException("The provider-map mutation receipt is malformed.", nameof(receipt));
        }

        if (receipt.Prior is null)
        {
            var deleted = await _persistence.TryDeleteAsync(receipt.ModelName,
                receipt.Mutation.ProviderName,
                receipt.Mutation.Revision,
                cancellationToken).ConfigureAwait(false);
            return deleted ? ProviderMapRestoreResult.Restored : ProviderMapRestoreResult.Superseded;
        }

        var updated = await _persistence.TryUpdateAsync(receipt.ModelName,
            receipt.Prior.ProviderName,
            receipt.Mutation.Revision,
            cancellationToken).ConfigureAwait(false);
        return updated is null ? ProviderMapRestoreResult.Superseded : ProviderMapRestoreResult.Restored;
    }

    public async Task<ProviderMapRemovalResult> TryRemoveIfMatchAsync(IModelProviderMapMutationLease lease,
        string modelName,
        string expectedProvider,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ValidateMutationLease(lease, modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);

        var current = await _persistence.ReadAsync(modelName, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return new ProviderMapRemovalResult.Absent();
        }

        if (!string.Equals(current.ProviderName, expectedProvider, StringComparison.Ordinal)
            || !string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
        {
            return new ProviderMapRemovalResult.Superseded(current);
        }

        var removed = await _persistence.TryDeleteAsync(modelName, expectedProvider, expectedRevision, cancellationToken).ConfigureAwait(false);
        if (!removed)
        {
            current = await _persistence.ReadAsync(modelName, cancellationToken).ConfigureAwait(false);
            return current is null ? new ProviderMapRemovalResult.Absent() : new ProviderMapRemovalResult.Superseded(current);
        }

        return new ProviderMapRemovalResult.Removed(new ProviderMapMutationReceipt(modelName, Prior: current, Mutation: null, WasRemoval: true));
    }

    private static void ValidateReadLease(IModelProviderMapReadLease lease, string modelName)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.IsDisposed || !lease.ContainsModel(modelName))
        {
            throw new InvalidOperationException("A live provider-map lease containing the requested model key is required.");
        }
    }

    private static void ValidateMutationLease(IModelProviderMapMutationLease lease, string modelName)
    {
        ValidateReadLease(lease, modelName);
        if (!lease.IsMutation)
        {
            throw new InvalidOperationException("A provider-map mutation requires a mutation lease.");
        }
    }
}
