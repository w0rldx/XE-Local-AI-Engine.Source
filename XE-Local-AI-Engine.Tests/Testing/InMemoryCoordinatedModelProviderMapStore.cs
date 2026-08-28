namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.LlamaServer;

internal sealed class InMemoryCoordinatedModelProviderMapStore : ICoordinatedModelProviderMapStore
{
    private readonly Dictionary<string, ModelProviderMapRecord> _mappings = new(StringComparer.OrdinalIgnoreCase);
    private int _readCount;

    public IReadOnlyDictionary<string, ModelProviderMapRecord> Mappings => _mappings;
    public int ReadCount => Volatile.Read(ref _readCount);
    public int MutationCount { get; private set; }

    public void Seed(string modelName, string providerName)
    {
        _mappings[modelName] = Create(modelName, providerName);
    }

    public void ResetMutationCount() =>
        MutationCount = 0;

    public Task<IReadOnlyList<ModelProviderMapRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        // Lease-free by contract, ordered by model name like the persisted store, so a reconciliation pass under test
        // sees the same stable order it sees in production.
        return Task.FromResult<IReadOnlyList<ModelProviderMapRecord>>(
            [.. _mappings.Values.OrderBy(mapping => mapping.ModelName, StringComparer.OrdinalIgnoreCase)]);
    }

    public Task<ModelProviderMapRecord?> ReadWithRevisionAsync(IModelProviderMapReadLease lease,
        string modelName,
        CancellationToken cancellationToken = default)
    {
        Validate(lease, modelName, mutation: false);
        _ = Interlocked.Increment(ref _readCount);
        return Task.FromResult(_mappings.TryGetValue(modelName, out var mapping) ? mapping : null);
    }

    public Task<ProviderMapClaimResult> TryClaimLlamaCppAsync(IModelProviderMapMutationLease lease,
        string modelName,
        CancellationToken cancellationToken = default)
    {
        Validate(lease, modelName, mutation: true);
        if (_mappings.TryGetValue(modelName, out var current))
        {
            ProviderMapClaimResult result = string.Equals(current.ProviderName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase)
                ? new ProviderMapClaimResult.CompatibleExisting(current)
                : new ProviderMapClaimResult.Conflict(current.ProviderName);
            return Task.FromResult(result);
        }

        var inserted = Create(modelName, LlamaServerProviderConstants.ProviderName);
        _mappings[modelName] = inserted;
        MutationCount++;
        return Task.FromResult<ProviderMapClaimResult>(new ProviderMapClaimResult.Created(new ProviderMapMutationReceipt(modelName, Prior: null, Mutation: inserted, WasRemoval: false)));
    }

    public Task<ProviderMapMutationResult> TryUpsertAsync(IModelProviderMapMutationLease lease,
        string modelName,
        string providerName,
        string? expectedRevision = null,
        CancellationToken cancellationToken = default)
    {
        Validate(lease, modelName, mutation: true);
        _mappings.TryGetValue(modelName, out var current);
        if ((current is null && expectedRevision is not null)
            || (current is not null && !string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal)))
        {
            return Task.FromResult<ProviderMapMutationResult>(new ProviderMapMutationResult.Superseded(current));
        }

        var mutation = Create(modelName, providerName);
        _mappings[modelName] = mutation;
        MutationCount++;
        return Task.FromResult<ProviderMapMutationResult>(new ProviderMapMutationResult.Mutated(new ProviderMapMutationReceipt(modelName, Prior: current, Mutation: mutation, WasRemoval: false)));
    }

    public Task<ProviderMapRestoreResult> TryRestoreAsync(IModelProviderMapMutationLease lease,
        ProviderMapMutationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        Validate(lease, receipt.ModelName, mutation: true);
        throw new NotSupportedException();
    }

    public Task<ProviderMapRemovalResult> TryRemoveIfMatchAsync(IModelProviderMapMutationLease lease,
        string modelName,
        string expectedProvider,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        Validate(lease, modelName, mutation: true);
        if (!_mappings.TryGetValue(modelName, out var current))
        {
            return Task.FromResult<ProviderMapRemovalResult>(new ProviderMapRemovalResult.Absent());
        }

        // Both halves of the compare-and-swap, like the persisted store: a row whose provider or revision moved since
        // the caller read it is reported superseded rather than silently removed.
        if (!string.Equals(current.ProviderName, expectedProvider, StringComparison.Ordinal)
            || !string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
        {
            return Task.FromResult<ProviderMapRemovalResult>(new ProviderMapRemovalResult.Superseded(current));
        }

        _ = _mappings.Remove(modelName);
        MutationCount++;
        return Task.FromResult<ProviderMapRemovalResult>(
            new ProviderMapRemovalResult.Removed(new ProviderMapMutationReceipt(modelName, current, Mutation: null, WasRemoval: true)));
    }

    private static ModelProviderMapRecord Create(string modelName, string providerName) =>
        new(modelName, providerName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Guid.NewGuid().ToString("N"));

    private static void Validate(IModelProviderMapReadLease lease, string modelName, bool mutation)
    {
        if (lease.IsDisposed || !lease.ContainsModel(modelName) || (mutation && !lease.IsMutation))
        {
            throw new InvalidOperationException("A matching live coordination lease is required.");
        }
    }
}
