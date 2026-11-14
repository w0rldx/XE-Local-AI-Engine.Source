namespace XE_Local_AI_Engine.Client.Services.Images.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Default <see cref="IImageModelDownloadCoordinator" />: each file-set download runs on a detached task, its
///     sanitized progress goes into an in-memory registry keyed by model name, and it always lands in an observable
///     terminal phase, which the list serves and the images hub pushes.
/// </summary>
/// <remarks>
///     Singleton, because the registry must outlive the request that started the download; it composes the singleton
///     <see cref="IImageModelStore" />. Honest limits: the registry is RAM-only, so a node restart drops in-flight
///     state and the partial <c>.part</c> file resumes on the next start. Byte progress is set-relative — the store
///     offsets each part by the bytes already finished — but the set TOTAL is known only when every part declared a
///     size, so a manually-entered file-set reports advancing bytes against a null total, never a fabricated figure.
/// </remarks>
public sealed class ImageModelDownloadCoordinator : IImageModelDownloadCoordinator
{
    // At most one Running progress push per model and interval; the start and terminal pushes bypass it.
    private static readonly TimeSpan ProgressPushInterval = TimeSpan.FromSeconds(1);

    private readonly IImageJobEventPublisher _eventPublisher;

    // In-flight downloads, each owning the token source that cancels it. Single-flight per model name: the presence of
    // an entry is what makes a double-submit rejoin instead of starting a second transfer.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _lastProgressPushTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ImageModelDownloadCoordinator> _logger;
    private readonly IImageModelStore _modelStore;
    private readonly ConcurrentDictionary<string, ImageModelDownloadStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    // Every status write takes this gate and stamps the next sequence, so the registry and the stamps share one order.
    // The pushes stay fire-and-forget and may land out of order; the client drops one older than what it already applied.
    private readonly Lock _statusGate = new();
    private readonly TimeProvider _timeProvider;
    private long _sequence;

    public ImageModelDownloadCoordinator(IImageModelStore modelStore,
        IImageJobEventPublisher eventPublisher,
        ILogger<ImageModelDownloadCoordinator> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(modelStore);
        _modelStore = modelStore;
        ArgumentNullException.ThrowIfNull(eventPublisher);
        _eventPublisher = eventPublisher;
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
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
            return new ImageModelDownloadTicket
            {
                ModelName = modelName,
                AlreadyInFlight = true
            };
        }

        // Publish Running before the transfer starts so a poll landing between accept and the first byte callback sees
        // the download rather than an empty registry.
        SetStatus(new ImageModelDownloadStatus
        {
            ModelName = modelName,
            Phase = ImageModelDownloadPhase.Running,
            CompletedBytes = null,
            TotalBytes = null,
            SanitizedError = null
        });

        // Hand the detached run the TOKEN, not the source. The source stays owned by the _inFlight entry (Cancel reads it there, the run disposes it from there),
        // which keeps a disposable out of an unawaited task's arguments — the shape CA2025 rejects, because the caller's `using` would otherwise dispose it mid-flight.
        _ = RunDownloadAsync(modelName, request, cts.Token);
        return new ImageModelDownloadTicket
        {
            ModelName = modelName,
            AlreadyInFlight = false
        };
    }

    /// <inheritdoc />
    public bool Cancel(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // The run publishes its terminal phase before its finally removes the _inFlight entry, so the entry alone does not
        // mean the download is still going: a cancel landing in that window must not claim to stop a finished download.
        if (!_inFlight.TryGetValue(modelName, out var cts)
            || (_status.TryGetValue(modelName, out var status) && status.Phase != ImageModelDownloadPhase.Running))
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
            _ = await _modelStore.EnsureModelAsync(request, progress, ct);

            var last = _status.TryGetValue(modelName, out var snapshot) ? snapshot : null;
            SetStatus(new ImageModelDownloadStatus
            {
                ModelName = modelName,
                Phase = ImageModelDownloadPhase.Completed,
                CompletedBytes = last?.CompletedBytes ?? last?.TotalBytes,
                TotalBytes = last?.TotalBytes,
                SanitizedError = null
            });
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
        catch (UnauthorizedAccessException exception)
        {
            // A fixed, path-free text: the raw message names the denied path.
            SetTerminal(modelName, ImageModelDownloadPhase.Failed, "The image models folder is not writable. Check its permissions.");
            _logger.LogWarning(exception, "Image model download failed for {ModelName}: access denied.", modelName);
        }
        catch (Exception exception)
        {
            // Every other fault must still end the status, or the UI spins until restart. Never surface a raw
            // message (URL or path): collapse it.
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

    // Records byte progress WITHOUT ever resurrecting a finished download. Progress<T> marshals its callback through the captured context, so a tick queued just
    // before completion can be delivered after the terminal write; publishing it unguarded would flip a Completed/Failed download back to Running and hang the UI.
    private void ReportRunningProgress(string modelName, PullProgress update)
    {
        ImageModelDownloadStatus status;
        lock (_statusGate)
        {
            // A terminal entry stays as it is: nothing to record or push.
            if (_status.TryGetValue(modelName, out var existing) && existing.Phase != ImageModelDownloadPhase.Running)
            {
                return;
            }

            status = new ImageModelDownloadStatus
            {
                ModelName = modelName,
                Phase = ImageModelDownloadPhase.Running,
                CompletedBytes = update.CompletedBytes,
                TotalBytes = update.TotalBytes,
                SanitizedError = null,
                PartIndex = update.PartIndex,
                PartCount = update.PartCount,
                Sequence = ++_sequence
            };
            _status[modelName] = status;

            var now = _timeProvider.GetUtcNow().UtcTicks;
            if (_lastProgressPushTicks.TryGetValue(modelName, out var last) && now - last < ProgressPushInterval.Ticks)
            {
                return;
            }

            _lastProgressPushTicks[modelName] = now;
        }

        _ = PushAsync(status);
    }

    private void SetTerminal(string modelName, ImageModelDownloadPhase phase, string? sanitizedError)
    {
        SetStatus(new ImageModelDownloadStatus
        {
            ModelName = modelName,
            Phase = phase,
            CompletedBytes = null,
            TotalBytes = null,
            SanitizedError = sanitizedError
        });
    }

    // The start and every terminal phase always push; the start primes the progress throttle, a terminal phase drops it.
    private void SetStatus(ImageModelDownloadStatus status)
    {
        ImageModelDownloadStatus stamped;
        lock (_statusGate)
        {
            stamped = status with
            {
                Sequence = ++_sequence
            };
            _status[status.ModelName] = stamped;
            if (status.Phase == ImageModelDownloadPhase.Running)
            {
                _lastProgressPushTicks[status.ModelName] = _timeProvider.GetUtcNow().UtcTicks;
            }
            else
            {
                _ = _lastProgressPushTicks.TryRemove(status.ModelName, out _);
            }
        }

        _ = PushAsync(stamped);
    }

    // Fire-and-forget from a synchronous Progress<T> callback, like the GGUF lane: a failed push is only debug-logged,
    // because the list endpoint still serves the status. No request token exists here (MA0032/CA2016 opt-out).
    private async Task PushAsync(ImageModelDownloadStatus status)
    {
        try
        {
            await _eventPublisher.PublishModelDownloadAsync(status, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not push the image model download status for {ModelName}.", status.ModelName);
        }
    }
}
