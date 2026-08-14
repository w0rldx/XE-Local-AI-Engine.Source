namespace XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

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
    // Minimum gap between two pushed Running progress updates for the same model — protects the socket from a
    // high-frequency byte callback. Terminal phase changes (Completed/Cancelled/Failed) and the initial Running push
    // always go out immediately, bypassing the throttle.
    private static readonly TimeSpan ProgressPushInterval = TimeSpan.FromSeconds(1);

    private readonly IGgufDownloadEventPublisher _eventPublisher;

    // Keyed by canonical model name. An in-flight download owns a live CTS; the status cell is updated as progress flows.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    // Last instant a Running progress push was broadcast per model, so high-frequency byte callbacks are throttled to at
    // most one push per ProgressPushInterval. Keyed by canonical model name; the entry is dropped on terminal phase.
    private readonly ConcurrentDictionary<string, long> _lastProgressPushTicks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<GgufDownloadCoordinator> _logger;
    private readonly IGgufModelStore _modelStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<string, GgufDownloadStatus> _status = new(StringComparer.OrdinalIgnoreCase);

    public GgufDownloadCoordinator(IGgufModelStore modelStore,
        IServiceScopeFactory scopeFactory,
        IGgufDownloadEventPublisher eventPublisher,
        ILogger<GgufDownloadCoordinator> logger)
    {
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _eventPublisher = eventPublisher ?? throw new ArgumentNullException(nameof(eventPublisher));
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

        // Initial Running status: write it and push immediately (bypass the progress throttle) so the operator UI shows
        // the new download the instant it is accepted, even before the first byte callback arrives.
        SetStatus(new GgufDownloadStatus(modelName, GgufDownloadPhase.Running, CompletedBytes: null, TotalBytes: null, SanitizedError: null), isInitialOrTerminal: true);

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

    public IReadOnlyList<GgufDownloadStatus> ListStatuses() =>
        _status.Values.ToList();

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
    // singleton and the coordinated map facade is scoped, so the write goes through a fresh DI scope. Caller cancellation
    // propagates; any other failure is swallowed with a warning so a
    // successful download is never reported as failed because the routing row could not be persisted.
    /// <summary>
    ///     Adds the just-installed model to the tool-capable allow-list when its GGUF chat template advertises tool
    ///     calling. Best-effort: a failure here leaves the operator with the existing (possibly stale) list, which is the
    ///     pre-change behaviour — never a failed download.
    /// </summary>
    private async Task RegisterToolCapabilityAsync(string modelName, CancellationToken token)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var registrar = scope.ServiceProvider.GetRequiredService<IToolCapableModelRegistrar>();
            _ = await registrar.RegisterIfToolCapableAsync(modelName, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Could not record tool capability for {ModelName}; the configured tool-capable model list still applies.",
                modelName);
        }
    }

    private async Task MapModelToLlamaCppAsync(string modelName, CancellationToken token)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var leaseCoordinator = scope.ServiceProvider.GetRequiredService<IModelProviderMapLeaseCoordinator>();
            var mapStore = scope.ServiceProvider.GetRequiredService<ICoordinatedModelProviderMapStore>();
            await using var lease = await leaseCoordinator.AcquireMapMutationAsync(modelName,
                ModelProviderMapMutationKind.MapClaim,
                token).ConfigureAwait(false);
            var claim = await mapStore.TryClaimLlamaCppAsync(lease, modelName, token).ConfigureAwait(false);
            if (claim is ProviderMapClaimResult.Conflict conflict)
            {
                _logger.LogWarning("Could not map {ModelName} to llamacpp because it is already mapped to {ProviderName}.",
                    modelName,
                    conflict.ExistingProvider);
                return;
            }

            // AUD4-16: the just-written row must be visible immediately, so drop the resolver's short-TTL provider-name
            // cache. Optional resolve — the singleton resolver may be absent in a narrow test host; the TTL is the backstop.
            scope.ServiceProvider.GetService<ILocalModelProviderResolver>()?.InvalidateModelProviderMap();
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

    // Records the latest status in the registry and broadcasts it to connected operator clients. Running progress pushes
    // are throttled to at most one per ProgressPushInterval per model so a high-frequency byte callback never floods the
    // socket; the initial Running push and every terminal phase (Completed/Cancelled/Failed) bypass the throttle and go
    // out immediately. The registry write is always unconditional, so the list endpoint still serves the freshest bytes.
    private void SetStatus(GgufDownloadStatus status, bool isInitialOrTerminal = false)
    {
        _status[status.ModelName] = status;

        if (isInitialOrTerminal)
        {
            // Drop the throttle bookkeeping on a terminal phase; the initial push primes it so the first throttled
            // progress tick still has to wait out the interval.
            if (status.Phase == GgufDownloadPhase.Running)
            {
                _lastProgressPushTicks[status.ModelName] = DateTimeOffset.UtcNow.UtcTicks;
            }
            else
            {
                _lastProgressPushTicks.TryRemove(status.ModelName, out _);
            }

            BroadcastStatus(status);
            return;
        }

        // Throttled Running progress: push only when at least ProgressPushInterval has elapsed since the last push.
        var now = DateTimeOffset.UtcNow.UtcTicks;
        var last = _lastProgressPushTicks.TryGetValue(status.ModelName, out var ticks) ? ticks : 0L;
        if (now - last < ProgressPushInterval.Ticks)
        {
            return;
        }

        _lastProgressPushTicks[status.ModelName] = now;
        BroadcastStatus(status);
    }

    // Maps the internal status to the sanitized hub event at the broadcast boundary (no internal type leaks) and pushes
    // it fire-and-forget. The Progress<T> callback is synchronous and must not block byte flow, so a push failure is
    // swallowed with a debug log — the list endpoint remains the authoritative one-shot hydrate either way.
    private void BroadcastStatus(GgufDownloadStatus status)
    {
        var hubEvent = new GgufDownloadStatusHubEvent(status.ModelName,
            status.Phase.ToString(),
            status.CompletedBytes,
            status.TotalBytes,
            status.SanitizedError);

        _ = PublishStatusAsync(hubEvent);
    }

    private async Task PublishStatusAsync(GgufDownloadStatusHubEvent hubEvent)
    {
        try
        {
            await _eventPublisher.PublishStatusAsync(hubEvent).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Could not push the GGUF download status for {ModelName}; the list endpoint still serves it.", hubEvent.ModelName);
        }
    }

    private async Task RunDownloadAsync(string modelName, GgufModelRequest request, CancellationToken token)
    {
        var progress = new Progress<PullProgress>(update => SetStatus(new GgufDownloadStatus(modelName,
            GgufDownloadPhase.Running,
            update.CompletedBytes,
            update.TotalBytes,
            SanitizedError: null)));

        try
        {
            await _modelStore.EnsureModelAsync(request, progress, token).ConfigureAwait(false);

            // Route this GGUF to the llama.cpp runtime: write the model_provider_map row so the provider resolver
            // dispatches it to "llamacpp" regardless of the unmapped-routing default. The store registers the GGUF in
            // its own registry (index.json) but does NOT touch the provider map, so this is the single production
            // writer that makes a downloaded GGUF reachable. Best-effort: a map-write failure must not mark the
            // (successful) download as Failed — the default-provider flip still routes it.
            await MapModelToLlamaCppAsync(modelName, token).ConfigureAwait(false);

            // Admit the model to the tool-capable allow-list when its chat template says it supports tool calls. Without
            // this, a user who followed the app's own recommendation downloaded a tool-capable model and silently got no
            // tool calling, because the gate is exact membership of a list whose shipped default named only two
            // previous-generation models. Same best-effort contract as the provider mapping above: this must never turn
            // a successful download into a Failed one.
            await RegisterToolCapabilityAsync(modelName, token).ConfigureAwait(false);

            var last = _status.TryGetValue(modelName, out var snapshot) ? snapshot : null;
            SetStatus(new GgufDownloadStatus(modelName,
                    GgufDownloadPhase.Completed,
                    last?.CompletedBytes ?? last?.TotalBytes,
                    last?.TotalBytes,
                    SanitizedError: null),
                isInitialOrTerminal: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus(new GgufDownloadStatus(modelName, GgufDownloadPhase.Cancelled, CompletedBytes: null, TotalBytes: null, SanitizedError: null), isInitialOrTerminal: true);
            _logger.LogInformation("Operator cancelled the GGUF download for {ModelName}.", modelName);
        }
        catch (HuggingFaceDownloadException exception)
        {
            // Message is contractually sanitized (no token / Bearer / path) — safe to surface to the operator.
            SetStatus(new GgufDownloadStatus(modelName, GgufDownloadPhase.Failed, CompletedBytes: null, TotalBytes: null, exception.Message), isInitialOrTerminal: true);
            _logger.LogWarning("GGUF download failed for {ModelName} ({Reason}).", modelName, exception.Reason);
        }
        catch (InsufficientDiskSpaceException exception)
        {
            SetStatus(new GgufDownloadStatus(modelName, GgufDownloadPhase.Failed, CompletedBytes: null, TotalBytes: null, exception.Message), isInitialOrTerminal: true);
            _logger.LogWarning("GGUF download failed for {ModelName}: insufficient disk space.", modelName);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException or InvalidOperationException)
        {
            // Never surface the raw transport message (it can carry a URL/path): collapse to a generic sanitized reason.
            SetStatus(new GgufDownloadStatus(modelName, GgufDownloadPhase.Failed, CompletedBytes: null, TotalBytes: null, "Download failed."), isInitialOrTerminal: true);
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
