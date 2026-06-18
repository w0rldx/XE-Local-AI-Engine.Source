namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Default <see cref="IGgufDownloadCoordinator" />. Starts each download on a detached task wired to a per-model
///     <see cref="CancellationTokenSource" /> kept in an in-memory registry, captures the latest sanitized progress, and
///     lets a separate request cancel the in-flight download by model name.
///     <para>
///         <b>Singleton.</b> The registry must outlive any one request scope (the download runs after the HTTP request
///         that started it returns). It composes the singleton Lane B <see cref="IGgufModelStore" />.
///     </para>
///     <para>
///         <b>Honest limits.</b> Progress and cancellation are best-effort and process-local: the registry is RAM-only
///         (a node restart drops in-flight state — the partial <c>.part</c> file resumes on the next Start), and cancel
///         is cooperative (it signals the token; the store stops at the next await/byte boundary). It never reports a
///         path/URL/token.
///     </para>
/// </summary>
public sealed class GgufDownloadCoordinator : IGgufDownloadCoordinator
{
    private readonly IGgufModelStore _modelStore;
    private readonly ILogger<GgufDownloadCoordinator> _logger;

    // Keyed by canonical model name. An in-flight download owns a live CTS; the status cell is updated as progress flows.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, GgufDownloadStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    public GgufDownloadCoordinator(IGgufModelStore modelStore, ILogger<GgufDownloadCoordinator> logger)
    {
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public GgufDownloadTicket Start(GgufModelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Canonical identity the operator tracks/cancels by. A request may omit the quant (the store applies its
        // default), so format from the request's repo + the effective quant label when present.
        var modelName = string.IsNullOrWhiteSpace(request.Quant)
            ? request.RepoId
            : GgufModelName.Format(request.RepoId, request.Quant);

        var cts = new CancellationTokenSource();

        // Single-flight per model name: if a download is already registered, rejoin it (do not start a second).
        if (!_inFlight.TryAdd(modelName, cts))
        {
            cts.Dispose();
            return new GgufDownloadTicket(modelName, AlreadyInFlight: true);
        }

        _status[modelName] = new GgufDownloadStatus(modelName, GgufDownloadPhase.Running, CompletedBytes: null, TotalBytes: null, SanitizedError: null);

        // The detached task owns the rest of the CTS lifetime: it captures only the token (a struct), and disposes the
        // CTS by re-reading it from the registry in its finally — so no IDisposable instance is passed into an un-awaited
        // task (CA2025), while Cancel can still signal it via the registry until then.
        _ = RunDownloadAsync(modelName, request, cts.Token);
        return new GgufDownloadTicket(modelName, AlreadyInFlight: false);
    }

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
        }
        catch (ObjectDisposedException)
        {
            // The download finished and disposed its CTS between the lookup and the cancel — treat as nothing to cancel.
            return false;
        }

        return true;
    }

    public GgufDownloadStatus? GetStatus(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        return _status.TryGetValue(modelName, out var status) ? status : null;
    }

    private async Task RunDownloadAsync(string modelName, GgufModelRequest request, CancellationToken token)
    {
        var progress = new Progress<PullProgress>(update => _status[modelName] = new GgufDownloadStatus(
            modelName,
            GgufDownloadPhase.Running,
            update.CompletedBytes,
            update.TotalBytes,
            SanitizedError: null));

        try
        {
            await _modelStore.EnsureModelAsync(request, progress, token).ConfigureAwait(false);

            var last = _status.TryGetValue(modelName, out var snapshot) ? snapshot : null;
            _status[modelName] = new GgufDownloadStatus(modelName,
                GgufDownloadPhase.Completed,
                last?.CompletedBytes ?? last?.TotalBytes,
                last?.TotalBytes,
                SanitizedError: null);
        }
        catch (OperationCanceledException)
        {
            _status[modelName] = new GgufDownloadStatus(modelName, GgufDownloadPhase.Cancelled, CompletedBytes: null, TotalBytes: null, SanitizedError: null);
            _logger.LogInformation("Operator cancelled the GGUF download for {ModelName}.", modelName);
        }
        catch (HuggingFaceDownloadException exception)
        {
            // Message is contractually sanitized (no token / Bearer / path) — safe to surface to the operator.
            _status[modelName] = new GgufDownloadStatus(modelName, GgufDownloadPhase.Failed, CompletedBytes: null, TotalBytes: null, exception.Message);
            _logger.LogWarning("GGUF download failed for {ModelName} ({Reason}).", modelName, exception.Reason);
        }
        catch (InsufficientDiskSpaceException exception)
        {
            _status[modelName] = new GgufDownloadStatus(modelName, GgufDownloadPhase.Failed, CompletedBytes: null, TotalBytes: null, exception.Message);
            _logger.LogWarning("GGUF download failed for {ModelName}: insufficient disk space.", modelName);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException or InvalidOperationException)
        {
            // Never surface the raw transport message (it can carry a URL/path): collapse to a generic sanitized reason.
            _status[modelName] = new GgufDownloadStatus(modelName, GgufDownloadPhase.Failed, CompletedBytes: null, TotalBytes: null, "Download failed.");
            _logger.LogWarning(exception, "GGUF download failed for {ModelName}.", modelName);
        }
        finally
        {
            if (_inFlight.TryRemove(modelName, out var registeredCts))
            {
                registeredCts.Dispose();
            }
        }
    }
}
