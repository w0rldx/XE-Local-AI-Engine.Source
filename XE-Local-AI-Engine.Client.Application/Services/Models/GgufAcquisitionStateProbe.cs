namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Providers.LlamaServer;

public sealed class GgufAcquisitionStateProbe(ICoordinatedModelProviderMapStore providerMapStore) : IGgufAcquisitionStateProbe
{
    private readonly ICoordinatedModelProviderMapStore _providerMapStore = providerMapStore ?? throw new ArgumentNullException(nameof(providerMapStore));

    public async Task<GgufAcquisitionState> ProbeAsync(ResolvedGgufAcquisitionIdentity identity,
        InstalledModelMutationLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(lease);

        var mapping = await _providerMapStore.ReadWithRevisionAsync(lease, identity.CanonicalModelName, cancellationToken).ConfigureAwait(false);
        var mapDisposition = mapping switch
        {
            null => ProviderMapDisposition.Absent,
            { ProviderName: var provider } when string.Equals(provider, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase) =>
                ProviderMapDisposition.CompatibleLlamaCpp,
            _ => ProviderMapDisposition.ConflictingProvider
        };

        var disposition = lease.Snapshot is null && mapDisposition != ProviderMapDisposition.ConflictingProvider
            ? GgufAcquisitionDisposition.Available
            : GgufAcquisitionDisposition.Conflict;
        return new GgufAcquisitionState(disposition, mapDisposition, mapping?.ProviderName);
    }
}
