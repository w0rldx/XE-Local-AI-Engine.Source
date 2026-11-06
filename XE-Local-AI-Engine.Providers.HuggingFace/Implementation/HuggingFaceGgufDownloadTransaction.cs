namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;

internal sealed class HuggingFaceGgufDownloadTransaction : IGgufDownloadTransaction
{
    // Names this pipeline in the compensation failure so the inner exception still says which transaction
    // could not clean up after itself.
    private const string CleanupOwnership = "download";
    private readonly HfDownloadClient _downloadClient;
    private readonly IHuggingFaceGgufDiscovery _discovery;
    private readonly GgufModelRegistry _registry;
    private readonly HuggingFaceOptions _options;
    private readonly TimeProvider _timeProvider;

    public HuggingFaceGgufDownloadTransaction(HfDownloadClient downloadClient,
        IHuggingFaceGgufDiscovery discovery,
        GgufModelRegistry registry,
        HuggingFaceOptions options,
        TimeProvider timeProvider)
    {
        _downloadClient = downloadClient;
        _discovery = discovery;
        _registry = registry;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<ResolvedGgufDownload> ResolveAsync(GgufModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RepoId))
        {
            throw new ArgumentException("The repository id is required.", nameof(request));
        }

        var detail = await _discovery.ListRepoFilesAsync(request.RepoId, cancellationToken).ConfigureAwait(false);
        var selected = ResolveFile(detail.Files, request, _options.DefaultQuant);
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

