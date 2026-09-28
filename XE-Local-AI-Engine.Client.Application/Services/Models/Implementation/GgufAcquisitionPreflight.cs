namespace XE_Local_AI_Engine.Client.Services.Models.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

public sealed class GgufAcquisitionPreflight : IGgufAcquisitionPreflight
{
    private readonly GgufAcquisitionIdentityResolver _identityResolver;
    private readonly IInstalledModelSnapshotCoordinator _snapshotCoordinator;
    private readonly GgufAcquisitionStateProbe _stateProbe;

    public GgufAcquisitionPreflight(GgufAcquisitionIdentityResolver identityResolver,
        IInstalledModelSnapshotCoordinator snapshotCoordinator,
        GgufAcquisitionStateProbe stateProbe)
    {
        ArgumentNullException.ThrowIfNull(identityResolver);
        ArgumentNullException.ThrowIfNull(snapshotCoordinator);
        ArgumentNullException.ThrowIfNull(stateProbe);
        _identityResolver = identityResolver;
        _snapshotCoordinator = snapshotCoordinator;
        _stateProbe = stateProbe;
    }

    public async Task<PreparedGgufAcquisition> ResolveAndReserveAsync(GgufAcquisitionIntent intent,
        CancellationToken cancellationToken = default)
    {
        var identity = _identityResolver.Resolve(intent);
        var members = new List<IntendedInstalledModelMember>
        {
            new()
            {
                RelativePath = identity.RelativeGgufPath,
                Role = InstalledModelPhysicalMemberRole.Weight
            },
            new()
            {
                RelativePath = identity.RelativeSidecarPath,
                Role = InstalledModelPhysicalMemberRole.Sidecar
            }
        };
        if (identity.ProjectorRelativePath is not null)
        {
            members.Add(new IntendedInstalledModelMember
            {
                RelativePath = identity.ProjectorRelativePath,
                Role = InstalledModelPhysicalMemberRole.Projector
            });
        }

        var lease = await _snapshotCoordinator.AcquireMutationAsync(new InstalledModelMutationRequest
            {
                ModelName = identity.CanonicalModelName,
                Kind = InstalledModelMutationKind.Acquire,
                IntendedMembers = members
            },
            cancellationToken);
        try
        {
            var state = await _stateProbe.ProbeAsync(intent, identity, lease, cancellationToken);
            if (state.Disposition == GgufAcquisitionDisposition.Conflict
                || state.ProviderMapDisposition == ProviderMapDisposition.ConflictingProvider
                || (intent.OperationKind == GgufAcquisitionOperationKind.Import && state.Disposition != GgufAcquisitionDisposition.Available))
            {
                throw new GgufAcquisitionConflictException();
            }

            if (state.Disposition == GgufAcquisitionDisposition.ActiveCompatible)
            {
                await lease.DisposeAsync();
                return new PreparedGgufAcquisition(identity, state.Disposition, state.ProviderMapDisposition, lease: null, state.ActiveOperationId);
            }

            return new PreparedGgufAcquisition(identity, state.Disposition, state.ProviderMapDisposition, lease, state.ActiveOperationId);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }
}
