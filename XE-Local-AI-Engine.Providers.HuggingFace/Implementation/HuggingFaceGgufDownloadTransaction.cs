namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

internal sealed class HuggingFaceGgufDownloadTransaction(
    HfDownloadClient downloadClient,
    IHuggingFaceGgufDiscovery discovery,
    GgufModelRegistry registry,
    HuggingFaceOptions options,
    TimeProvider timeProvider) : IGgufDownloadTransaction
{
    public async Task<ResolvedGgufDownload> ResolveAsync(GgufModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RepoId))
        {
            throw new ArgumentException("The repository id is required.", nameof(request));
        }
        var detail = await discovery.ListRepoFilesAsync(request.RepoId, cancellationToken).ConfigureAwait(false);
        var selected = ResolveFile(detail.Files, request, options.DefaultQuant);
        EnsureSafeSource(selected.FileName, selected.SizeBytes, selected.Sha256, selected.Revision);

        if (request.Revision is not null && !string.Equals(request.Revision, selected.Revision, StringComparison.Ordinal))
        {
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.HashMismatch,
                "The requested revision does not match the exact repository metadata currently available.");
        }

        var revision = selected.Revision;
        var requestedRole = request.Role == GgufRole.Unknown ? GgufRole.Chat : request.Role;
        var role = GgufDraftModel.IsDraftQuant(selected.Quant) ? GgufRole.Draft : requestedRole;
        ResolvedGgufProjectorDownload? projector = null;
        if (role != GgufRole.Draft)
        {
            var discoveredProjector = await discovery.FindProjectorAsync(request.RepoId, cancellationToken).ConfigureAwait(false);
            if (discoveredProjector is not null)
            {
                EnsureSafeSource(discoveredProjector.FileName,
                    discoveredProjector.SizeBytes,
                    discoveredProjector.Sha256,
                    discoveredProjector.Revision);
                if (!string.Equals(revision, discoveredProjector.Revision, StringComparison.Ordinal))
                {
                    throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.HashMismatch,
                        "The projector metadata does not identify the selected model revision exactly.");
                }

                projector = new ResolvedGgufProjectorDownload(Path.GetFileName(discoveredProjector.FileName),
                    discoveredProjector.SizeBytes,
                    NormalizeSha256(discoveredProjector.Sha256!));
            }
        }

        return new ResolvedGgufDownload(request.RepoId,
            selected.Quant,
            request.RepoId,
            revision,
            Path.GetFileName(selected.FileName),
            selected.SizeBytes,
            NormalizeSha256(selected.Sha256!),
            role,
            projector);
    }

    public async Task<PreparedGgufDownload> PrepareAsync(ResolvedGgufDownload source,
        GgufDownloadDestination destination,
        IProgress<PullProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateDestination(source, destination);

        var finalWeightPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, destination.RelativeGgufPath);
        var finalSidecarPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, destination.RelativeSidecarPath);
        var finalProjectorPath = destination.ProjectorRelativePath is null
            ? null
            : GgufFilePath.ResolveContainedPath(options.ModelsDirectory, destination.ProjectorRelativePath);
        EnsureNoCollision(finalWeightPath);
        EnsureNoCollision(finalSidecarPath);
        if (finalProjectorPath is not null)
        {
            EnsureNoCollision(finalProjectorPath);
        }

        Directory.CreateDirectory(options.ModelsDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var temporaryWeightPath = finalWeightPath + $".{operationId}.part";
        var temporarySidecarPath = finalSidecarPath + $".{operationId}.part";
        var temporaryProjectorPath = finalProjectorPath is null ? null : finalProjectorPath + $".{operationId}.part";
        try
        {
            var weightResult = await downloadClient.DownloadAsync(source.RepoId,
                source.SourceDisplayName,
                source.ResolvedRevision,
                destination.CanonicalModelName,
                temporaryWeightPath,
                source.SourceSizeBytes,
                source.SourceSha256,
                progress,
                cancellationToken).ConfigureAwait(false);
            var weightHash = await VerifyStagedAsync(temporaryWeightPath,
                source.SourceSizeBytes,
                source.SourceSha256,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(weightResult.ResolvedRevision)
                && !string.Equals(weightResult.ResolvedRevision, source.ResolvedRevision, StringComparison.Ordinal))
            {
                throw IntegrityFailure("The downloaded model resolved to a different revision.");
            }

            string? projectorHash = null;
            if (source.Projector is not null)
            {
                _ = await downloadClient.DownloadAsync(source.RepoId,
                    source.Projector.SourceDisplayName,
                    source.ResolvedRevision,
                    $"{destination.CanonicalModelName} projector",
                    temporaryProjectorPath!,
                    source.Projector.SourceSizeBytes,
                    source.Projector.SourceSha256,
                    progress: null,
                    cancellationToken).ConfigureAwait(false);
                projectorHash = await VerifyStagedAsync(temporaryProjectorPath!,
                    source.Projector.SourceSizeBytes,
                    source.Projector.SourceSha256,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var weightFingerprint = GgufMemberFingerprint.Compute(weightHash, source.SourceSizeBytes);
            var projectorFingerprint = projectorHash is null
                ? null
                : GgufMemberFingerprint.Compute(projectorHash, source.Projector!.SourceSizeBytes);
            var contentMembers = new List<GgufModelContentMember>
            {
                new(destination.RelativeGgufPath,
                    InstalledModelPhysicalMemberRole.Weight,
                    source.SourceSizeBytes,
                    weightHash,
                    [destination.CanonicalModelName])
            };
            if (projectorHash is not null)
            {
                contentMembers.Add(new GgufModelContentMember(destination.ProjectorRelativePath!,
                    InstalledModelPhysicalMemberRole.Projector,
                    source.Projector!.SourceSizeBytes,
                    projectorHash,
                    [destination.CanonicalModelName]));
            }

            var modelFingerprint = GgufModelContentFingerprint.ComputeV1(contentMembers);
            var acquiredAt = timeProvider.GetUtcNow();
            var entry = new GgufModelRegistryEntry
            {
                ModelName = destination.CanonicalModelName,
                RepoId = source.RepoId,
                FileName = Path.GetFileName(finalWeightPath),
                Quant = destination.CanonicalQuant,
                LocalPath = finalWeightPath,
                SizeBytes = source.SourceSizeBytes,
                Sha256 = weightHash,
                SourceRevision = source.ResolvedRevision,
                DownloadedAtUtc = acquiredAt,
                Role = source.Role,
                ProjectorFileName = source.Projector?.SourceDisplayName,
                ProjectorLocalPath = finalProjectorPath,
                ProjectorSizeBytes = source.Projector?.SourceSizeBytes,
                ProjectorSha256 = projectorHash,
                Origin = LocalModelOrigin.HuggingFace,
                SourceDisplayName = source.SourceDisplayName,
                MetadataSchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                ModelContentFingerprint = modelFingerprint
            };
            var registryRevision = GgufRegistryRevision.ComputeV1(entry, options.ModelsDirectory);
            entry = entry with { RegistryRevision = registryRevision };
            var sidecar = new GgufAcquisitionMetadata
            {
                SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                RegistryRevision = registryRevision,
                ModelName = entry.ModelName,
                Origin = LocalModelOrigin.HuggingFace,
                LocalFileName = entry.FileName,
                Quantization = entry.Quant,
                WeightContentSha256 = weightHash,
                WeightSizeBytes = entry.SizeBytes,
                WeightMemberFingerprint = weightFingerprint,
                SourceDisplayName = source.SourceDisplayName,
                AcquiredAtUtc = acquiredAt,
                RegistryRepoId = source.RepoId,
                RegistrySourceRevision = source.ResolvedRevision,
                Role = source.Role,
                ProjectorRelativePath = destination.ProjectorRelativePath,
                ProjectorSourceDisplayName = source.Projector?.SourceDisplayName,
                ProjectorSourceSha256 = source.Projector?.SourceSha256,
                ProjectorSourceSizeBytes = source.Projector?.SourceSizeBytes,
                ProjectorContentSha256 = projectorHash,
                ProjectorContentSizeBytes = source.Projector?.SourceSizeBytes,
                ProjectorMemberFingerprint = projectorFingerprint,
                ModelContentFingerprint = modelFingerprint
            };
            await GgufAcquisitionSidecar.WriteAsync(temporarySidecarPath, sidecar, cancellationToken).ConfigureAwait(false);
            return new PreparedGgufDownload(operationId,
                source,
                destination,
                temporaryWeightPath,
                temporarySidecarPath,
                temporaryProjectorPath,
                entry,
                sidecar,
                weightFingerprint,
                projectorFingerprint,
                modelFingerprint);
        }
        catch (Exception exception)
        {
            var artifacts = new List<(string Path, bool Owned)>
            {
                (temporarySidecarPath, true),
                (temporaryWeightPath, true),
                (temporaryWeightPath + ".part", true)
            };
            if (temporaryProjectorPath is not null)
            {
                artifacts.Add((temporaryProjectorPath, true));
                artifacts.Add((temporaryProjectorPath + ".part", true));
            }

            var cleanupFailure = TryDeleteOwnedArtifacts(artifacts.ToArray());
            if (cleanupFailure is not null)
            {
                throw new GgufAcquisitionCleanupException("Download cleanup requires recovery.",
                    new AggregateException(exception, cleanupFailure));
            }

            throw;
        }
    }

    public async Task<GgufDownloadCommitReceipt> CommitAsync(PreparedGgufDownload preparedDownload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedDownload);
        ValidateDestination(preparedDownload.Source, preparedDownload.Destination);
        var finalWeightPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, preparedDownload.Destination.RelativeGgufPath);
        var finalSidecarPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, preparedDownload.Destination.RelativeSidecarPath);
        var finalProjectorPath = preparedDownload.Destination.ProjectorRelativePath is null
            ? null
            : GgufFilePath.ResolveContainedPath(options.ModelsDirectory, preparedDownload.Destination.ProjectorRelativePath);
        var movedSidecar = false;
        var movedProjector = false;
        var movedWeight = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNoCollision(finalSidecarPath);
            File.Move(preparedDownload.TemporarySidecarPath, finalSidecarPath, overwrite: false);
            movedSidecar = true;
            if (finalProjectorPath is not null)
            {
                EnsureNoCollision(finalProjectorPath);
                File.Move(preparedDownload.TemporaryProjectorPath!, finalProjectorPath, overwrite: false);
                movedProjector = true;
            }
            EnsureNoCollision(finalWeightPath);
            File.Move(preparedDownload.TemporaryGgufPath, finalWeightPath, overwrite: false);
            movedWeight = true;
            await registry.InsertIfAbsentAsync(preparedDownload.RegistryEntry, cancellationToken).ConfigureAwait(false);

            var verified = await GgufAcquisitionSidecar.ReadValidAsync(finalSidecarPath,
                finalWeightPath,
                options.ModelsDirectory,
                cancellationToken).ConfigureAwait(false);
            if (verified is null
                || !string.Equals(verified.RegistryRevision, preparedDownload.RegistryEntry.RegistryRevision, StringComparison.Ordinal)
                || !string.Equals(verified.WeightMemberFingerprint, preparedDownload.WeightMemberFingerprint, StringComparison.Ordinal)
                || !string.Equals(verified.ProjectorMemberFingerprint, preparedDownload.ProjectorMemberFingerprint, StringComparison.Ordinal)
                || !string.Equals(verified.ModelContentFingerprint, preparedDownload.ModelContentFingerprint, StringComparison.Ordinal))
            {
                throw IntegrityFailure("The committed download failed integrity revalidation.");
            }

            return new GgufDownloadCommitReceipt(preparedDownload.RegistryEntry,
                finalWeightPath,
                finalSidecarPath,
                finalProjectorPath,
                preparedDownload.WeightMemberFingerprint,
                preparedDownload.ProjectorMemberFingerprint,
                preparedDownload.ModelContentFingerprint);
        }
        catch (Exception exception)
        {
            if (movedWeight || movedProjector || movedSidecar)
            {
                var receipt = new GgufDownloadCommitReceipt(preparedDownload.RegistryEntry,
                    finalWeightPath,
                    finalSidecarPath,
                    finalProjectorPath,
                    preparedDownload.WeightMemberFingerprint,
                    preparedDownload.ProjectorMemberFingerprint,
                    preparedDownload.ModelContentFingerprint)
                {
                    OwnsFinalGguf = movedWeight,
                    OwnsFinalSidecar = movedSidecar,
                    OwnsFinalProjector = movedProjector
                };
                throw new GgufDownloadCommitException(receipt, "The download could not be committed safely.", exception);
            }

            throw;
        }
    }

    public async Task RollbackCommittedAsync(GgufDownloadCommitReceipt commitReceipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commitReceipt);
        var entries = await registry.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var exactOwner = entries.Any(entry => entry == commitReceipt.RegistryEntry);
        if (exactOwner && !await registry.RemoveExactAsync(commitReceipt.RegistryEntry, cancellationToken).ConfigureAwait(false))
        {
            throw IntegrityFailure("The committed download registry identity changed during rollback.");
        }

        if (!exactOwner && entries.Any(entry => string.Equals(entry.ModelName, commitReceipt.RegistryEntry.ModelName, StringComparison.OrdinalIgnoreCase)
                                               || PathsEqual(entry.LocalPath, commitReceipt.FinalGgufPath)))
        {
            throw IntegrityFailure("The committed download is now owned by a different registry entry.");
        }

        if (!exactOwner && File.Exists(commitReceipt.FinalSidecarPath))
        {
            var metadata = commitReceipt.OwnsFinalGguf
                ? await GgufAcquisitionSidecar.ReadValidAsync(commitReceipt.FinalSidecarPath,
                    commitReceipt.FinalGgufPath,
                    options.ModelsDirectory,
                    cancellationToken).ConfigureAwait(false)
                : await GgufAcquisitionSidecar.ReadShapeValidAsync(commitReceipt.FinalSidecarPath,
                    commitReceipt.FinalGgufPath,
                    options.ModelsDirectory,
                    cancellationToken).ConfigureAwait(false);
            if (metadata is null
                || !string.Equals(metadata.RegistryRevision, commitReceipt.RegistryEntry.RegistryRevision, StringComparison.Ordinal)
                || !string.Equals(metadata.ModelContentFingerprint, commitReceipt.ModelContentFingerprint, StringComparison.Ordinal))
            {
                throw IntegrityFailure("The committed download artifacts no longer match the rollback receipt.");
            }
        }

        DeleteOwnedArtifacts((commitReceipt.FinalGgufPath, commitReceipt.OwnsFinalGguf),
            (commitReceipt.FinalProjectorPath ?? string.Empty,
                commitReceipt.FinalProjectorPath is not null && commitReceipt.OwnsFinalProjector),
            (commitReceipt.FinalSidecarPath, commitReceipt.OwnsFinalSidecar));
    }

    public Task DiscardPreparedAsync(PreparedGgufDownload preparedDownload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedDownload);
        var artifacts = new List<(string Path, bool Owned)>
        {
            (preparedDownload.TemporarySidecarPath, true),
            (preparedDownload.TemporaryGgufPath, true),
            (preparedDownload.TemporaryGgufPath + ".part", true)
        };
        if (preparedDownload.TemporaryProjectorPath is not null)
        {
            artifacts.Add((preparedDownload.TemporaryProjectorPath, true));
            artifacts.Add((preparedDownload.TemporaryProjectorPath + ".part", true));
        }

        DeleteOwnedArtifacts(artifacts.ToArray());
        return Task.CompletedTask;
    }

    private static GgufRepoFile ResolveFile(IReadOnlyList<GgufRepoFile> files, GgufModelRequest request, string defaultQuant)
    {
        if (request.FileName is not null)
        {
            return files.FirstOrDefault(file => string.Equals(file.FileName, request.FileName, StringComparison.OrdinalIgnoreCase))
                   ?? throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.NotFound,
                       "The requested model file was not found in the repository.");
        }

        var targetQuant = request.Quant ?? defaultQuant;
        var selected = files.FirstOrDefault(file => string.Equals(file.Quant, targetQuant, StringComparison.OrdinalIgnoreCase));
        if (selected is null && !GgufQuantParser.IsDynamic(targetQuant))
        {
            selected = files.FirstOrDefault(file => string.Equals(GgufQuantParser.StripDynamicPrefix(file.Quant), targetQuant, StringComparison.OrdinalIgnoreCase));
        }

        return selected ?? throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.NotFound,
            "No GGUF file with the requested quantization was found in the repository.");
    }

    private static void ValidateDestination(ResolvedGgufDownload source, GgufDownloadDestination destination)
    {
        if (!string.Equals(source.CanonicalQuant, destination.CanonicalQuant, StringComparison.Ordinal)
            || !string.Equals(GgufModelName.Format(source.ModelBaseName, source.CanonicalQuant), destination.CanonicalModelName, StringComparison.Ordinal)
            || (source.Projector is null) != (destination.ProjectorRelativePath is null))
        {
            throw new ArgumentException("The resolved source does not match the deterministic download destination.", nameof(destination));
        }
    }

    private static void EnsureSafeSource(string fileName, long sizeBytes, string? sha256, string revision)
    {
        if (!GgufFilePath.IsSafeRelativePath(fileName)
            || sizeBytes <= 0
            || string.IsNullOrWhiteSpace(revision)
            || sha256 is not { Length: 64 }
            || !sha256.All(Uri.IsHexDigit))
        {
            throw IntegrityFailure("The repository did not provide exact downloadable artifact metadata.");
        }
    }

    private static async Task<string> VerifyStagedAsync(string path, long expectedSize, string expectedSha, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var hash = await GgufAcquisitionSidecar.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (info.Length != expectedSize || !string.Equals(hash, NormalizeSha256(expectedSha), StringComparison.Ordinal))
        {
            throw IntegrityFailure("The downloaded artifact did not match its resolved metadata.");
        }

        return hash;
    }

    private static void EnsureNoCollision(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (directory is not null && Directory.Exists(directory)
            && Directory.EnumerateFileSystemEntries(directory).Any(existing => string.Equals(Path.GetFileName(existing), name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new HuggingFaceDownloadException(HuggingFaceDownloadFailure.DestinationConflict,
                "The model download destination already exists.");
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Acquisition metadata requires canonical lowercase SHA-256 values.")]
    private static string NormalizeSha256(string value) => value.ToLowerInvariant();

    private static HuggingFaceDownloadException IntegrityFailure(string message) =>
        new(HuggingFaceDownloadFailure.HashMismatch, message);

    private static Exception? TryDeleteOwnedArtifacts(params (string Path, bool Owned)[] artifacts)
    {
        List<Exception>? failures = null;
        foreach (var artifact in artifacts)
        {
            try
            {
                DeleteOwned(artifact.Path, artifact.Owned);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                (failures ??= []).Add(exception);
            }
        }

        return failures is null ? null : new AggregateException(failures);
    }

    private static void DeleteOwnedArtifacts(params (string Path, bool Owned)[] artifacts)
    {
        var failure = TryDeleteOwnedArtifacts(artifacts);
        if (failure is not null)
        {
            throw new IOException("One or more download-owned artifacts could not be removed.", failure);
        }
    }

    private static void DeleteOwned(string path, bool owned)
    {
        if (!owned)
        {
            return;
        }

        File.Delete(path);
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException("A download-owned artifact could not be removed.");
        }
    }
}
