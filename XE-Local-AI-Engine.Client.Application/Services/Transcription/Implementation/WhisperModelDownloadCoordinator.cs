namespace XE_Local_AI_Engine.Client.Services.Transcription.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp;

/// <summary>
///     Default <see cref="IWhisperModelDownloadCoordinator" />. Runs each download on a detached task, records the
///     latest sanitized progress in an in-memory registry keyed by catalogue id, and — the point of the type — always
///     lands the download in an observable terminal phase the operator UI can poll.
/// </summary>
/// <remarks>
///     <para>
///         <b>Singleton.</b> The registry has to outlive the request that started the download.
///     </para>
///     <para>
///         <b>The VAD file is fetched first.</b> Voice-activity detection is on for every launch, so a weight without
///         its VAD model is an installation that cannot actually run. Fetching it as part one of two means a node that
///         downloads any model ends up able to serve it, and the part counters tell the operator which of the two is
///         moving instead of leaving a restarting progress bar to look like a fault.
///     </para>
///     <para>
///         <b>Honest limit:</b> the registry is RAM-only, so a node restart drops in-flight state. The partial file is
///         left on disk and the next attempt resumes from it.
///     </para>
/// </remarks>
public sealed class WhisperModelDownloadCoordinator : IWhisperModelDownloadCoordinator
{
    private const int TotalParts = 2;

    // In-flight downloads, each owning the token source that cancels it. The presence of an entry is what makes a
    // double submit rejoin instead of starting a second transfer.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<WhisperModelDownloadCoordinator> _logger;
    private readonly WhisperModelPathResolver _pathResolver;
    private readonly ConcurrentDictionary<string, WhisperModelDownloadStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly IWhisperWeightFileStore _weightStore;

