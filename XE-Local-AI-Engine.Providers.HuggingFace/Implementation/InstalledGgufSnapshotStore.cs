namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

internal sealed class InstalledGgufSnapshotStore(GgufModelRegistry registry, HuggingFaceOptions options) : IInstalledGgufSnapshotStore
{
    public async Task<InstalledGgufCandidate?> DiscoverCandidateAsync(string modelName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        try
        {
            var entries = await registry.ListAllAsync(cancellationToken).ConfigureAwait(false);
            var requested = entries.FirstOrDefault(entry => string.Equals(entry.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
            if (requested is null)
            {
                return null;
            }

            var aliases = DiscoverAliasClosure(entries, requested);
            var snapshots = aliases.Select(ToAliasSnapshot)
                                   .OrderBy(static alias => alias.ModelName, StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(static alias => alias.ModelName, StringComparer.Ordinal)
                                   .ToArray();
            var members = snapshots.SelectMany(static alias => EnumerateMemberPaths(alias))
                                   .Distinct(StringComparer.OrdinalIgnoreCase)
                                   .OrderBy(static path => path, StringComparer.Ordinal)
                                   .ToArray();
            return new InstalledGgufCandidate(requested.ModelName, Array.AsReadOnly(snapshots), Array.AsReadOnly(members));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InstalledGgufSnapshotException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InstalledGgufSnapshotException("InstalledModelSnapshotDiscoveryFailed",
                "The installed model snapshot could not be discovered safely.",
                exception);
        }
    }

    public async Task<InstalledGgufSnapshot> LoadVerifiedAsync(string modelName,
        InstalledGgufCandidate expectedCandidate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(expectedCandidate);
        try
        {
            var current = await DiscoverCandidateAsync(modelName, cancellationToken).ConfigureAwait(false)
                          ?? throw new InstalledGgufSnapshotException("InstalledModelNotFound", "The installed model is no longer available.");
            if (!CandidateMatches(expectedCandidate, current))
            {
                throw new InstalledGgufSnapshotException("InstalledModelSnapshotUnstable", "The installed model changed during snapshot acquisition.");
            }

            var requested = current.RegistryAliases.First(alias => string.Equals(alias.ModelName, modelName, StringComparison.OrdinalIgnoreCase));
            var members = await LoadMembersAsync(current.RegistryAliases, cancellationToken).ConfigureAwait(false);
            var contentMembers = members.Where(static member => member.Role is InstalledModelPhysicalMemberRole.Weight
                                                                or InstalledModelPhysicalMemberRole.Projector)
                                        .Select(static member => new GgufModelContentMember(member.RelativePath,
                                            member.Role,
                                            member.SizeBytes,
                                            member.Sha256,
                                            member.OwningAliases))
                                        .ToArray();
            var modelContentFingerprint = GgufModelContentFingerprint.ComputeV1(contentMembers);
            if (requested.RegistryValue.ModelContentFingerprint is not null
                && !string.Equals(requested.RegistryValue.ModelContentFingerprint, modelContentFingerprint, StringComparison.Ordinal))
            {
                throw new InstalledGgufSnapshotException("InstalledModelContentFingerprintMismatch",
                    "The installed model content no longer matches its recorded fingerprint.");
            }

            return new InstalledGgufSnapshot(requested.ModelName,
                requested.RegistryRevision,
                current.RegistryAliases,
                GgufRegistryAliasSetHash.ComputeV1(current.RegistryAliases),
                members,
                GgufPhysicalMemberSetHash.ComputeV1(members),
                requested.RegistryValue.Origin,
                requested.RegistryValue.RepoId,
                requested.RegistryValue.SourceRevision,
                requested.RegistryValue.Quant,
                requested.RegistryValue.Role,
                modelContentFingerprint);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InstalledGgufSnapshotException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InstalledGgufSnapshotException("InstalledModelSnapshotVerificationFailed",
                "The installed model snapshot could not be verified safely.",
                exception);
        }
    }

    private async Task<IReadOnlyList<InstalledModelPhysicalMember>> LoadMembersAsync(
        IReadOnlyList<InstalledModelRegistryAliasSnapshot> aliases,
        CancellationToken cancellationToken)
    {
        var observations = new Dictionary<string, MemberObservation>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            AddObservation(observations, alias.WeightRelativePath, InstalledModelPhysicalMemberRole.Weight, alias.ModelName,
                metadataSchemaVersion: null);
            if (alias.ProjectorRelativePath is not null)
            {
                AddObservation(observations, alias.ProjectorRelativePath, InstalledModelPhysicalMemberRole.Projector, alias.ModelName,
                    metadataSchemaVersion: null);
            }

            if (alias.SidecarRelativePath is not null)
            {
                AddObservation(observations, alias.SidecarRelativePath, InstalledModelPhysicalMemberRole.Sidecar, alias.ModelName,
                    GgufAcquisitionMetadata.CurrentSchemaVersion);
            }
        }

        var members = new List<InstalledModelPhysicalMember>(observations.Count);
        foreach (var observation in observations.Values.OrderBy(static observation => observation.RelativePath, StringComparer.Ordinal))
        {
            var absolutePath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, observation.RelativePath);
            var info = new FileInfo(absolutePath);
            if (!info.Exists || info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InstalledGgufSnapshotException("InstalledModelMemberMissing", "A required installed model member is unavailable.");
            }

            var sha256 = await GgufAcquisitionSidecar.ComputeSha256Async(absolutePath, cancellationToken).ConfigureAwait(false);
            var fingerprint = observation.Role == InstalledModelPhysicalMemberRole.Sidecar
                ? null
                : GgufMemberFingerprint.Compute(sha256, info.Length);
            members.Add(new InstalledModelPhysicalMember(observation.RelativePath,
                observation.Role,
                info.Length,
                sha256,
                fingerprint,
                Array.AsReadOnly(observation.OwningAliases.OrderBy(static alias => alias, StringComparer.OrdinalIgnoreCase)
                                            .ThenBy(static alias => alias, StringComparer.Ordinal)
                                            .ToArray()),
                Required: true,
                observation.Role == InstalledModelPhysicalMemberRole.Sidecar ? observation.MetadataSchemaVersion : null));
        }

        foreach (var alias in aliases.Where(static alias => alias.RegistryValue.Origin is not null))
        {
            if (alias.SidecarRelativePath is null)
            {
                throw new InstalledGgufSnapshotException("InstalledModelSidecarMissing", "The acquired model recovery metadata is unavailable.");
            }

            var weightPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, alias.WeightRelativePath);
            var sidecarPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, alias.SidecarRelativePath);
            if (await GgufAcquisitionSidecar.ReadValidAsync(sidecarPath, weightPath, options.ModelsDirectory, cancellationToken).ConfigureAwait(false) is null)
            {
                throw new InstalledGgufSnapshotException("InstalledModelSidecarInvalid", "The acquired model recovery metadata is invalid.");
            }
        }

