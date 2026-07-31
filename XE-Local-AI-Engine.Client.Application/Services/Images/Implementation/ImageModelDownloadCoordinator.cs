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
///         <c>.part</c> file resumes on the next start). Byte progress is whatever the underlying store reports, and for
///         a multi-part file-set it reflects the part currently transferring, not the set total.
///     </para>
/// </summary>
public sealed class ImageModelDownloadCoordinator : IImageModelDownloadCoordinator
{
    // In-flight model names. Single-flight only — there is no cancel surface for image downloads yet, so a token source
    // per entry would be dead weight; the value is a presence marker.
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
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
        if (!_inFlight.TryAdd(modelName, value: 0))
        {
            return new ImageModelDownloadTicket(modelName, AlreadyInFlight: true);
        }

        // Publish Running before the transfer starts so a poll landing between accept and the first byte callback sees
        // the download rather than an empty registry.
        _status[modelName] = new ImageModelDownloadStatus(modelName, ImageModelDownloadPhase.Running, CompletedBytes: null, TotalBytes: null, SanitizedError: null);

        _ = RunDownloadAsync(modelName, request);
        return new ImageModelDownloadTicket(modelName, AlreadyInFlight: false);
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
    private async Task RunDownloadAsync(string modelName, ImageModelRequest request)
    {
        var progress = new Progress<PullProgress>(update => ReportRunningProgress(modelName, update));

        try
        {
            _ = await _modelStore.EnsureModelAsync(request, progress, CancellationToken.None).ConfigureAwait(false);

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
            _ = _inFlight.TryRemove(modelName, out _);
        }
    }

    // Records byte progress WITHOUT ever resurrecting a finished download. Progress<T> marshals its callback through the
    // captured context, so a tick queued just before completion can be delivered after the terminal write — publishing
    // it unguarded would flip a Completed/Failed download back to Running and hang the UI on a download that is over.
    private void ReportRunningProgress(string modelName, PullProgress update)
    {
        _ = _status.AddOrUpdate(modelName,
            key => new ImageModelDownloadStatus(key, ImageModelDownloadPhase.Running, update.CompletedBytes, update.TotalBytes, SanitizedError: null),
            (_, existing) => existing.Phase != ImageModelDownloadPhase.Running
                ? existing
                : existing with
                {
                    CompletedBytes = update.CompletedBytes,
                    TotalBytes = update.TotalBytes
                });
    }

    private void SetTerminal(string modelName, ImageModelDownloadPhase phase, string? sanitizedError)
    {
        _status[modelName] = new ImageModelDownloadStatus(modelName, phase, CompletedBytes: null, TotalBytes: null, sanitizedError);
    }
}
