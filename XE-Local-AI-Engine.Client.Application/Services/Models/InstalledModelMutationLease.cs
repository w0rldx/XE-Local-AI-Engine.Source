namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class InstalledModelMutationLease : IModelProviderMapMutationLease
{
    private ModelCoordinationLockLease? _inner;

    internal InstalledModelMutationLease(InstalledModelMutationRequest request,
        InstalledModelSnapshot? snapshot,
        ModelProviderMapRecord? providerMapping,
        ModelCoordinationLockLease inner)
    {
        Request = request;
        Snapshot = snapshot;
        ProviderMapping = providerMapping;
        _inner = inner;
        MapKeys = inner.Keys.Where(static key => key.StartsWith("2:provider-map:", StringComparison.Ordinal)).ToArray();
        ModelKeys = MapKeys.Select(static key => key["2:provider-map:".Length..]).ToArray();
        ReservedKeys = inner.Keys;
    }

    public InstalledModelMutationRequest Request { get; }
    public InstalledModelSnapshot? Snapshot { get; }
    public ModelProviderMapRecord? ProviderMapping { get; }
    public IReadOnlyList<string> ReservedKeys { get; }
    public IReadOnlyList<string> ModelKeys { get; }
    public IReadOnlyList<string> MapKeys { get; }
    public bool IsDisposed => _inner is null;
    public bool IsMutation => true;

    public bool ContainsModel(string modelName) =>
        MapKeys.Contains(ModelCoordinationKeys.ProviderMap(modelName), StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        var inner = Interlocked.Exchange(ref _inner, null);
        if (inner is not null)
        {
            await inner.DisposeAsync();
        }
    }
}
