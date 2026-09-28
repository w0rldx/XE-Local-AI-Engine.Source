namespace XE_Local_AI_Engine.Client.Services.Models.Implementation;

public sealed class ModelProviderMapLeaseCoordinator : IModelProviderMapLeaseCoordinator
{
    private readonly KeyedCompositeLockDomain _lockDomain;

    public ModelProviderMapLeaseCoordinator(KeyedCompositeLockDomain lockDomain)
    {
        ArgumentNullException.ThrowIfNull(lockDomain);
        _lockDomain = lockDomain;
    }

    public ValueTask<ModelProviderMapReadLease> AcquireMapReadAsync(string modelName, CancellationToken cancellationToken = default) =>
        AcquireMapReadAsync([modelName], cancellationToken);

    public async ValueTask<ModelProviderMapReadLease> AcquireMapReadAsync(IEnumerable<string> modelNames,
        CancellationToken cancellationToken = default)
    {
        var normalizedNames = NormalizeNames(modelNames);
        var mapKeys = normalizedNames.Select(ModelCoordinationKeys.ProviderMap).ToArray();
        var inner = await _lockDomain.AcquireReadAsync(mapKeys, cancellationToken);
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
        var inner = await _lockDomain.AcquireMutationAsync(mapKeys, cancellationToken);
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
