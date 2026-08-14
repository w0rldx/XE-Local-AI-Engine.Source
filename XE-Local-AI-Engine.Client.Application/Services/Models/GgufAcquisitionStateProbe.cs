namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.LlamaServer;

public sealed class GgufAcquisitionStateProbe(HuggingFaceOptions? options = null) : IGgufAcquisitionStateProbe
{
    public Task<GgufAcquisitionState> ProbeAsync(GgufAcquisitionIntent intent,
        ResolvedGgufAcquisitionIdentity identity,
        InstalledModelMutationLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
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

        var disposition = lease.Snapshot is null && HasDestinationCollision(identity)
            ? GgufAcquisitionDisposition.Conflict
            : GetDisposition(intent, identity, lease.Snapshot, mapDisposition);
        return Task.FromResult(new GgufAcquisitionState(disposition, mapDisposition, mapping?.ProviderName));
    }

    private bool HasDestinationCollision(ResolvedGgufAcquisitionIdentity identity)
    {
        if (options is null)
        {
            return false;
        }

        return HasCaseInsensitiveCollision(GgufFilePath.ResolveContainedPath(options.ModelsDirectory, identity.RelativeGgufPath))
               || HasCaseInsensitiveCollision(GgufFilePath.ResolveContainedPath(options.ModelsDirectory, identity.RelativeSidecarPath))
               || identity.ProjectorRelativePath is not null
               && HasCaseInsensitiveCollision(GgufFilePath.ResolveContainedPath(options.ModelsDirectory, identity.ProjectorRelativePath));
    }

    private static bool HasCaseInsensitiveCollision(string path)
    {
        var directory = Path.GetDirectoryName(path);
        return directory is not null
               && Directory.Exists(directory)
               && Directory.EnumerateFileSystemEntries(directory)
                           .Any(existing => string.Equals(Path.GetFileName(existing), Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
    }

    private static GgufAcquisitionDisposition GetDisposition(GgufAcquisitionIntent intent,
        ResolvedGgufAcquisitionIdentity identity,
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

        if (intent.OperationKind != GgufAcquisitionOperationKind.Download
            || intent.Download is not { } download
            || !MatchesResolvedDownload(snapshot, identity, download, intent.Projector))
        {
            return GgufAcquisitionDisposition.Conflict;
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

    private static bool MatchesResolvedDownload(InstalledModelSnapshot snapshot,
        ResolvedGgufAcquisitionIdentity identity,
        GgufDownloadAcquisitionMetadata download,
        GgufProjectorAcquisitionMetadata? projector)
    {
        if (download.DeclaredSha256 is null
            || !string.Equals(snapshot.RepoId, download.RepoId, StringComparison.Ordinal)
            || !string.Equals(snapshot.SourceRevision, download.ResolvedRevision, StringComparison.Ordinal)
            || snapshot.Role != download.Role)
        {
            return false;
        }

        var requestedAlias = snapshot.RegistryAliases.FirstOrDefault(alias =>
            string.Equals(alias.ModelName, identity.CanonicalModelName, StringComparison.OrdinalIgnoreCase));
        if (requestedAlias is null
            || !string.Equals(requestedAlias.RegistryValue.RepoId, download.RepoId, StringComparison.Ordinal)
            || !string.Equals(requestedAlias.RegistryValue.SourceRevision, download.ResolvedRevision, StringComparison.Ordinal)
            || !string.Equals(requestedAlias.RegistryValue.SourceDisplayName, download.SourceDisplayName, StringComparison.Ordinal)
            || requestedAlias.RegistryValue.Role != download.Role)
        {
            return false;
        }

        var weight = snapshot.Members.Where(static member => member.Role == InstalledModelPhysicalMemberRole.Weight).ToArray();
        var projectors = snapshot.Members.Where(static member => member.Role == InstalledModelPhysicalMemberRole.Projector).ToArray();
        if (weight.Length != 1
            || weight[0].SizeBytes != download.DeclaredSizeBytes
            || !string.Equals(weight[0].Sha256, download.DeclaredSha256, StringComparison.Ordinal)
            || !string.Equals(weight[0].MemberFingerprint,
                GgufMemberFingerprint.Compute(download.DeclaredSha256, download.DeclaredSizeBytes),
                StringComparison.Ordinal)
            || projectors.Length != (projector is null ? 0 : 1))
        {
            return false;
        }

        if (projector is not null
            && (projectors[0].SizeBytes != projector.DeclaredSizeBytes
                || !string.Equals(projectors[0].Sha256, projector.DeclaredSha256, StringComparison.Ordinal)
                || !string.Equals(projectors[0].MemberFingerprint,
                    GgufMemberFingerprint.Compute(projector.DeclaredSha256, projector.DeclaredSizeBytes),
                    StringComparison.Ordinal)
                || !string.Equals(requestedAlias.RegistryValue.ProjectorFileName, projector.SourceDisplayName, StringComparison.Ordinal)))
        {
            return false;
        }

        var expectedMembers = weight.Select(member => new GgufModelContentMember(member.RelativePath,
                member.Role,
                download.DeclaredSizeBytes,
                download.DeclaredSha256,
                member.OwningAliases))
            .Concat(projectors.Select(member => new GgufModelContentMember(member.RelativePath,
                member.Role,
                projector!.DeclaredSizeBytes,
                projector.DeclaredSha256,
                member.OwningAliases)));
        return string.Equals(GgufModelContentFingerprint.ComputeV1(expectedMembers),
            snapshot.ModelContentFingerprint,
            StringComparison.Ordinal);
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
