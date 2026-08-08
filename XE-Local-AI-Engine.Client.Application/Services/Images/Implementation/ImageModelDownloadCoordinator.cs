namespace XE_Local_AI_Engine.Client.Services.Images.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Default <see cref="IImageModelDownloadCoordinator" />. Runs each file-set download on a detached task, records the
///     latest sanitized progress in an in-memory registry keyed by model name, and — the point of the type — always lands
///     the download in an observable terminal phase (<c>Completed</c>/<c>Cancelled</c>/<c>Failed</c>) that the operator
///     UI polls, instead of swallowing the failure into a log line.
///     <para>
///         <b>Singleton.</b> The registry must outlive the request that started the download. It composes the singleton
///         <see cref="IImageModelStore" />.
///     </para>
///     <para>
///         <b>Honest limits.</b> The registry is RAM-only, so a node restart drops in-flight state (the partial
///         <c>.part</c> file resumes on the next start). Byte progress is set-relative — the store offsets each part by
///         the bytes already finished — but the set <i>total</i> is only known when every part declared a size, so a
///         manually-entered file-set reports advancing bytes against a null total rather than a fabricated percentage.
///     </para>
/// </summary>
public sealed class ImageModelDownloadCoordinator : IImageModelDownloadCoordinator
{
    // In-flight downloads, each owning the token source that cancels it. Single-flight per model name: the presence of
    // an entry is what makes a double-submit rejoin instead of starting a second transfer.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ImageModelDownloadCoordinator> _logger;
    private readonly IImageModelStore _modelStore;
    private readonly ConcurrentDictionary<string, ImageModelDownloadStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    public ImageModelDownloadCoordinator(IImageModelStore modelStore, ILogger<ImageModelDownloadCoordinator> logger)
    {
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ImageModelDownloadTicket Start(ImageModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var modelName = request.ModelName;
        var cts = new CancellationTokenSource();
        if (!_inFlight.TryAdd(modelName, cts))
        {
            cts.Dispose();
            return new ImageModelDownloadTicket(modelName, AlreadyInFlight: true);
        }

        // Publish Running before the transfer starts so a poll landing between accept and the first byte callback sees
        // the download rather than an empty registry.
        _status[modelName] = new ImageModelDownloadStatus(modelName, ImageModelDownloadPhase.Running, CompletedBytes: null, TotalBytes: null, SanitizedError: null);

        // Hand the detached run the TOKEN, not the source. The source stays owned by the _inFlight entry (Cancel reads
        // it there, the run disposes it from there), which keeps a disposable out of an unawaited task's arguments —
        // the shape CA2025 rejects, because in the general case the caller's `using` would dispose it mid-flight.
        _ = RunDownloadAsync(modelName, request, cts.Token);
        return new ImageModelDownloadTicket(modelName, AlreadyInFlight: false);
    }

    /// <inheritdoc />
    public bool Cancel(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        if (!_inFlight.TryGetValue(modelName, out var cts))
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
    public ImageModelDownloadStatus? GetStatus(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _status.TryGetValue(modelName, out var status) ? status : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<ImageModelDownloadStatus> ListStatuses()
    {
        return _status.Values.ToList();
    }

    // Detached, self-contained download run. Every exit path writes a terminal status; the task itself never faults
    // (an unobserved background fault would take the reason with it).
    private async Task RunDownloadAsync(string modelName, ImageModelRequest request, CancellationToken ct)
    {
        var progress = new Progress<PullProgress>(update => ReportRunningProgress(modelName, update));

        try
        {
            _ = await _modelStore.EnsureModelAsync(request, progress, ct).ConfigureAwait(false);

            var last = _status.TryGetValue(modelName, out var snapshot) ? snapshot : null;
            _status[modelName] = new ImageModelDownloadStatus(modelName,
                ImageModelDownloadPhase.Completed,
                last?.CompletedBytes ?? last?.TotalBytes,
                last?.TotalBytes,
                SanitizedError: null);
            _logger.LogInformation("Image model download completed for {ModelName}.", modelName);
        }
        catch (OperationCanceledException)
        {
            SetTerminal(modelName, ImageModelDownloadPhase.Cancelled, sanitizedError: null);
            _logger.LogInformation("Image model download cancelled for {ModelName}.", modelName);
        }
        catch (HuggingFaceDownloadException exception)
        {
            // The message is contractually sanitized (no token / Bearer / path) — safe to surface to the operator, and
            // it is the one that actually tells them a mistyped weight file was not found.
            SetTerminal(modelName, ImageModelDownloadPhase.Failed, exception.Message);
            _logger.LogWarning("Image model download failed for {ModelName} ({Reason}).", modelName, exception.Reason);
        }
        catch (InsufficientDiskSpaceException exception)
        {
            SetTerminal(modelName, ImageModelDownloadPhase.Failed, exception.Message);
            _logger.LogWarning("Image model download failed for {ModelName}: insufficient disk space.", modelName);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            // Never surface a raw transport/argument message (it can carry a URL or path): collapse to a generic reason.
            SetTerminal(modelName, ImageModelDownloadPhase.Failed, "Download failed.");
            _logger.LogWarning(exception, "Image model download failed for {ModelName}.", modelName);
        }
        finally
        {
            // Remove before disposing so a concurrent Cancel() either finds a live source or finds nothing — never a
            // disposed one it is about to call Cancel on.
            if (_inFlight.TryRemove(modelName, out var source))
            {
                source.Dispose();
            }
        }
    }

    // Records byte progress WITHOUT ever resurrecting a finished download. Progress<T> marshals its callback through the
    // captured context, so a tick queued just before completion can be delivered after the terminal write — publishing
    // it unguarded would flip a Completed/Failed download back to Running and hang the UI on a download that is over.
    private void ReportRunningProgress(string modelName, PullProgress update)
    {
        _ = _status.AddOrUpdate(modelName,
            key => new ImageModelDownloadStatus(key, ImageModelDownloadPhase.Running, update.CompletedBytes, update.TotalBytes, SanitizedError: null)
            {
                PartIndex = update.PartIndex,
                PartCount = update.PartCount
            },
            (_, existing) => existing.Phase != ImageModelDownloadPhase.Running
                ? existing
                : existing with
                {
                    CompletedBytes = update.CompletedBytes,
                    TotalBytes = update.TotalBytes,
                    PartIndex = update.PartIndex,
                    PartCount = update.PartCount
                });
    }

    private void SetTerminal(string modelName, ImageModelDownloadPhase phase, string? sanitizedError)
    {
        _status[modelName] = new ImageModelDownloadStatus(modelName, phase, CompletedBytes: null, TotalBytes: null, sanitizedError);
    }
}
