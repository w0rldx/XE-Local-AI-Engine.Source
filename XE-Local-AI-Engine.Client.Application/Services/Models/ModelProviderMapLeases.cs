namespace XE_Local_AI_Engine.Client.Services.Models;

public interface IModelProviderMapReadLease : IAsyncDisposable
{
    IReadOnlyList<string> ModelKeys { get; }
    IReadOnlyList<string> MapKeys { get; }
    bool IsDisposed { get; }
    bool IsMutation { get; }
    bool ContainsModel(string modelName);
}

public interface IModelProviderMapMutationLease : IModelProviderMapReadLease
{
}

public enum ModelProviderMapMutationKind
{
    MapClaim,
    MapUpsert,
    MapRestore,
    MapRemove,
    Backfill
}

public interface IModelProviderMapLeaseCoordinator
{
    ValueTask<ModelProviderMapReadLease> AcquireMapReadAsync(string modelName, CancellationToken cancellationToken = default);
    ValueTask<ModelProviderMapReadLease> AcquireMapReadAsync(IEnumerable<string> modelNames, CancellationToken cancellationToken = default);

    ValueTask<ModelProviderMapMutationLease> AcquireMapMutationAsync(string modelName,
        ModelProviderMapMutationKind kind,
        CancellationToken cancellationToken = default);

    ValueTask<ModelProviderMapMutationLease> AcquireMapMutationAsync(IEnumerable<string> modelNames,
        ModelProviderMapMutationKind kind,
        CancellationToken cancellationToken = default);
}

public sealed class ModelProviderMapLeaseCoordinator(KeyedCompositeLockDomain lockDomain) : IModelProviderMapLeaseCoordinator
{
    private readonly KeyedCompositeLockDomain _lockDomain = lockDomain ?? throw new ArgumentNullException(nameof(lockDomain));

    public ValueTask<ModelProviderMapReadLease> AcquireMapReadAsync(string modelName, CancellationToken cancellationToken = default) =>
        AcquireMapReadAsync([modelName], cancellationToken);

    public async ValueTask<ModelProviderMapReadLease> AcquireMapReadAsync(IEnumerable<string> modelNames,
        CancellationToken cancellationToken = default)
    {
        var normalizedNames = NormalizeNames(modelNames);
        var mapKeys = normalizedNames.Select(ModelCoordinationKeys.ProviderMap).ToArray();
        var inner = await _lockDomain.AcquireReadAsync(mapKeys, cancellationToken).ConfigureAwait(false);
        return new ModelProviderMapReadLease(normalizedNames, mapKeys, inner);
    }

    public ValueTask<ModelProviderMapMutationLease> AcquireMapMutationAsync(string modelName,
        ModelProviderMapMutationKind kind,
        CancellationToken cancellationToken = default) =>
        AcquireMapMutationAsync([modelName], kind, cancellationToken);

    public async ValueTask<ModelProviderMapMutationLease> AcquireMapMutationAsync(IEnumerable<string> modelNames,
        ModelProviderMapMutationKind kind,
        CancellationToken cancellationToken = default)
    {
        var normalizedNames = NormalizeNames(modelNames);
        var mapKeys = normalizedNames.Select(ModelCoordinationKeys.ProviderMap).ToArray();
        var inner = await _lockDomain.AcquireMutationAsync(mapKeys, cancellationToken).ConfigureAwait(false);
        return new ModelProviderMapMutationLease(normalizedNames, mapKeys, kind, inner);
    }

    private static IReadOnlyList<string> NormalizeNames(IEnumerable<string> modelNames)
    {
        ArgumentNullException.ThrowIfNull(modelNames);
        var names = modelNames.Select(ModelCoordinationKeys.NormalizeModelName)
                              .Distinct(StringComparer.Ordinal)
                              .Order(StringComparer.Ordinal)
                              .ToArray();
        if (names.Length == 0)
        {
            throw new ArgumentException("At least one model name is required.", nameof(modelNames));
        }

        return names;
    }
}

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
            await inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}

public sealed class ModelProviderMapMutationLease : ModelProviderMapReadLease, IModelProviderMapMutationLease
{
    internal ModelProviderMapMutationLease(IReadOnlyList<string> modelKeys,
        IReadOnlyList<string> mapKeys,
        ModelProviderMapMutationKind kind,
        ModelCoordinationLockLease inner)
        : base(modelKeys, mapKeys, inner)
    {
        Kind = kind;
    }

    public ModelProviderMapMutationKind Kind { get; }
    public override bool IsMutation => true;
}
