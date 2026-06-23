namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Default <see cref="IGgufDownloadCoordinator" />. Starts each download on a detached task wired to a per-model
///     <see cref="CancellationTokenSource" /> kept in an in-memory registry, captures the latest sanitized progress, and
///     lets a separate request cancel the in-flight download by model name.
///     <para>
///         <b>Singleton.</b> The registry must outlive any one request scope (the download runs after the HTTP request
///         that started it returns). It composes the singleton Hugging Face GGUF store <see cref="IGgufModelStore" />.
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
    // Keyed by canonical model name. An in-flight download owns a live CTS; the status cell is updated as progress flows.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<GgufDownloadCoordinator> _logger;
    private readonly IGgufModelStore _modelStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, GgufDownloadStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    public GgufDownloadCoordinator(IGgufModelStore modelStore, IServiceScopeFactory scopeFactory, ILogger<GgufDownloadCoordinator> logger)
    {
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GgufDownloadTicket> StartAsync(GgufModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Canonical identity the operator tracks/cancels by — resolved the SAME way the store registers it, so the
        // track/cancel key matches the installed-model identity even when a base-quant request resolves to a different
        // file (e.g. an Unsloth Dynamic variant). If resolution fails (discovery unreachable), fall back to the
        // request-derived label so Start never throws — the detached download then surfaces the failure under that name.
        var modelName = await ResolveModelNameAsync(request, ct).ConfigureAwait(false);

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

    // Resolves the canonical model name via the store; on a discovery/transport failure (or HttpClient request TIMEOUT,
    // which surfaces as a non-caller OperationCanceledException) falls back to the request-derived label so a download
    // can still be started and surface its own failure. Genuine caller cancellation propagates.
    private async Task<string> ResolveModelNameAsync(GgufModelRequest request, CancellationToken ct)
    {
        try
        {
            return await _modelStore.ResolveModelNameAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or HuggingFaceDownloadException or IOException or TimeoutException or InvalidOperationException
                                              or OperationCanceledException)
        {
            _logger.LogDebug(exception, "Could not pre-resolve the GGUF model name for {RepoId}; using the request-derived key.", request.RepoId);
            return string.IsNullOrWhiteSpace(request.Quant)
                ? request.RepoId
                : GgufModelName.Format(request.RepoId, request.Quant);
        }
    }

    // Writes the model_provider_map row pointing the canonical GGUF name at the llama.cpp provider. The coordinator is a
    // SINGLETON and IModelProviderMapStore is SCOPED, so the write goes through a fresh DI scope (same pattern the
    // provider resolver uses). Caller-cancellation propagates; any other failure is swallowed with a warning so a
    // successful download is never reported as failed because the routing row could not be persisted.
    private async Task MapModelToLlamaCppAsync(string modelName, CancellationToken token)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var mapStore = scope.ServiceProvider.GetRequiredService<IModelProviderMapStore>();
            await mapStore.UpsertAsync(modelName, LlamaServerProviderConstants.ProviderName, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not persist the llamacpp provider mapping for {ModelName}; the default-provider routing still applies.", modelName);
        }
    }

    private async Task RunDownloadAsync(string modelName, GgufModelRequest request, CancellationToken token)
    {
        var progress = new Progress<PullProgress>(update => _status[modelName] = new GgufDownloadStatus(modelName,
            GgufDownloadPhase.Running,
            update.CompletedBytes,
            update.TotalBytes,
            SanitizedError: null));

        try
        {
            await _modelStore.EnsureModelAsync(request, progress, token).ConfigureAwait(false);

            // Route this GGUF to the llama.cpp runtime: write the model_provider_map row so the provider resolver
            // dispatches it to "llamacpp" regardless of the unmapped-routing default. The store registers the GGUF in
            // its own registry (index.json) but does NOT touch the provider map, so this is the single production
            // writer that makes a downloaded GGUF reachable. Best-effort: a map-write failure must not mark the
            // (successful) download as Failed — the default-provider flip still routes it.
            await MapModelToLlamaCppAsync(modelName, token).ConfigureAwait(false);

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
