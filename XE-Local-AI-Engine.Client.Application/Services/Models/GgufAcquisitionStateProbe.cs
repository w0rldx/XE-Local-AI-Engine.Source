namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;

public sealed class GgufAcquisitionStateProbe : IGgufAcquisitionStateProbe
{
    public Task<GgufAcquisitionState> ProbeAsync(ResolvedGgufAcquisitionIdentity identity,
        InstalledModelMutationLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();

        var mapping = lease.ProviderMapping;
        var mapDisposition = mapping switch
        {
            null => ProviderMapDisposition.Absent,
            { ProviderName: var provider } when string.Equals(provider, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase) =>
                ProviderMapDisposition.CompatibleLlamaCpp,
            _ => ProviderMapDisposition.ConflictingProvider
        };

        var disposition = GetDisposition(identity, lease.Snapshot, mapDisposition);
        return Task.FromResult(new GgufAcquisitionState(disposition, mapDisposition, mapping?.ProviderName));
    }

    private static GgufAcquisitionDisposition GetDisposition(ResolvedGgufAcquisitionIdentity identity,
        InstalledModelSnapshot? snapshot,
        ProviderMapDisposition mapDisposition)
    {
        if (mapDisposition == ProviderMapDisposition.ConflictingProvider)
        {
            return GgufAcquisitionDisposition.Conflict;
        }

        if (snapshot is null)
        {
            return GgufAcquisitionDisposition.Available;
        }

        if (!string.Equals(ModelCoordinationKeys.NormalizeModelName(snapshot.ModelName), identity.ModelReservationKey, StringComparison.Ordinal)
            || !string.Equals(snapshot.Quantization, identity.CanonicalQuantization, StringComparison.Ordinal)
            || !HasVerifiedFingerprintFacts(snapshot))
        {
            return GgufAcquisitionDisposition.Conflict;
        }

        if (snapshot.Origin == LocalModelOrigin.HuggingFace && HasCurrentAcquisitionMembers(snapshot, identity))
        {
            return GgufAcquisitionDisposition.VerifiedInstalled;
        }

        if (snapshot.Origin is null
            && snapshot.Members.All(static member => member.Role != InstalledModelPhysicalMemberRole.Sidecar)
            && HasExpectedLegacyContentShape(snapshot, identity))
        {
            return GgufAcquisitionDisposition.VerifiedLegacyInstalled;
        }

        return GgufAcquisitionDisposition.Conflict;
    }

    private static bool HasCurrentAcquisitionMembers(InstalledModelSnapshot snapshot, ResolvedGgufAcquisitionIdentity identity)
    {
        if (!HasMember(snapshot, InstalledModelPhysicalMemberRole.Weight, identity.RelativeGgufPath)
            || !HasMember(snapshot, InstalledModelPhysicalMemberRole.Sidecar, identity.RelativeSidecarPath))
        {
            return false;
        }

        var projectors = snapshot.Members.Where(static member => member.Role == InstalledModelPhysicalMemberRole.Projector).ToArray();
        return identity.ProjectorRelativePath is null
            ? projectors.Length == 0
            : projectors.Length == 1 && PathsEqual(projectors[0].RelativePath, identity.ProjectorRelativePath);
    }

    private static bool HasExpectedLegacyContentShape(InstalledModelSnapshot snapshot, ResolvedGgufAcquisitionIdentity identity)
    {
        var weights = snapshot.Members.Count(static member => member.Role == InstalledModelPhysicalMemberRole.Weight);
        var projectors = snapshot.Members.Count(static member => member.Role == InstalledModelPhysicalMemberRole.Projector);
        return weights == 1 && projectors == (identity.ProjectorRelativePath is null ? 0 : 1);
    }

    private static bool HasMember(InstalledModelSnapshot snapshot, InstalledModelPhysicalMemberRole role, string path) =>
        snapshot.Members.Any(member => member.Role == role && PathsEqual(member.RelativePath, path));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(ModelCoordinationKeys.Path(left), ModelCoordinationKeys.Path(right), StringComparison.Ordinal);

    private static bool HasVerifiedFingerprintFacts(InstalledModelSnapshot snapshot)
    {
        try
        {
            var contentMembers = snapshot.Members.Where(static member => member.Role is InstalledModelPhysicalMemberRole.Weight
                                                                         or InstalledModelPhysicalMemberRole.Projector)
                                         .Select(static member => new GgufModelContentMember(member.RelativePath,
                                             member.Role,
                                             member.SizeBytes,
                                             member.Sha256,
                                             member.OwningAliases));
            return snapshot.Members.Where(static member => member.Role != InstalledModelPhysicalMemberRole.Sidecar)
                           .All(static member => GgufMemberFingerprint.IsCanonical(member.MemberFingerprint))
                   && string.Equals(GgufRegistryAliasSetHash.ComputeV1(snapshot.RegistryAliases),
                       snapshot.RegistryAliasSetHash,
                       StringComparison.Ordinal)
                   && string.Equals(GgufPhysicalMemberSetHash.ComputeV1(snapshot.Members),
                       snapshot.PhysicalMemberSetHash,
                       StringComparison.Ordinal)
                   && string.Equals(GgufModelContentFingerprint.ComputeV1(contentMembers),
                       snapshot.ModelContentFingerprint,
                       StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
