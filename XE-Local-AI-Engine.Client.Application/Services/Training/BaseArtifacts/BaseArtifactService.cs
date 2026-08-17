namespace XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>
///     Default <see cref="IBaseArtifactService" />. Resolves the operator-selected checkpoint repository, preflights
///     disk, records the artifact, and hands the transfer to <see cref="BaseArtifactDownloadCoordinator" />.
/// </summary>
internal sealed class BaseArtifactService(
    ITrainingBaseArtifactStore store,
    IBaseCheckpointStore checkpointStore,
    BaseArtifactDownloadCoordinator coordinator,
    IFreeSpaceProbe freeSpaceProbe,
    INodeDataDirectory dataDirectory,
    TimeProvider timeProvider) : IBaseArtifactService
{
    /// <summary>
    ///     Headroom demanded on top of the manifest's own reported size. A checkpoint that exactly fills the volume
    ///     leaves nothing for the frozen dataset copy, the run's work directory, or the export — all of which land on
    ///     the same disk immediately afterwards.
    /// </summary>
    internal const long DiskHeadroomBytes = 10L * 1024 * 1024 * 1024;

    private readonly IBaseCheckpointStore _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
    private readonly BaseArtifactDownloadCoordinator _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    private readonly INodeDataDirectory _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    private readonly IFreeSpaceProbe _freeSpaceProbe = freeSpaceProbe ?? throw new ArgumentNullException(nameof(freeSpaceProbe));
    private readonly ITrainingBaseArtifactStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public async Task<BaseArtifactView> StartDownloadAsync(string repoId, string? revision, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);

        BaseCheckpointManifest manifest;
        try
        {
            manifest = await _checkpointStore.ResolveAsync(repoId.Trim(), revision?.Trim(), ct).ConfigureAwait(false);
        }
        catch (BaseCheckpointNotTrainableException exception)
        {
            throw new BaseArtifactRejectedException(exception.Message, exception);
        }

        EnsureDiskSpace(manifest.TotalBytes);

        var record = await _store.StartDownloadAsync(manifest.RepoId, manifest.Revision, ct).ConfigureAwait(false);
        if (record.Status != TrainingBaseArtifactStatus.Downloading)
        {
            // Already Ready: hand back what exists rather than re-downloading tens of gigabytes.
            return ToView(record);
        }

        var license = new BaseArtifactLicenseView(manifest.RepoId, manifest.License, manifest.IsGated, _timeProvider.GetUtcNow());
        _coordinator.Start(record.Id, record.Version, manifest, license);
        return ToView(record);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BaseArtifactView>> ListAsync(CancellationToken ct)
    {
        var records = await _store.ListAsync(ct).ConfigureAwait(false);
        return records.Select(ToView).ToArray();
    }

    /// <inheritdoc />
    public async Task<BaseArtifactView?> GetAsync(Guid artifactId, CancellationToken ct)
    {
        var record = await _store.GetAsync(artifactId, ct).ConfigureAwait(false);
        return record is null ? null : ToView(record);
    }

    /// <inheritdoc />
    public async Task<BaseArtifactLicenseView?> GetLicenseAsync(Guid artifactId, CancellationToken ct)
    {
        var record = await _store.GetAsync(artifactId, ct).ConfigureAwait(false);
        return record is null ? null : BaseArtifactManifest.DeserializeLicense(record.LicenseJson);
    }

    /// <inheritdoc />
    public bool Cancel(Guid artifactId)
    {
        return _coordinator.Cancel(artifactId);
    }

    /// <inheritdoc />
    public async Task<BaseArtifactDeleteOutcome> DeleteAsync(Guid artifactId, CancellationToken ct)
    {
        var record = await _store.GetAsync(artifactId, ct).ConfigureAwait(false);
        if (record is null)
        {
            return BaseArtifactDeleteOutcome.NotFound;
        }

        // Refuse while the transfer is live: deleting the directory under the writer would leave half-written shards
        // that the next attempt would happily resume from. Run references arrive with the run module.
        if (record.Status == TrainingBaseArtifactStatus.Downloading || _coordinator.IsDownloading(artifactId))
        {
            return BaseArtifactDeleteOutcome.Downloading;
        }

        var deleted = await _store.DeleteAsync(artifactId, record.Version, ct).ConfigureAwait(false);
        if (!deleted)
        {
            return BaseArtifactDeleteOutcome.NotFound;
        }

        // The row is the record of truth; a directory that survives is wasted disk, not a phantom artifact, so cleanup
        // failure must not fail the delete the operator already saw succeed.
        TryDeleteDirectory(BaseArtifactManifest.ResolveDirectory(_dataDirectory, artifactId));
        return BaseArtifactDeleteOutcome.Deleted;
    }

    private void EnsureDiskSpace(long requiredBytes)
    {
        var root = _dataDirectory.Root;
        var available = _freeSpaceProbe.GetAvailableFreeBytes(root);
        if (available >= requiredBytes + DiskHeadroomBytes)
        {
            return;
        }

        throw new BaseArtifactRejectedException(
            $"The base checkpoint needs {FormatGigabytes(requiredBytes + DiskHeadroomBytes)} of free space but only {FormatGigabytes(available)} is available.");
    }

    private BaseArtifactView ToView(TrainingBaseArtifactRecord record)
    {
        return new BaseArtifactView(record.Id,
            record.RepoId,
            record.Revision,
            record.Status.ToString(),
            record.TotalBytes,
            BaseArtifactManifest.DeserializeFiles(record.FilesJson),
            BaseArtifactManifest.DeserializeLicense(record.LicenseJson),
            record.ErrorMessage,
            record.Version,
            DateTimeOffset.FromUnixTimeMilliseconds(record.CreatedAtUtc),
            DateTimeOffset.FromUnixTimeMilliseconds(record.UpdatedAtUtc),
            _coordinator.GetProgress(record.Id));
    }

    private static string FormatGigabytes(long bytes)
    {
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1024 * 1024 * 1024):0.#} GB");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort: see the call site.
        }
    }
}
