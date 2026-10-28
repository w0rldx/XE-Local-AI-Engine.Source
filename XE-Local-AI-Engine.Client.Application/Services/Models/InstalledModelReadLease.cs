namespace XE_Local_AI_Engine.Client.Services.Models;

public class InstalledModelReadLease : IModelProviderMapReadLease
{
    private ModelCoordinationLockLease? _inner;

    internal InstalledModelReadLease(InstalledModelSnapshot snapshot, ModelCoordinationLockLease inner)
    {
        Snapshot = snapshot;
        _inner = inner;
        MapKeys = inner.Keys.Where(static key => key.StartsWith("2:provider-map:", StringComparison.Ordinal)).ToArray();
        ModelKeys = MapKeys.Select(static key => key["2:provider-map:".Length..]).ToArray();
    }

    public InstalledModelSnapshot Snapshot { get; }
    public IReadOnlyList<string> ModelKeys { get; }
    public IReadOnlyList<string> MapKeys { get; }
    public bool IsDisposed => _inner is null;
    public virtual bool IsMutation => false;

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
