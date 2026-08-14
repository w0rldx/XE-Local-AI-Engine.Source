namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

internal sealed class GgufModelImporter(
    IGgufImportInspector inspector,
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

        var inspection = await inspector.InspectAsync(source, cancellationToken).ConfigureAwait(false);
        if (!IsUsableInspection(inspection, destination.CanonicalQuant))
        {
            throw new GgufImportException(inspection.Rejections[0], "The selected file is not a supported causal-chat GGUF.");
        }

        if (!string.Equals(inspection.DetectedQuantization, destination.CanonicalQuant, StringComparison.Ordinal))
        {
            throw new GgufImportException(GgufImportRejectionCode.UnsupportedQuantization,
                "The selected quantization does not match the import destination.");
        }

        var finalPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, destination.RelativeGgufPath);
        var finalSidecarPath = GgufFilePath.ResolveContainedPath(options.ModelsDirectory, destination.RelativeSidecarPath);
        if (File.Exists(finalPath) || File.Exists(finalSidecarPath))
        {
            throw new IOException("The import destination already exists.");
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
            var (hash, copied) = await CopyAndHashAsync(source.AbsolutePath, temporaryPath, inspection.SizeBytes, progress, cancellationToken)
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
            entry = entry with { RegistryRevision = GgufRegistryRevision.ComputeV1(entry) };
            var sidecar = new GgufAcquisitionMetadata
            {
                SchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
                RegistryRevision = entry.RegistryRevision,
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
        catch
        {
            TryDelete(temporaryPath);
            TryDelete(temporarySidecarPath);
            throw;
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
            File.Move(preparedImport.TemporaryGgufPath, finalPath, overwrite: false);
            movedWeight = true;
            File.Move(preparedImport.TemporarySidecarPath, finalSidecarPath, overwrite: false);
            movedSidecar = true;
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
        catch
        {
            if (movedWeight)
            {
                await registry.RemoveExactAsync(preparedImport.RegistryEntry, CancellationToken.None).ConfigureAwait(false);
            }

            if (movedSidecar) TryDelete(finalSidecarPath);
            if (movedWeight) TryDelete(finalPath);
            throw;
        }
    }

    public async Task RollbackCommittedAsync(GgufImportCommitReceipt commitReceipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commitReceipt);
        if (await registry.RemoveExactAsync(commitReceipt.RegistryEntry, cancellationToken).ConfigureAwait(false))
        {
            TryDelete(commitReceipt.FinalSidecarPath);
            TryDelete(commitReceipt.FinalGgufPath);
        }
    }

    public Task DiscardPreparedAsync(PreparedGgufImport preparedImport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedImport);
        TryDelete(preparedImport.TemporarySidecarPath);
        TryDelete(preparedImport.TemporaryGgufPath);
        return Task.CompletedTask;
    }

    private static async Task<(string Hash, long Bytes)> CopyAndHashAsync(string sourcePath,
        string destinationPath,
        long expectedBytes,
        IProgress<GgufImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var before = new FileInfo(sourcePath);
        if (before.LinkTarget is not null || before.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new GgufImportException(GgufImportRejectionCode.InvalidSource, "The selected source is not a regular file.");
        }

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hasher.AppendData(buffer, 0, read);
            total += read;
            progress?.Report(new GgufImportProgress(total, expectedBytes));
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
        var after = new FileInfo(sourcePath);
        if (total != expectedBytes || before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc
            || after.LinkTarget is not null || after.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new GgufImportException(GgufImportRejectionCode.InvalidSource, "The selected source changed while it was copied.");
        }

        return (Convert.ToHexStringLower(hasher.GetHashAndReset()), total);
    }

    private static void ValidateDestination(GgufImportDestination destination)
    {
        if (destination.Origin != LocalModelOrigin.Imported || destination.ProjectorRelativePath is not null)
        {
            throw new ArgumentException("Local import accepts only imported, weight-only destinations.", nameof(destination));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destination.CanonicalModelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination.CanonicalQuant);
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
               && string.Equals(GgufQuantParser.TryParse($"model-{selectedQuantization}.gguf"), selectedQuantization,
                   StringComparison.Ordinal);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
    }
}