        // A weights-only request never even asks the discovery for a projector: the repo scan is skipped, so a
        // vision-capable repo resolves to exactly the artifact set a text-only repo does.
        if (role != GgufRole.Draft && request.IncludeProjector)
        {
            var discoveredProjector = await _discovery.FindProjectorAsync(request.RepoId, cancellationToken).ConfigureAwait(false);
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

                projector = new ResolvedGgufProjectorDownload
                {
                    SourceDisplayName = Path.GetFileName(discoveredProjector.FileName),
                    SourceSizeBytes = discoveredProjector.SizeBytes,
                    SourceSha256 = NormalizeSha256(discoveredProjector.Sha256!)
                };
            }
        }

        return new ResolvedGgufDownload
        {
            ModelBaseName = request.RepoId,
            CanonicalQuant = selected.Quant,
            RepoId = request.RepoId,
            ResolvedRevision = revision,
            SourceDisplayName = Path.GetFileName(selected.FileName),
            SourceSizeBytes = selected.SizeBytes,
            SourceSha256 = NormalizeSha256(selected.Sha256!),
            Role = role,
            Projector = projector
        };
    }

    public async Task<PreparedGgufDownload> PrepareAsync(ResolvedGgufDownload source,
        GgufDownloadDestination destination,
        IProgress<PullProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ValidateDestination(source, destination);

        var finalWeightPath = GgufFilePath.ResolveContainedPath(_options.ModelsDirectory, destination.RelativeGgufPath);
        var finalSidecarPath = GgufFilePath.ResolveContainedPath(_options.ModelsDirectory, destination.RelativeSidecarPath);
        var finalProjectorPath = destination.ProjectorRelativePath is null
            ? null
            : GgufFilePath.ResolveContainedPath(_options.ModelsDirectory, destination.ProjectorRelativePath);
        EnsureNoCollision(finalWeightPath);
        EnsureNoCollision(finalSidecarPath);
        if (finalProjectorPath is not null)
        {
            EnsureNoCollision(finalProjectorPath);
        }

        Directory.CreateDirectory(_options.ModelsDirectory);
        var operationId = Guid.NewGuid().ToString("N");

        // One stable partial per final file lets an interrupted download resume on the next attempt; only verified bytes take
        // the per-operation name. The acquisition registry admits one operation per model, so nothing else writes it.
        var paths = new StagingPaths
        {
            FinalWeight = finalWeightPath,
            FinalProjector = finalProjectorPath,
            TemporaryWeight = finalWeightPath + $".{operationId}.part",
            TemporarySidecar = finalSidecarPath + $".{operationId}.part",
            TemporaryProjector = finalProjectorPath is null ? null : finalProjectorPath + $".{operationId}.part",
            WeightPart = PartPathFor(finalWeightPath),
            ProjectorPart = finalProjectorPath is null ? null : PartPathFor(finalProjectorPath),
            Intent = IntentPathFor(finalWeightPath)
        };

        var weightVerified = false;
        var projectorVerified = false;
        // ONE total for the whole set (weight + projector): the intent file, so an interrupted row, and both legs' live progress report against it.
        var setTotalBytes = checked(source.SourceSizeBytes + (source.Projector?.SourceSizeBytes ?? 0));
        var partCount = source.Projector is null ? 1 : 2;
        try
        {
            await File.WriteAllTextAsync(paths.Intent,
                JsonSerializer.Serialize(new InterruptedGgufDownload
                {
                    ModelName = destination.CanonicalModelName,
                    TotalBytes = setTotalBytes
                }),
                cancellationToken).ConfigureAwait(false);
            var weightResult = await _downloadClient.DownloadAsync(source.RepoId,
                source.SourceDisplayName,
                source.ResolvedRevision,
                destination.CanonicalModelName,
                paths.TemporaryWeight,
                paths.WeightPart,
                source.SourceSizeBytes,
                source.SourceSha256,
                progress is null ? null : new SetProgressAdapter(progress, destination.CanonicalModelName, completedInPriorParts: 0, setTotalBytes, partIndex: 1, partCount),
                cancellationToken).ConfigureAwait(false);
            var weightHash = await VerifyStagedAsync(paths.TemporaryWeight,
                source.SourceSizeBytes,
                source.SourceSha256,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(weightResult.ResolvedRevision)
                && !string.Equals(weightResult.ResolvedRevision, source.ResolvedRevision, StringComparison.Ordinal))
            {
                throw IntegrityFailure("The downloaded model resolved to a different revision.");
            }

            weightVerified = true;
            string? projectorHash = null;
            if (source.Projector is not null)
            {
                _ = await _downloadClient.DownloadAsync(source.RepoId,
                    source.Projector.SourceDisplayName,
                    source.ResolvedRevision,
                    $"{destination.CanonicalModelName} projector",
                    paths.TemporaryProjector!,
                    paths.ProjectorPart!,
                    source.Projector.SourceSizeBytes,
                    source.Projector.SourceSha256,
                    progress is null
                        ? null
                        : new SetProgressAdapter(progress, destination.CanonicalModelName, completedInPriorParts: source.SourceSizeBytes, setTotalBytes, partIndex: 2, partCount),
                    cancellationToken).ConfigureAwait(false);
                projectorHash = await VerifyStagedAsync(paths.TemporaryProjector!,
                    source.Projector.SourceSizeBytes,
                    source.Projector.SourceSha256,
                    cancellationToken).ConfigureAwait(false);
                projectorVerified = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var prepared = ComposeArtifact(operationId,
                source,
                destination,
                paths,
                weightHash,
                projectorHash,
                _timeProvider.GetUtcNow());
            await GgufAcquisitionSidecar.WriteAsync(paths.TemporarySidecar, prepared.Sidecar, cancellationToken).ConfigureAwait(false);
            File.Delete(paths.Intent);
            return prepared;
        }
        catch (Exception exception)
        {
            // A cancel is the operator abandoning the download, so its partial and resume cursors go too; any other failure
            // keeps them for the next attempt to resume (a corrupt or superseded partial is already dropped by the client).
            var abandoned = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
            if (!abandoned && weightVerified)
            {
                HandBackVerified(paths.TemporaryWeight, paths.WeightPart, source.SourceSizeBytes, source.ResolvedRevision);
            }

            if (!abandoned && projectorVerified)
            {
                HandBackVerified(paths.TemporaryProjector!, paths.ProjectorPart!, source.Projector!.SourceSizeBytes, source.ResolvedRevision);
            }

            var cleanupFailure = OwnedArtifactCleanup.DeleteAllOrFailure(OwnedArtifactsFor(abandoned, paths));
            if (cleanupFailure is not null)
            {
                throw new GgufAcquisitionCleanupException("Download cleanup requires recovery.",
                    new AggregateException(exception, cleanupFailure));
            }

            throw;
        }
    }

    private PreparedGgufDownload ComposeArtifact(string operationId,
        ResolvedGgufDownload source,
        GgufDownloadDestination destination,
        StagingPaths paths,
        string weightHash,
        string? projectorHash,
        DateTimeOffset acquiredAt)
    {
        var weightFingerprint = GgufMemberFingerprint.Compute(weightHash, source.SourceSizeBytes);
        var projectorFingerprint = projectorHash is null
            ? null
            : GgufMemberFingerprint.Compute(projectorHash, source.Projector!.SourceSizeBytes);
        var contentMembers = new List<GgufModelContentMember>
        {
            new()
            {
                RelativePath = destination.RelativeGgufPath,
                Role = InstalledModelPhysicalMemberRole.Weight,
                SizeBytes = source.SourceSizeBytes,
                Sha256 = weightHash,
                OwningAliases = [destination.CanonicalModelName]
            }
        };
        if (projectorHash is not null)
        {
            contentMembers.Add(new GgufModelContentMember
            {
                RelativePath = destination.ProjectorRelativePath!,
                Role = InstalledModelPhysicalMemberRole.Projector,
                SizeBytes = source.Projector!.SourceSizeBytes,
                Sha256 = projectorHash,
                OwningAliases = [destination.CanonicalModelName]
            });
        }

        var modelFingerprint = GgufModelContentFingerprint.ComputeV1(contentMembers);
        var entry = new GgufModelRegistryEntry
        {
            ModelName = destination.CanonicalModelName,
            RepoId = source.RepoId,
            FileName = Path.GetFileName(paths.FinalWeight),
            Quant = destination.CanonicalQuant,
            LocalPath = paths.FinalWeight,
            SizeBytes = source.SourceSizeBytes,
            Sha256 = weightHash,
            SourceRevision = source.ResolvedRevision,
            DownloadedAtUtc = acquiredAt,
            Role = source.Role,
            ProjectorFileName = source.Projector?.SourceDisplayName,
            ProjectorLocalPath = paths.FinalProjector,
            ProjectorSizeBytes = source.Projector?.SourceSizeBytes,
            ProjectorSha256 = projectorHash,
            Origin = LocalModelOrigin.HuggingFace,
            SourceDisplayName = source.SourceDisplayName,
            MetadataSchemaVersion = GgufAcquisitionMetadata.CurrentSchemaVersion,
            ModelContentFingerprint = modelFingerprint
        };
        var registryRevision = GgufRegistryRevision.ComputeV1(entry, _options.ModelsDirectory);
        entry = entry with
        {
            RegistryRevision = registryRevision
        };
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
        return new PreparedGgufDownload
        {
            OperationId = operationId,
            Source = source,
            Destination = destination,
            TemporaryGgufPath = paths.TemporaryWeight,
            TemporarySidecarPath = paths.TemporarySidecar,
            TemporaryProjectorPath = paths.TemporaryProjector,
            RegistryEntry = entry,
            Sidecar = sidecar,
            WeightMemberFingerprint = weightFingerprint,
            ProjectorMemberFingerprint = projectorFingerprint,
            ModelContentFingerprint = modelFingerprint
        };
    }

    /// <summary>
    ///     What a failed preparation deletes: staged files always, and the intent, stable partials and resume cursors only
    ///     when the operator <paramref name="abandoned" /> the download.
    /// </summary>
    private static OwnedArtifact[] OwnedArtifactsFor(bool abandoned, StagingPaths paths)
    {
        var artifacts = new List<OwnedArtifact>
        {
            new()
            {
                Path = paths.TemporarySidecar,
                Owned = true
            },
            new()
            {
                Path = paths.Intent,
                Owned = abandoned
            },
            new()
            {
                Path = paths.TemporaryWeight,
                Owned = true
            },
            new()
            {
                Path = paths.WeightPart,
                Owned = abandoned
            },
            new()
            {
                Path = paths.WeightPart + HfDownloadClient.RangeSidecarSuffix,
                Owned = abandoned
            }
        };
        if (paths.TemporaryProjector is not null)
        {
            artifacts.Add(new OwnedArtifact
            {
                Path = paths.TemporaryProjector,
                Owned = true
            });
            artifacts.Add(new OwnedArtifact
            {
                Path = paths.ProjectorPart!,
                Owned = abandoned
            });
            artifacts.Add(new OwnedArtifact
            {
                Path = paths.ProjectorPart + HfDownloadClient.RangeSidecarSuffix,
                Owned = abandoned
            });
        }

        return [.. artifacts];
    }

    public async Task<GgufDownloadCommitReceipt> CommitAsync(PreparedGgufDownload preparedDownload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedDownload);
        ValidateDestination(preparedDownload.Source, preparedDownload.Destination);
        var finalWeightPath = GgufFilePath.ResolveContainedPath(_options.ModelsDirectory, preparedDownload.Destination.RelativeGgufPath);
        var finalSidecarPath = GgufFilePath.ResolveContainedPath(_options.ModelsDirectory, preparedDownload.Destination.RelativeSidecarPath);
        var finalProjectorPath = preparedDownload.Destination.ProjectorRelativePath is null
            ? null
            : GgufFilePath.ResolveContainedPath(_options.ModelsDirectory, preparedDownload.Destination.ProjectorRelativePath);
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
            await _registry.InsertIfAbsentAsync(preparedDownload.RegistryEntry, cancellationToken).ConfigureAwait(false);

            var verified = await GgufAcquisitionSidecar.ReadValidAsync(finalSidecarPath,
                finalWeightPath,
                _options.ModelsDirectory,
                cancellationToken).ConfigureAwait(false);
            if (verified is null
                || !string.Equals(verified.RegistryRevision, preparedDownload.RegistryEntry.RegistryRevision, StringComparison.Ordinal)
                || !string.Equals(verified.WeightMemberFingerprint, preparedDownload.WeightMemberFingerprint, StringComparison.Ordinal)
                || !string.Equals(verified.ProjectorMemberFingerprint, preparedDownload.ProjectorMemberFingerprint, StringComparison.Ordinal)
                || !string.Equals(verified.ModelContentFingerprint, preparedDownload.ModelContentFingerprint, StringComparison.Ordinal))
            {
                throw IntegrityFailure("The committed download failed integrity revalidation.");
            }

            return new GgufDownloadCommitReceipt
            {
                RegistryEntry = preparedDownload.RegistryEntry,
                FinalGgufPath = finalWeightPath,
                FinalSidecarPath = finalSidecarPath,
                FinalProjectorPath = finalProjectorPath,
                WeightMemberFingerprint = preparedDownload.WeightMemberFingerprint,
                ProjectorMemberFingerprint = preparedDownload.ProjectorMemberFingerprint,
                ModelContentFingerprint = preparedDownload.ModelContentFingerprint
            };
        }
        catch (Exception exception)
        {
            if (movedWeight || movedProjector || movedSidecar)
            {
                var receipt = new GgufDownloadCommitReceipt
                {
                    RegistryEntry = preparedDownload.RegistryEntry,
                    FinalGgufPath = finalWeightPath,
                    FinalSidecarPath = finalSidecarPath,
                    FinalProjectorPath = finalProjectorPath,
                    WeightMemberFingerprint = preparedDownload.WeightMemberFingerprint,
                    ProjectorMemberFingerprint = preparedDownload.ProjectorMemberFingerprint,
                    ModelContentFingerprint = preparedDownload.ModelContentFingerprint,
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
        var entries = await _registry.ListAllAsync(cancellationToken).ConfigureAwait(false);
        var exactOwner = entries.Any(entry => entry == commitReceipt.RegistryEntry);
        if (exactOwner && !await _registry.RemoveExactAsync(commitReceipt.RegistryEntry, cancellationToken).ConfigureAwait(false))
        {
            throw IntegrityFailure("The committed download registry identity changed during rollback.");
        }

        if (!exactOwner && entries.Any(entry => string.Equals(entry.ModelName, commitReceipt.RegistryEntry.ModelName, StringComparison.OrdinalIgnoreCase)
                                                || PathContainment.PathsEqual(entry.LocalPath, commitReceipt.FinalGgufPath)))
        {
            throw IntegrityFailure("The committed download is now owned by a different registry entry.");
        }

        if (!exactOwner && File.Exists(commitReceipt.FinalSidecarPath))
        {
            var metadata = commitReceipt.OwnsFinalGguf
                ? await GgufAcquisitionSidecar.ReadValidAsync(commitReceipt.FinalSidecarPath,
                    commitReceipt.FinalGgufPath,
                    _options.ModelsDirectory,
                    cancellationToken).ConfigureAwait(false)
                : await GgufAcquisitionSidecar.ReadShapeValidAsync(commitReceipt.FinalSidecarPath,
                    commitReceipt.FinalGgufPath,
                    _options.ModelsDirectory,
                    cancellationToken).ConfigureAwait(false);
            if (metadata is null
                || !string.Equals(metadata.RegistryRevision, commitReceipt.RegistryEntry.RegistryRevision, StringComparison.Ordinal)
                || !string.Equals(metadata.ModelContentFingerprint, commitReceipt.ModelContentFingerprint, StringComparison.Ordinal))
            {
                throw IntegrityFailure("The committed download artifacts no longer match the rollback receipt.");
            }
        }

        OwnedArtifactCleanup.DeleteAll(CleanupOwnership, new OwnedArtifact
            {
                Path = commitReceipt.FinalGgufPath,
                Owned = commitReceipt.OwnsFinalGguf
            },
            new OwnedArtifact
            {
                Path = commitReceipt.FinalProjectorPath ?? string.Empty,
                Owned = commitReceipt.FinalProjectorPath is not null && commitReceipt.OwnsFinalProjector
            },
            new OwnedArtifact
            {
                Path = commitReceipt.FinalSidecarPath,
                Owned = commitReceipt.OwnsFinalSidecar
            });
    }

    public Task DiscardPreparedAsync(PreparedGgufDownload preparedDownload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedDownload);
        var artifacts = new List<OwnedArtifact>
        {
            new()
            {
                Path = preparedDownload.TemporarySidecarPath,
                Owned = true
            },
            new()
            {
                Path = preparedDownload.TemporaryGgufPath,
                Owned = true
            }
        };
        if (preparedDownload.TemporaryProjectorPath is not null)
        {
            artifacts.Add(new OwnedArtifact
            {
                Path = preparedDownload.TemporaryProjectorPath,
                Owned = true
            });
        }

        OwnedArtifactCleanup.DeleteAll(CleanupOwnership, [.. artifacts]);
        return Task.CompletedTask;
    }

    /// <summary>Moves a verified staged file back to its stable partial, recorded as complete, so the next attempt commits it without a download.</summary>
    private static void HandBackVerified(string stagedPath, string partPath, long sizeBytes, string revision)
    {
        try
        {
            File.Move(stagedPath, partPath, overwrite: true);
            HfDownloadClient.RecordCompletePartial(partPath, sizeBytes, revision);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a file that cannot be handed back is deleted with the other staged artifacts and fetched again.
        }
    }

    /// <summary>The stable in-progress name for <paramref name="finalPath" />: <c>&lt;final&gt;.part</c>, cursors beside it.</summary>
    internal static string PartPathFor(string finalPath) =>
        finalPath + HfDownloadClient.PartSuffix;

    // What a resumable partial was downloading for, beside it: the partial's file name does not carry the model name. The
    // ".part" ending keeps it under the startup reaper's rules for partials (stale after a day, gone once installed).
    internal static string IntentPathFor(string finalPath) =>
        finalPath + IntentSuffix;

    internal const string IntentSuffix = ".intent" + HfDownloadClient.PartSuffix;

    /// <inheritdoc />
    public IReadOnlyList<InterruptedGgufDownload> ListInterrupted()
    {
        var interrupted = new List<InterruptedGgufDownload>();
        if (!Directory.Exists(_options.ModelsDirectory))
        {
            return interrupted;
        }

        foreach (var intentPath in Directory.EnumerateFiles(_options.ModelsDirectory, "*.gguf" + IntentSuffix, SearchOption.TopDirectoryOnly))
        {
            try
            {
                var finalPath = intentPath[..^IntentSuffix.Length];
                if (!HasInterruptedArtifact(finalPath))
                {
                    // Nothing left to resume: the intent outlived its partial.
                    File.Delete(intentPath);
                    continue;
                }

                if (JsonSerializer.Deserialize<InterruptedGgufDownload>(File.ReadAllText(intentPath)) is { ModelName.Length: > 0 } download)
                {
                    interrupted.Add(download);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Best-effort: an unreadable intent only loses the row; a retried download still resumes the partial.
            }
        }

        return interrupted;
    }

    /// <summary>
    ///     Whether a download of <paramref name="finalPath" /> left its stable partial or its verified weights staged as
    ///     <c>&lt;final&gt;.&lt;operation&gt;.part</c>, all a crash in the projector phase leaves.
    /// </summary>
    private static bool HasInterruptedArtifact(string finalPath)
    {
        if (File.Exists(PartPathFor(finalPath)))
        {
            return true;
        }

        var prefix = Path.GetFileName(finalPath) + ".";
        return Directory.EnumerateFiles(Path.GetDirectoryName(finalPath)!, prefix + "*" + HfDownloadClient.PartSuffix, SearchOption.TopDirectoryOnly)
                        .Select(static path => Path.GetFileName(path))
                        .Any(name => Guid.TryParseExact(name[prefix.Length..^HfDownloadClient.PartSuffix.Length], "N", out _));
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
        var hash = await FileSha256.ComputeAsync(path, cancellationToken).ConfigureAwait(false);
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

    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Acquisition metadata requires canonical lowercase SHA-256 values.")]
    private static string NormalizeSha256(string value) =>
        value.ToLowerInvariant();

    private static HuggingFaceDownloadException IntegrityFailure(string message) =>
        new(HuggingFaceDownloadFailure.HashMismatch, message);

    /// <summary>Every path one preparation works with; the projector members are null for a weights-only download.</summary>
    private sealed class StagingPaths
    {
        public required string FinalWeight { get; init; }

        public required string? FinalProjector { get; init; }

        public required string TemporaryWeight { get; init; }

        public required string TemporarySidecar { get; init; }

        public required string? TemporaryProjector { get; init; }

        public required string WeightPart { get; init; }

        public required string? ProjectorPart { get; init; }

        public required string Intent { get; init; }
    }
}
