namespace XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>
///     Owns the in-flight base-checkpoint downloads: one cancellation source and one live progress snapshot per
///     artifact, and the detached task that performs the transfer.
/// </summary>
/// <remarks>
///     A singleton, because a download outlives the request that started it. It opens its own DI scope for the store —
///     the request scope (and its <c>DbContext</c>) is disposed the moment the endpoint responds, so reusing it would
///     fault the first write after the response. Progress is in-memory only and deliberately not persisted: a byte
///     counter written per chunk would be thousands of encrypted row updates for information nobody needs after the
///     transfer ends.
/// </remarks>
internal sealed class BaseArtifactDownloadCoordinator(
    IServiceScopeFactory scopeFactory,
    IBaseCheckpointStore checkpointStore,
    INodeDataDirectory dataDirectory,
    ILogger<BaseArtifactDownloadCoordinator> logger) : IDisposable
{
    private readonly IBaseCheckpointStore _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
    private readonly INodeDataDirectory _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
    private readonly ConcurrentDictionary<Guid, InFlightDownload> _inFlight = new();
    private readonly ILogger<BaseArtifactDownloadCoordinator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public bool IsDownloading(Guid artifactId)
    {
        return _inFlight.ContainsKey(artifactId);
    }

    public BaseArtifactProgressView? GetProgress(Guid artifactId)
    {
        return _inFlight.TryGetValue(artifactId, out var download) ? download.Progress : null;
    }

    public bool Cancel(Guid artifactId)
    {
        if (!_inFlight.TryGetValue(artifactId, out var download))
        {
            return false;
        }

        try
        {
            download.Cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The download completed between the lookup and the cancel.
            return false;
        }
    }

    /// <summary>
    ///     Starts the transfer for an artifact already recorded as <c>Downloading</c>. A second start for the same
    ///     artifact is a no-op so a double-submit cannot run two writers over one directory.
    /// </summary>
    public void Start(Guid artifactId, long expectedVersion, BaseCheckpointManifest manifest, BaseArtifactLicenseView license)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(license);

        var download = new InFlightDownload();
        if (!_inFlight.TryAdd(artifactId, download))
        {
            download.Dispose();
            return;
        }

        // The task looks its own entry up rather than closing over it: the entry is disposed by the task itself, and
        // capturing a disposable in a detached lambda is exactly the lifetime the analyzer cannot verify.
        download.Task = Task.Run(() => RunAsync(artifactId, expectedVersion, manifest, license), CancellationToken.None);
    }

    /// <summary>Waits for an in-flight download to settle, for deterministic teardown and tests.</summary>
    public async Task DrainAsync(Guid artifactId, CancellationToken ct)
    {
        if (_inFlight.TryGetValue(artifactId, out var download) && download.Task is not null)
        {
            await download.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        foreach (var download in _inFlight.Values)
        {
            download.Dispose();
        }

        _inFlight.Clear();
    }

    private async Task RunAsync(Guid artifactId,
        long expectedVersion,
        BaseCheckpointManifest manifest,
        BaseArtifactLicenseView license)
    {
        if (!_inFlight.TryGetValue(artifactId, out var download))
        {
            return;
        }

        var directory = BaseArtifactManifest.ResolveDirectory(_dataDirectory, artifactId);
        try
        {
            var progress = new Progress<PullProgress>(value => download.Progress = new BaseArtifactProgressView(
                value.CompletedBytes ?? 0,
                value.TotalBytes,
                value.PartIndex ?? 0,
                value.PartCount ?? manifest.Files.Count));

            var completed = await _checkpointStore
                                  .DownloadAsync(manifest, directory, progress, download.Cancellation.Token)
                                  .ConfigureAwait(false);

            await UpdateStoreAsync(async (store, ct) => await store.MarkReadyAsync(artifactId,
                                                                       expectedVersion,
                                                                       BaseArtifactManifest.SerializeFiles(completed.Files),
                                                                       completed.TotalBytes,
                                                                       BaseArtifactManifest.SerializeLicense(license),
                                                                       ct)
                                                                   .ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Partially transferred files stay on disk on purpose: the .part staging is what makes a retry resume
            // rather than restart, and a cancelled 30 GB checkpoint should not have to be refetched from byte zero.
            await FailAsync(artifactId, expectedVersion, "The download was cancelled.").ConfigureAwait(false);
        }
        catch (BaseCheckpointNotTrainableException exception)
        {
            await FailAsync(artifactId, expectedVersion, exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The base checkpoint download for {ArtifactId} failed.", artifactId);
            await FailAsync(artifactId,
                    expectedVersion,
                    "The base checkpoint download failed. Check the network connection and try again.")
                .ConfigureAwait(false);
        }
        finally
        {
            _ = _inFlight.TryRemove(artifactId, out _);
            download.Dispose();
        }
    }

    private async Task FailAsync(Guid artifactId, long expectedVersion, string message)
    {
        await UpdateStoreAsync(async (store, ct) =>
                await store.MarkFailedAsync(artifactId, expectedVersion, message, ct).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    private async Task UpdateStoreAsync(Func<ITrainingBaseArtifactStore, CancellationToken, Task> update)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<ITrainingBaseArtifactStore>();
            await update(store, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Losing the terminal write leaves the row Downloading, which the startup recovery pass terminalizes. Never
            // let it escape: this runs on a detached task with nobody to observe the exception.
            _logger.LogError(exception, "Recording the terminal state of a base checkpoint download failed.");
        }
    }

    private sealed class InFlightDownload : IDisposable
    {
        private int _disposed;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Task { get; set; }

        public BaseArtifactProgressView? Progress { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
            {
                return;
            }

            Cancellation.Dispose();
        }
    }
}