        VerifyRegistryFingerprints(aliases, members);
        return Array.AsReadOnly(members.ToArray());
    }

    private InstalledModelRegistryAliasSnapshot ToAliasSnapshot(GgufModelRegistryEntry entry)
    {
        var weight = GgufFilePath.GetRelativeContainedPath(options.ModelsDirectory, entry.LocalPath);
        var projector = entry.ProjectorLocalPath is null
            ? null
            : GgufFilePath.GetRelativeContainedPath(options.ModelsDirectory, entry.ProjectorLocalPath);
        var sidecarAbsolute = entry.LocalPath + GgufAcquisitionSidecar.Suffix;
        var sidecar = File.Exists(sidecarAbsolute)
            ? GgufFilePath.GetRelativeContainedPath(options.ModelsDirectory, sidecarAbsolute)
            : null;
        var revision = GgufRegistryRevision.ComputeV1(entry, options.ModelsDirectory);
        if (!string.Equals(entry.RegistryRevision, revision, StringComparison.Ordinal))
        {
            throw new InstalledGgufSnapshotException("RegistryRevisionMismatch", "An installed model registry revision is invalid.");
        }

        var registryValue = new InstalledGgufRegistryValue(entry.RepoId,
            entry.FileName,
            entry.Quant,
            weight,
            entry.SizeBytes,
            entry.Sha256,
            entry.SourceRevision,
            entry.DownloadedAtUtc,
            entry.Role,
            entry.ProjectorFileName,
            projector,
            entry.ProjectorSizeBytes,
            entry.ProjectorSha256,
            entry.Origin,
            entry.SourceDisplayName,
            entry.MetadataSchemaVersion,
            entry.ModelContentFingerprint);
        return new InstalledModelRegistryAliasSnapshot(entry.ModelName, registryValue, revision, weight, projector, sidecar);
    }

    private static IReadOnlyList<GgufModelRegistryEntry> DiscoverAliasClosure(IReadOnlyList<GgufModelRegistryEntry> entries,
        GgufModelRegistryEntry requested)
    {
        var closure = new List<GgufModelRegistryEntry> { requested };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(requested.LocalPath) };
        if (requested.ProjectorLocalPath is not null)
        {
            paths.Add(Path.GetFullPath(requested.ProjectorLocalPath));
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var entry in entries)
            {
                if (closure.Contains(entry))
                {
                    continue;
                }

                var weight = Path.GetFullPath(entry.LocalPath);
                var projector = entry.ProjectorLocalPath is null ? null : Path.GetFullPath(entry.ProjectorLocalPath);
                if (!paths.Contains(weight) && (projector is null || !paths.Contains(projector)))
                {
                    continue;
                }

                closure.Add(entry);
                changed = true;
                _ = paths.Add(weight);
                if (projector is not null)
                {
                    _ = paths.Add(projector);
                }
            }
        }

        return closure;
    }

    private static IEnumerable<string> EnumerateMemberPaths(InstalledModelRegistryAliasSnapshot alias)
    {
        yield return alias.WeightRelativePath;
        if (alias.ProjectorRelativePath is not null)
        {
            yield return alias.ProjectorRelativePath;
        }

        if (alias.SidecarRelativePath is not null)
        {
            yield return alias.SidecarRelativePath;
        }
    }

    private static bool CandidateMatches(InstalledGgufCandidate expected, InstalledGgufCandidate current)
    {
        return string.Equals(expected.ModelName, current.ModelName, StringComparison.Ordinal)
               && expected.RegistryAliases.Select(static alias => (alias.ModelName, alias.RegistryRevision))
                          .SequenceEqual(current.RegistryAliases.Select(static alias => (alias.ModelName, alias.RegistryRevision)))
               && expected.MemberRelativePaths.SequenceEqual(current.MemberRelativePaths, StringComparer.Ordinal);
    }

    private static void AddObservation(IDictionary<string, MemberObservation> observations,
        string relativePath,
        InstalledModelPhysicalMemberRole role,
        string alias,
        int? metadataSchemaVersion)
    {
        if (observations.TryGetValue(relativePath, out var existing))
        {
            if (existing.Role != role || existing.MetadataSchemaVersion != metadataSchemaVersion)
            {
                throw new InstalledGgufSnapshotException("InstalledModelMemberConflict",
                    "Installed model aliases disagree about a shared physical member.");
            }

            existing.OwningAliases.Add(alias);
            return;
        }

        observations[relativePath] = new MemberObservation(relativePath, role, metadataSchemaVersion,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { alias });
    }

    private static void VerifyRegistryFingerprints(IReadOnlyList<InstalledModelRegistryAliasSnapshot> aliases,
        IReadOnlyList<InstalledModelPhysicalMember> members)
    {
        foreach (var alias in aliases)
        {
            var weight = members.Single(member => string.Equals(member.RelativePath, alias.WeightRelativePath, StringComparison.OrdinalIgnoreCase));
            if (alias.RegistryValue.SizeBytes != weight.SizeBytes
                || alias.RegistryValue.Sha256 is not null && !string.Equals(alias.RegistryValue.Sha256, weight.Sha256, StringComparison.Ordinal))
            {
                throw new InstalledGgufSnapshotException("InstalledModelMemberFingerprintMismatch",
                    "The installed model weight no longer matches its registry value.");
            }

            if (alias.ProjectorRelativePath is null)
            {
                continue;
            }

            var projector = members.Single(member => string.Equals(member.RelativePath, alias.ProjectorRelativePath, StringComparison.OrdinalIgnoreCase));
            if (alias.RegistryValue.ProjectorSizeBytes != projector.SizeBytes
                || !string.Equals(alias.RegistryValue.ProjectorSha256, projector.Sha256, StringComparison.Ordinal))
            {
                throw new InstalledGgufSnapshotException("InstalledModelMemberFingerprintMismatch",
                    "The installed model projector no longer matches its registry value.");
            }
        }
    }

    private sealed record MemberObservation(string RelativePath,
        InstalledModelPhysicalMemberRole Role,
        int? MetadataSchemaVersion,
        HashSet<string> OwningAliases);
}
