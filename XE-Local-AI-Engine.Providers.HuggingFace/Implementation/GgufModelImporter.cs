namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

internal sealed class GgufModelImporter(
    GgufModelRegistry registry,
    IFreeSpaceProbe freeSpaceProbe,
    HuggingFaceOptions options,
    TimeProvider timeProvider) : IGgufModelImporter
{
    public async Task<PreparedGgufImport> PrepareAsync(GgufImportSource source,
        GgufImportDestination destination,
        IProgress<GgufImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateDestination(destination);

        await using var openedSource = ValidatedGgufImportSource.Open(source.AbsolutePath, options.ModelsDirectory);
        var inspection = await GgufImportInspector
                               .InspectOpenedAsync(openedSource, GgufImportInspectionMode.PublicImport, cancellationToken)
                               .ConfigureAwait(false);
        if (!IsUsableInspection(inspection, destination.CanonicalQuant))
        {
            var reason = inspection.Rejections.Count > 0
                ? inspection.Rejections[0]
                : GgufImportRejectionCode.UnsupportedQuantization;
            throw new GgufImportException(reason, "The selected file is not a supported causal-chat GGUF.");
        }

        var finalPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, destination.RelativeGgufPath);
        var finalSidecarPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, destination.RelativeSidecarPath);
        if (HasCaseInsensitiveCollision(finalPath) || HasCaseInsensitiveCollision(finalSidecarPath))
        {
            throw new GgufImportException(GgufImportRejectionCode.DestinationConflict, "The import destination already exists.");
        }

        var requiredBytes = checked(inspection.SizeBytes + Math.Max(0, options.DiskMarginBytes));
        var availableBytes = freeSpaceProbe.GetAvailableFreeBytes(options.ModelsDirectory);
        if (availableBytes < requiredBytes)
        {
            throw new InsufficientDiskSpaceException(requiredBytes, availableBytes);
        }

        Directory.CreateDirectory(options.ModelsDirectory);
        var operationId = Guid.NewGuid().ToString("N");
        var temporaryPath = finalPath + $".{operationId}.part";
        var temporarySidecarPath = finalSidecarPath + $".{operationId}.part";
        try
        {
            var (hash, copied) = await CopyAndHashAsync(openedSource, temporaryPath, inspection.SizeBytes, progress, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var copiedInspection = GgufImportInspector.Classify(Path.GetFileName(finalPath),
                copied,
                await GgufStrictHeaderParser.ReadAsync(temporaryPath, cancellationToken).ConfigureAwait(false));
            if (!IsUsableInspection(copiedInspection, destination.CanonicalQuant))
            {
                throw new GgufImportException(GgufImportRejectionCode.InvalidGguf,
                    "The copied file did not pass strict GGUF reinspection.");
            }

            var memberFingerprint = GgufMemberFingerprint.Compute(hash, copied);
            var modelFingerprint = GgufModelContentFingerprint.ComputeV1([
                new GgufModelContentMember(destination.RelativeGgufPath,
                    InstalledModelPhysicalMemberRole.Weight,
                    copied,
                    hash,
                    [destination.CanonicalModelName])
            ]);
            var acquiredAt = timeProvider.GetUtcNow();
            var entry = new GgufModelRegistryEntry
            {
                ModelName = destination.CanonicalModelName,
                RepoId = destination.CanonicalModelName,
                FileName = Path.GetFileName(finalPath),
                Quant = destination.CanonicalQuant,
                LocalPath = finalPath,
                SizeBytes = copied,
                Sha256 = hash,
                SourceRevision = $"sha256:{hash}",
                DownloadedAtUtc = acquiredAt,
                Role = GgufRole.Chat,
                Origin = LocalModelOrigin.Imported,
                SourceDisplayName = inspection.SourceDisplayName,
                MetadataSchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                ModelContentFingerprint = modelFingerprint
            };
            var registryRevision = GgufRegistryRevision.ComputeV1(entry, options.ModelsDirectory);
            entry = entry with
            {
                RegistryRevision = registryRevision
            };
            var sidecar = new GgufAcquisitionMetadata
            {
                SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                RegistryRevision = registryRevision,
                ModelName = entry.ModelName,
                Origin = LocalModelOrigin.Imported,
                LocalFileName = entry.FileName,
                Quantization = entry.Quant,
                WeightContentSha256 = hash,
                WeightSizeBytes = copied,
                WeightMemberFingerprint = memberFingerprint,
                SourceDisplayName = inspection.SourceDisplayName,
                AcquiredAtUtc = acquiredAt,
                RegistryRepoId = entry.RepoId,
                RegistrySourceRevision = entry.SourceRevision,
                Role = entry.Role,
                ModelContentFingerprint = modelFingerprint
            };
            await GgufAcquisitionSidecar.WriteAsync(temporarySidecarPath, sidecar, cancellationToken).ConfigureAwait(false);
            return new PreparedGgufImport(operationId, destination, temporaryPath, temporarySidecarPath, entry, sidecar,
                memberFingerprint, modelFingerprint);
        }
        catch (Exception exception)
        {
            var cleanupFailure = TryDeleteOwnedArtifacts((temporaryPath, true), (temporarySidecarPath, true));
            if (cleanupFailure is not null)
            {
                throw new GgufAcquisitionCleanupException("Import cleanup requires recovery.",
                    new AggregateException(exception, cleanupFailure));
            }

            if (exception is OperationCanceledException or GgufImportException or InsufficientDiskSpaceException)
            {
                throw;
            }

            throw new GgufImportException(GgufImportRejectionCode.InvalidSource,
                "The selected source could not be prepared safely.",
                exception);
        }
    }

    public async Task<GgufImportCommitReceipt> CommitAsync(PreparedGgufImport preparedImport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedImport);
        ValidateDestination(preparedImport.Destination);
        var finalPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, preparedImport.Destination.RelativeGgufPath);
        var finalSidecarPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, preparedImport.Destination.RelativeSidecarPath);
        var movedWeight = false;
        var movedSidecar = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Sidecar first, then the GGUF weight: a crash between the two moves leaves an orphan sidecar (cleanable by
            // the startup reaper) rather than a permanently sidecar-less deterministic-named GGUF that would fail closed.
            EnsureNoCaseInsensitiveCollision(finalSidecarPath);
            File.Move(preparedImport.TemporarySidecarPath, finalSidecarPath, overwrite: false);
            movedSidecar = true;
            EnsureNoCaseInsensitiveCollision(finalPath);
            File.Move(preparedImport.TemporaryGgufPath, finalPath, overwrite: false);
            movedWeight = true;
            await registry.InsertIfAbsentAsync(preparedImport.RegistryEntry, cancellationToken).ConfigureAwait(false);

            var verified = await GgufAcquisitionSidecar.ReadValidAsync(finalSidecarPath, finalPath, options.ModelsDirectory, cancellationToken)
                                                       .ConfigureAwait(false);
            if (verified is null
                || !string.Equals(verified.RegistryRevision, preparedImport.RegistryEntry.RegistryRevision, StringComparison.Ordinal)
                || !string.Equals(verified.ModelContentFingerprint, preparedImport.ModelContentFingerprint, StringComparison.Ordinal))
            {
                throw new IOException("The committed import failed integrity revalidation.");
            }

            return new GgufImportCommitReceipt(preparedImport.RegistryEntry,
                finalPath,
                finalSidecarPath,
                preparedImport.WeightMemberFingerprint,
                preparedImport.ModelContentFingerprint);
        }
        catch (Exception exception)
        {
            if (movedWeight || movedSidecar)
            {
                var receipt = new GgufImportCommitReceipt(preparedImport.RegistryEntry,
                    finalPath,
                    finalSidecarPath,
                    preparedImport.WeightMemberFingerprint,
                    preparedImport.ModelContentFingerprint)
                {
                    OwnsFinalGguf = movedWeight,
                    OwnsFinalSidecar = movedSidecar
                };
                throw new GgufImportCommitException(receipt, "The import could not be committed safely.", exception);
            }

            if (exception is OperationCanceledException or GgufImportException)
            {
                throw;
            }

            throw new GgufImportException(GgufImportRejectionCode.DestinationConflict,
                "The import could not be committed safely.",
                exception);
        }
    }

    public async Task RollbackCommittedAsync(GgufImportCommitReceipt commitReceipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commitReceipt);
        var entries = await registry.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var exactOwner = entries.Any(entry => entry == commitReceipt.RegistryEntry
                                              && string.Equals(entry.RegistryRevision,
                                                  commitReceipt.RegistryEntry.RegistryRevision,
                                                  StringComparison.Ordinal));
        if (exactOwner)
        {
            if (!await registry.RemoveExactAsync(commitReceipt.RegistryEntry, cancellationToken).ConfigureAwait(false))
            {
                throw new GgufImportException(GgufImportRejectionCode.DestinationConflict,
                    "The committed import registry identity changed during rollback.");
            }

            DeleteOwnedArtifacts((commitReceipt.FinalGgufPath, commitReceipt.OwnsFinalGguf),
                (commitReceipt.FinalSidecarPath, commitReceipt.OwnsFinalSidecar));
            return;
        }

        var conflictingOwner = entries.Any(entry => string.Equals(entry.ModelName, commitReceipt.RegistryEntry.ModelName,
                                                        StringComparison.OrdinalIgnoreCase)
                                                    || PathsEqual(entry.LocalPath, commitReceipt.FinalGgufPath));
        if (conflictingOwner)
        {
            throw new GgufImportException(GgufImportRejectionCode.DestinationConflict,
                "The committed import is now owned by a different registry entry.");
        }

        // A previous rollback may have removed the exact registry row and then crashed between artifact deletes.
        // Delete only orphaned artifacts that still prove they belong to this receipt; a same-path replacement is
        // preserved and reported as a conflict.
        if (commitReceipt.OwnsFinalGguf && File.Exists(commitReceipt.FinalGgufPath))
        {
            var info = new FileInfo(commitReceipt.FinalGgufPath);
            var hash = await GgufAcquisitionSidecar.ComputeSha256Async(commitReceipt.FinalGgufPath, cancellationToken).ConfigureAwait(false);
            if (info.Length != commitReceipt.RegistryEntry.SizeBytes
                || !string.Equals(hash, commitReceipt.RegistryEntry.Sha256, StringComparison.Ordinal)
                || !string.Equals(GgufMemberFingerprint.Compute(hash, info.Length), commitReceipt.WeightMemberFingerprint,
                    StringComparison.Ordinal))
            {
                throw new GgufImportException(GgufImportRejectionCode.DestinationConflict,
                    "The committed import artifact identity no longer matches the rollback receipt.");
            }

            DeleteOwnedArtifacts((commitReceipt.FinalGgufPath, true));
        }
        else if (commitReceipt.OwnsFinalGguf && Directory.Exists(commitReceipt.FinalGgufPath))
        {
            DeleteOwnedArtifacts((commitReceipt.FinalGgufPath, true));
        }

        if (commitReceipt.OwnsFinalSidecar && File.Exists(commitReceipt.FinalSidecarPath))
        {
            var metadata = await GgufAcquisitionSidecar.ReadShapeValidAsync(commitReceipt.FinalSidecarPath,
                commitReceipt.FinalGgufPath,
                options.ModelsDirectory,
                cancellationToken).ConfigureAwait(false);
            if (metadata is null
                || !string.Equals(metadata.RegistryRevision, commitReceipt.RegistryEntry.RegistryRevision, StringComparison.Ordinal)
                || !string.Equals(metadata.ModelContentFingerprint, commitReceipt.ModelContentFingerprint, StringComparison.Ordinal))
            {
                throw new GgufImportException(GgufImportRejectionCode.DestinationConflict,
                    "The committed import recovery metadata no longer matches the rollback receipt.");
            }

            DeleteOwnedArtifacts((commitReceipt.FinalSidecarPath, true));
        }
        else if (commitReceipt.OwnsFinalSidecar && Directory.Exists(commitReceipt.FinalSidecarPath))
        {
            DeleteOwnedArtifacts((commitReceipt.FinalSidecarPath, true));
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    public Task DiscardPreparedAsync(PreparedGgufImport preparedImport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedImport);
        DeleteOwnedArtifacts((preparedImport.TemporarySidecarPath, true), (preparedImport.TemporaryGgufPath, true));
        return Task.CompletedTask;
    }

    private static async Task<(string Hash, long Bytes)> CopyAndHashAsync(ValidatedGgufImportSource source,
        string destinationPath,
        long expectedBytes,
        IProgress<GgufImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        source.Rewind();
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.Stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hasher.AppendData(buffer, 0, read);
            total += read;
            progress?.Report(new GgufImportProgress(total, expectedBytes));
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        FlushToDisk(destination);
        if (total != expectedBytes)
        {
            throw new GgufImportException(GgufImportRejectionCode.InvalidSource, "The selected source changed while it was copied.");
        }

        source.VerifyStillCurrent();

        return (Convert.ToHexStringLower(hasher.GetHashAndReset()), total);
    }

    private static void ValidateDestination(GgufImportDestination destination)
    {
        if (destination.Origin != LocalModelOrigin.Imported || destination.ProjectorRelativePath is not null)
        {
            throw new ArgumentException("Local import accepts only imported, weight-only destinations.", nameof(destination));
        }

        if (string.IsNullOrWhiteSpace(destination.CanonicalModelName))
        {
            throw new ArgumentException("The import destination model name is required.", nameof(destination));
        }

        if (string.IsNullOrWhiteSpace(destination.CanonicalQuant))
        {
            throw new ArgumentException("The import destination quantization is required.", nameof(destination));
        }

        if (!GgufQuantDetector.IsCanonical(destination.CanonicalQuant))
        {
            throw new ArgumentException("The import destination quantization is not canonical.", nameof(destination));
        }
    }

    private static bool IsUsableInspection(GgufImportInspection inspection, string selectedQuantization)
    {
        if (inspection.Workload != GgufImportWorkload.CausalChat)
        {
            return false;
        }

        if (inspection.IsAccepted)
        {
            return string.Equals(inspection.DetectedQuantization, selectedQuantization, StringComparison.Ordinal);
        }

        return inspection.Rejections.All(static rejection => rejection == GgufImportRejectionCode.QuantizationRequired)
               && GgufQuantDetector.IsCanonical(selectedQuantization);
    }

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
            throw new IOException("One or more import-owned artifacts could not be removed.", failure);
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
            throw new IOException("An import-owned artifact could not be removed.");
        }
    }

    // FlushAsync drains managed buffers but exposes no flush-to-disk overload. This short synchronous durability
    // boundary runs only after the async flush and before the staged file can be committed by rename.
    private static void FlushToDisk(FileStream stream) =>
        stream.Flush(flushToDisk: true);

    private static void EnsureNoCaseInsensitiveCollision(string finalPath)
    {
        if (HasCaseInsensitiveCollision(finalPath))
        {
            throw new GgufImportException(GgufImportRejectionCode.DestinationConflict, "The import destination already exists.");
        }
    }

    private static bool HasCaseInsensitiveCollision(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return false;
        }

        var fileName = Path.GetFileName(finalPath);
        return Directory.EnumerateFileSystemEntries(directory)
                        .Select(Path.GetFileName)
                        .Any(existing => string.Equals(existing, fileName, StringComparison.OrdinalIgnoreCase));
    }
}
