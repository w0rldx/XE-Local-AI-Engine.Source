namespace XE_Local_AI_Engine.Client.Services.Models.Implementation;

public class ModelProviderMapReadLease : IModelProviderMapReadLease
{
    private ModelCoordinationLockLease? _inner;

    internal ModelProviderMapReadLease(IReadOnlyList<string> modelKeys,
        IReadOnlyList<string> mapKeys,
        ModelCoordinationLockLease inner)
    {
        ModelKeys = modelKeys;
        MapKeys = mapKeys;
        _inner = inner;
    }

    public IReadOnlyList<string> ModelKeys { get; }
    public IReadOnlyList<string> MapKeys { get; }
    public bool IsDisposed => _inner is null;
    public virtual bool IsMutation => false;

    public bool ContainsModel(string modelName)
    {
        var key = ModelCoordinationKeys.ProviderMap(modelName);
        return MapKeys.Contains(key, StringComparer.Ordinal);
    }

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
