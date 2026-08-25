namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

internal sealed class InstalledGgufSnapshotStore(GgufModelRegistry registry, HuggingFaceOptions options) : IInstalledGgufSnapshotStore
{
    /// <summary>
    ///     Digests of members already verified in this process. The store is a singleton, so one memo per node; see
    ///     <see cref="GgufMemberHashMemo" /> for the key and its ceiling.
    /// </summary>
    internal GgufMemberHashMemo MemberHashMemo { get; } = new();

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

    private async Task<IReadOnlyList<InstalledModelPhysicalMember>> LoadMembersAsync(IReadOnlyList<InstalledModelRegistryAliasSnapshot> aliases,
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

            // Re-reading a multi-gigabyte weight file per acquire is the whole cost of verification, and the file is
            // unchanged almost every time — the benchmark freeze alone acquires once per queued run.
            var lastWriteTimeUtc = info.LastWriteTimeUtc;
            var sha256 = MemberHashMemo.TryGet(absolutePath, info.Length, lastWriteTimeUtc);
            if (sha256 is null)
            {
                sha256 = await GgufAcquisitionSidecar.ComputeSha256Async(absolutePath, cancellationToken).ConfigureAwait(false);
                MemberHashMemo.Set(absolutePath, info.Length, lastWriteTimeUtc, sha256);
            }

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
            entry.ModelContentFingerprint,
            entry.DerivedFromRepoId,
            entry.DerivedFromRevision,
            entry.DerivedFromContentFingerprint,
            entry.AdapterFileName,
            entry.AdapterSha256,
            entry.AdapterSizeBytes,
            entry.AdapterMemberFingerprint,
            entry.BaseModelName);
        return new InstalledModelRegistryAliasSnapshot(entry.ModelName, registryValue, revision, weight, projector, sidecar);
    }

    private static IReadOnlyList<GgufModelRegistryEntry> DiscoverAliasClosure(IReadOnlyList<GgufModelRegistryEntry> entries,
        GgufModelRegistryEntry requested)
    {
        var closure = new List<GgufModelRegistryEntry>
        {
            requested
        };
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(requested.LocalPath)
        };
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
            new HashSet<string>(StringComparer.Ordinal)
            {
                alias
            });
    }

    /// <summary>
    ///     Compares the recorded registry digests against the freshly computed ones. The comparison is
    ///     case-INSENSITIVE on purpose: registry entries written before the sidecar era persisted
    ///     <c>Convert.ToHexString</c> (UPPERCASE) hex, while every current write path and
    ///     <see cref="GgufAcquisitionSidecar.ComputeSha256Async" /> produce lowercase. An ordinal compare therefore
    ///     failed every legacy entry with <c>InstalledModelMemberFingerprintMismatch</c>, which took down whole
    ///     catalog endpoints that verify each installed model. Only the comparison is relaxed — the persisted
    ///     fingerprint/revision inputs are untouched, so no identity changes.
    /// </summary>
    private static void VerifyRegistryFingerprints(IReadOnlyList<InstalledModelRegistryAliasSnapshot> aliases,
        IReadOnlyList<InstalledModelPhysicalMember> members)
    {
        foreach (var alias in aliases)
        {
            var weight = members.Single(member => string.Equals(member.RelativePath, alias.WeightRelativePath, StringComparison.OrdinalIgnoreCase));
            if (alias.RegistryValue.SizeBytes != weight.SizeBytes
                || alias.RegistryValue.Sha256 is not null && !string.Equals(alias.RegistryValue.Sha256, weight.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstalledGgufSnapshotException("InstalledModelMemberFingerprintMismatch",
                    "The installed model weight no longer matches its registry value.");
            }

            if (alias.ProjectorRelativePath is null)
            {
                continue;
            }

            var projector = members.Single(member => string.Equals(member.RelativePath, alias.ProjectorRelativePath, StringComparison.OrdinalIgnoreCase));
            var acquired = alias.RegistryValue.Origin is not null;
            if (acquired && (alias.RegistryValue.ProjectorSizeBytes is null || alias.RegistryValue.ProjectorSha256 is null)
                || alias.RegistryValue.ProjectorSizeBytes is { } projectorSize && projectorSize != projector.SizeBytes
                || alias.RegistryValue.ProjectorSha256 is { } projectorHash
                && !string.Equals(projectorHash, projector.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InstalledGgufSnapshotException("InstalledModelMemberFingerprintMismatch",
                    "The installed model projector no longer matches its registry value.");
            }
        }
    }

    private sealed record MemberObservation(
        string RelativePath,
        InstalledModelPhysicalMemberRole Role,
        int? MetadataSchemaVersion,
        HashSet<string> OwningAliases);
}