    public WhisperModelDownloadCoordinator(IWhisperWeightFileStore weightStore,
        WhisperModelPathResolver pathResolver,
        ILogger<WhisperModelDownloadCoordinator> logger)
    {
        _weightStore = weightStore ?? throw new ArgumentNullException(nameof(weightStore));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public WhisperModelDownloadTicket Start(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        // Rejected at the boundary, before anything is registered or any byte is fetched.
        var entry = WhisperModelCatalog.Find(modelId)
                    ?? throw new ArgumentException("The requested transcription model is not in the catalogue.", nameof(modelId));

        var cts = new CancellationTokenSource();
        if (!_inFlight.TryAdd(entry.Id, cts))
        {
            cts.Dispose();
            return new WhisperModelDownloadTicket(entry.Id, AlreadyInFlight: true);
        }

        // Publish Running before the transfer starts, so a poll landing between the accept and the first byte callback
        // sees the download rather than an empty registry.
        _status[entry.Id] = new WhisperModelDownloadStatus(entry.Id, WhisperModelDownloadPhase.Running, CompletedBytes: null, TotalBytes: null, SanitizedError: null);

        // The detached run gets the TOKEN, not the source: the source stays owned by the _inFlight entry, which keeps a
        // disposable out of an unawaited task's arguments.
        _ = RunDownloadAsync(entry, cts.Token);
        return new WhisperModelDownloadTicket(entry.Id, AlreadyInFlight: false);
    }

    /// <inheritdoc />
    public bool Cancel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (!_inFlight.TryGetValue(modelId, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The run completed and disposed its source between the lookup and the cancel — nothing to stop.
            return false;
        }
    }

    /// <inheritdoc />
    public WhisperModelDownloadStatus? GetStatus(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        return _status.TryGetValue(modelId, out var status) ? status : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<WhisperModelDownloadStatus> ListStatuses() =>
        _status.Values.ToList();

    // Detached, self-contained run. Every exit path writes a terminal status; the task itself never faults, because an
    // unobserved background fault would take the reason with it.
    private async Task RunDownloadAsync(WhisperModelEntry entry, CancellationToken ct)
    {
        try
        {
            // Part 1 of 2: the VAD model. Every launch runs with voice-activity detection on, so a weight without it
            // is an installation that cannot serve a request.
            await _weightStore.EnsureFileAsync(new WhisperWeightFileRequest
                {
                    RepoId = WhisperModelCatalog.VadRepoId,
                    FileName = WhisperModelCatalog.VadFileName,
                    DestinationPath = _pathResolver.VadFilePath,
                    ExpectedSizeBytes = WhisperModelCatalog.VadSizeBytes,
                    ExpectedSha256 = WhisperModelCatalog.VadSha256,
                    ProgressLabel = entry.Id
                },
                ProgressFor(entry.Id, partIndex: 1),
                ct);

            // Part 2 of 2: the weights themselves.
            await _weightStore.EnsureFileAsync(new WhisperWeightFileRequest
                {
                    RepoId = entry.RepoId,
                    FileName = entry.FileName,
                    DestinationPath = _pathResolver.FilePathFor(entry),
                    ExpectedSizeBytes = entry.SizeBytes,
                    ExpectedSha256 = entry.Sha256,
                    ProgressLabel = entry.Id
                },
                ProgressFor(entry.Id, partIndex: 2),
                ct);

            var last = _status.TryGetValue(entry.Id, out var snapshot) ? snapshot : null;
            _status[entry.Id] = new WhisperModelDownloadStatus(entry.Id,
                WhisperModelDownloadPhase.Completed,
                last?.CompletedBytes ?? entry.SizeBytes,
                last?.TotalBytes ?? entry.SizeBytes,
                SanitizedError: null);
            _logger.LogInformation("Transcription model download completed for {ModelId}.", entry.Id);
        }
        catch (OperationCanceledException)
        {
            SetTerminal(entry.Id, WhisperModelDownloadPhase.Cancelled, sanitizedError: null);
            _logger.LogInformation("Transcription model download cancelled for {ModelId}.", entry.Id);
        }
        catch (HuggingFaceDownloadException exception)
        {
            // The message is contractually sanitized — no token, no Bearer value, no path — so it is safe to surface,
            // and it is the one that actually tells the operator what went wrong.
            SetTerminal(entry.Id, WhisperModelDownloadPhase.Failed, exception.Message);
            _logger.LogWarning("Transcription model download failed for {ModelId} ({Reason}).", entry.Id, exception.Reason);
        }
        catch (InsufficientDiskSpaceException exception)
        {
            SetTerminal(entry.Id, WhisperModelDownloadPhase.Failed, exception.Message);
            _logger.LogWarning("Transcription model download failed for {ModelId}: insufficient disk space.", entry.Id);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            // Never surface a raw transport or argument message: it can carry a URL or a path.
            SetTerminal(entry.Id, WhisperModelDownloadPhase.Failed, "Download failed.");
            _logger.LogWarning(exception, "Transcription model download failed for {ModelId}.", entry.Id);
        }
        finally
        {
            // Remove before disposing, so a concurrent Cancel finds either a live source or nothing — never a disposed
            // one it is about to call Cancel on.
            if (_inFlight.TryRemove(entry.Id, out var source))
            {
                source.Dispose();
            }
        }
    }

    private IProgress<PullProgress> ProgressFor(string modelId, int partIndex) =>
        new Progress<PullProgress>(update => ReportRunningProgress(modelId, update, partIndex));

    // Records byte progress WITHOUT ever resurrecting a finished download. Progress<T> marshals through the captured
    // context, so a tick queued just before completion can be delivered after the terminal write; publishing it
    // unguarded would flip a finished download back to Running and hang the UI on something that is over.
    private void ReportRunningProgress(string modelId, PullProgress update, int partIndex)
    {
        _ = _status.AddOrUpdate(modelId,
            key => new WhisperModelDownloadStatus(key, WhisperModelDownloadPhase.Running, update.CompletedBytes, update.TotalBytes, SanitizedError: null)
            {
                PartIndex = partIndex,
                PartCount = TotalParts
            },
            (_, existing) => existing.Phase != WhisperModelDownloadPhase.Running
                ? existing
                : existing with
                {
                    CompletedBytes = update.CompletedBytes,
                    TotalBytes = update.TotalBytes,
                    PartIndex = partIndex,
                    PartCount = TotalParts
                });
    }

    private void SetTerminal(string modelId, WhisperModelDownloadPhase phase, string? sanitizedError)
    {
        _status[modelId] = new WhisperModelDownloadStatus(modelId, phase, CompletedBytes: null, TotalBytes: null, sanitizedError);
    }
}
