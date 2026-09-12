namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     Owns the node's single resident <c>whisper-server</c> child process: reuse-or-spawn behind a single-flight
///     gate, readiness gating on the health route, an in-place model switch when the daemon is healthy, loopback port
///     allocation by bind probe, idle-TTL eviction with a background reaper, per-OS tree-kill teardown, and an
///     orphan-free shutdown.
/// </summary>
/// <remarks>
///     <para>
///         <b>Reduced to ONE daemon on purpose.</b> The image supervisor this ports is keyed by model with a loaded
///         cap and least-recently-used eviction. A node has one selected transcription model, and the server
///         serializes every request on a single mutex, so a second daemon could never serve anyone faster. The cap,
///         the LRU and the per-model gate dictionary are not ported; everything else is kept one for one.
///     </para>
///     <para>
///         <b>GPU loads are serialized process-wide.</b> Both a spawn and an in-place model switch initialise GPU
///         weights, so both acquire the shared admission gate that the llama-server and image supervisors also use,
///         and hold it through readiness, failure, timeout and process exit. A CPU backend bypasses it entirely — it
///         does not contend for VRAM.
///     </para>
/// </remarks>
internal sealed class WhisperServerProcessSupervisor : IWhisperServerSupervisor, IAsyncDisposable
{
    /// <summary>Poll cadence for noticing that a freshly spawned process exited during its readiness wait.</summary>
    private static readonly TimeSpan ProcessExitPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IWhisperCppBinaryManager _binaryManager;
    private readonly IWhisperBackendSelector _backendSelector;
    private readonly SemaphoreSlim _ensureGate = new(initialCount: 1, maxCount: 1);
    private readonly HttpClient _httpClient;
    private readonly IWhisperServerProcessLauncher _launcher;
    private readonly IGpuModelLoadAdmission _loadAdmission;
    private readonly ILogger<WhisperServerProcessSupervisor> _logger;
    private readonly WhisperRuntimeOptions _options;
    private readonly IWhisperServerReadinessProbe _readinessProbe;
    private readonly Task _reaperLoop;
    private readonly IWhisperRuntimeActivityGate _runtimeActivityGate;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Lock _stateGate = new();
    private readonly TimeProvider _timeProvider;

    private RunningServer? _current;
    private int _disposed;
    private long _generation;
    private bool _starting;

    /// <summary>Creates the supervisor over its collaborators. The idle reaper loop starts immediately.</summary>
    internal WhisperServerProcessSupervisor(IWhisperBackendSelector backendSelector,
        IWhisperCppBinaryManager binaryManager,
        IWhisperServerProcessLauncher launcher,
        IWhisperServerReadinessProbe readinessProbe,
        HttpClient httpClient,
        WhisperRuntimeOptions options,
        TimeProvider? timeProvider = null,
        ILogger<WhisperServerProcessSupervisor>? logger = null,
        IGpuModelLoadAdmission? loadAdmission = null,
        IWhisperRuntimeActivityGate? runtimeActivityGate = null)
    {
        _backendSelector = backendSelector ?? throw new ArgumentNullException(nameof(backendSelector));
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _readinessProbe = readinessProbe ?? throw new ArgumentNullException(nameof(readinessProbe));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<WhisperServerProcessSupervisor>.Instance;
        _runtimeActivityGate = runtimeActivityGate ?? new WhisperRuntimeActivityGate();

        // Absent a wired gate (a provider-only host, or a test), default to the no-op floor so GPU-load serialization
        // is simply off. The composition root injects the real singleton shared with the other supervisors.
        _loadAdmission = loadAdmission ?? new NoOpGpuModelLoadAdmission();

        _reaperLoop = Task.Run(() => ReapIdleLoopAsync(_shutdownCts.Token), _shutdownCts.Token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
        {
            return;
        }

        await _shutdownCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _reaperLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        if (Detach() is { } detached)
        {
            KillDetached(detached);
        }

        _ensureGate.Dispose();
        _shutdownCts.Dispose();
    }

    /// <inheritdoc />
    public async Task<WhisperServerEndpoint> EnsureRunningAsync(string modelId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        using var spawnLease = _runtimeActivityGate.TryAcquireSpawnReadinessLease()
                               ?? throw new WhisperRuntimeException("The transcription runtime is busy with an exclusive operation.");

        // Fast path: a live daemon already serving this model is reused without taking the ensure gate, subject to a
        // rate-limited liveness probe so a wedged daemon is respawned rather than handed out forever.
        if (Current is { } existing && !existing.Handle.HasExited && existing.ServesModel(modelId))
        {
            var reused = await TryReuseAsync(existing, ct).ConfigureAwait(false);
            if (reused is not null)
            {
                return reused;
            }
        }

        await _ensureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate: another caller may have spawned or switched while we waited.
            if (Current is { } underGate && !underGate.Handle.HasExited)
            {
                if (underGate.ServesModel(modelId))
                {
                    var reused = await TryReuseAsync(underGate, ct).ConfigureAwait(false);
                    if (reused is not null)
                    {
                        return reused;
                    }
                }
                else if (await TryLoadModelAsync(underGate, modelId, ct).ConfigureAwait(false) is { } switched)
                {
                    return switched;
                }
            }

            TearDownCurrent();
            var running = await SpawnOnceAsync(modelId, ct).ConfigureAwait(false);
            return running.Endpoint;
        }
        finally
        {
            try
            {
                _ensureGate.Release();
            }
            catch (ObjectDisposedException)
            {
                // DisposeAsync disposes the gate. When a dispose races a spawn unwinding on the shutdown-linked token,
                // the gate can already be gone here; the release is moot at teardown. Swallowing it lets the real
                // unwind cause surface instead of a leaked ObjectDisposedException.
            }
        }
    }

    /// <inheritdoc />
    public Task<WhisperServerEvictResult> EvictAsync(CancellationToken ct)
    {
        var reservation = _runtimeActivityGate.TryAcquireEvictionReservation();
        if (reservation is null)
        {
            // A transcription or a spawn is in flight. This is the 409 the endpoint reports, with the snapshot that
            // tells the operator what to wait for.
            return Task.FromResult(new WhisperServerEvictResult(false, _runtimeActivityGate.GetSnapshot()));
        }

        using (reservation)
        {
            // Detached under the state lock, tree-killed outside it: a kill is slow and holds nothing useful.
            if (Detach() is { } detached)
            {
                KillDetached(detached);
            }
        }

        return Task.FromResult(new WhisperServerEvictResult(true, _runtimeActivityGate.GetSnapshot()));
    }

    /// <inheritdoc />
    public IWhisperTranscriptionLease? TryAcquireTranscriptionLease(string modelId, long generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        var running = Current;
        if (running is null || running.Handle.HasExited || !running.Matches(modelId, generation))
        {
            return null;
        }

        // Atomically register against the daemon, refusing when the reaper or a model switch has already latched it.
        if (!running.TryAcquireTranscription())
        {
            return null;
        }

        // Confirm it is still the REGISTERED, live daemon: a teardown between the lookup and here would leave this
        // lease guarding a dead handle.
        if (running.Handle.HasExited || !ReferenceEquals(Current, running) || !running.Matches(modelId, generation))
        {
            running.ReleaseTranscription();
            return null;
        }

        var activityLease = _runtimeActivityGate.TryAcquireTranscriptionLease();
        if (activityLease is null)
        {
            running.ReleaseTranscription();
            return null;
        }

        running.MarkUsed(_timeProvider.GetUtcNow());
        return new WhisperTranscriptionLease(running, activityLease, _timeProvider);
    }

    /// <inheritdoc />
    public WhisperRuntimeStatusSnapshot GetStatus()
    {
        RunningServer? running;
        bool starting;
        lock (_stateGate)
        {
            running = _current;
            starting = _starting;
        }

        if (running is null || running.Handle.HasExited)
        {
            return new WhisperRuntimeStatusSnapshot(starting ? WhisperRuntimeState.Starting : WhisperRuntimeState.Stopped,
                LoadedModelId: null,
                Backend: null,
                BinaryVersion: null,
                BinarySource: null,
                WhisperFfmpegProbe.IsAvailable);
        }

        if (starting)
        {
            // An in-place model switch: the daemon is resident but answering 503, and the model it still holds is the
            // one being replaced. Reporting the old id as Ready would tell the operator a request will be served.
            return new WhisperRuntimeStatusSnapshot(WhisperRuntimeState.Starting,
                LoadedModelId: null,
                running.Binary.Backend,
                running.Binary.Version,
                ResolveBinarySource(running.Binary),
                WhisperFfmpegProbe.IsAvailable);
        }

        return new WhisperRuntimeStatusSnapshot(WhisperRuntimeState.Ready,
            running.ModelId,
            running.Binary.Backend,
            running.Binary.Version,
            ResolveBinarySource(running.Binary),
            WhisperFfmpegProbe.IsAvailable);
    }

    private RunningServer? Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current;
            }
        }
    }

    private static WhisperBinarySource ResolveBinarySource(WhisperBinary binary)
    {
        if (binary.IsPinnedFallback)
        {
            return WhisperBinarySource.Pinned;
        }

        return string.Equals(binary.Version, "byo", StringComparison.Ordinal)
            ? WhisperBinarySource.BringYourOwn
            : WhisperBinarySource.Managed;
    }

    /// <summary>
    ///     Reuse decision for a live daemon already serving the requested model: hands back its endpoint, or returns
    ///     <see langword="null" /> after tearing it down when it is wedged — alive, but unresponsive to
    ///     <see cref="WhisperRuntimeOptions.MaxReuseLivenessFailures" /> consecutive probes. The probe is rate limited
    ///     to at most one per interval per daemon, so the hot path costs no HTTP at all between probes.
    /// </summary>
    private async Task<WhisperServerEndpoint?> TryReuseAsync(RunningServer existing, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        if (!existing.TryClaimLivenessProbe(now, _options.ReuseLivenessProbeInterval))
        {
            existing.MarkUsed(now);
            return existing.Endpoint;
        }

        var responsive = await ProbeResponsiveWithTimeoutAsync(existing.Endpoint.BaseAddress, ct).ConfigureAwait(false);
        if (responsive)
        {
            existing.ResetLivenessFailures();
            existing.MarkUsed(_timeProvider.GetUtcNow());
            return existing.Endpoint;
        }

        // Under the threshold the daemon is still handed out: one transient probe failure must never tear down a
        // daemon that is merely busy.
        var failures = existing.RecordLivenessFailure();
        if (failures < _options.MaxReuseLivenessFailures)
        {
            existing.MarkUsed(_timeProvider.GetUtcNow());
            return existing.Endpoint;
        }

        // Wedged. Every reuse refreshes the idle clock, so the reaper would never see it: tear it down here so the
        // caller respawns instead of being handed a hung endpoint forever.
        _logger.LogWarning("whisper-server for model {ModelId} is wedged ({Failures} consecutive failed liveness probes); tree-killing to respawn.",
            existing.ModelId, failures);
        TearDownCurrent();
        return null;
    }

    private async Task<bool> ProbeResponsiveWithTimeoutAsync(Uri baseAddress, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.ReuseLivenessProbeTimeout);
        try
        {
            return await _readinessProbe.CheckResponsiveAsync(baseAddress, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The probe exceeded its own budget rather than the caller's: treat the daemon as unresponsive.
            return false;
        }
    }

    /// <summary>Resolves the model, backend and binary, launches, readiness-gates and registers the daemon.</summary>
    private async Task<RunningServer> SpawnOnceAsync(string modelId, CancellationToken ct)
    {
        var entry = WhisperModelCatalog.Find(modelId)
                    ?? throw new WhisperRuntimeException("The selected transcription model is not installed.");
        var modelPath = ResolveModelPath(entry);
        RequireConfiguredVadFile();

        var backend = await _backendSelector.SelectBackendAsync(ct).ConfigureAwait(false);
        var binary = await _binaryManager.EnsureBinaryAsync(backend, ct).ConfigureAwait(false);

        // Link the spawn/readiness window to the supervisor's shutdown token, so a DisposeAsync racing this spawn
        // cancels the readiness wait and the catch below tree-kills the launched handle instead of orphaning it: this
        // spawn registers itself only AFTER readiness, so disposal's own teardown cannot see it yet.
        using var spawnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        var spawnCt = spawnCts.Token;

        SetStarting(starting: true);
        IWhisperServerProcessHandle? handle = null;
        var port = 0;
        try
        {
            // The BINARY's backend decides, not the host probe's: a bring-your-own override may serve a different one.
            // A CPU backend bypasses the gate entirely; it does not contend for VRAM.
            using var admissionTicket = binary.Backend == WhisperBackend.Cpu
                ? null
                : await _loadAdmission.AcquireAsync(spawnCt).ConfigureAwait(false);

            port = AllocatePort();
            var spec = WhisperServerArgumentBuilder.Build(modelId,
                modelPath,
                binary.ServerExecutablePath,
                binary.Backend,
                port,
                _options,
                Environment.ProcessorCount);

            handle = _launcher.Launch(spec);
            _logger.LogInformation("whisper-server spawned for model {ModelId} (pid {ProcessId}, port {Port}).",
                modelId, handle.ProcessId, port);

            var readyStartedUtc = _timeProvider.GetUtcNow();
            await WaitForReadyOrExitAsync(handle, spec.BaseAddress, _options.ReadinessTimeout, spawnCt).ConfigureAwait(false);
            _logger.LogInformation("whisper-server ready for model {ModelId} (pid {ProcessId}) after {ElapsedMs:F0} ms.",
                modelId, handle.ProcessId, (_timeProvider.GetUtcNow() - readyStartedUtc).TotalMilliseconds);

            var residentLease = _runtimeActivityGate.TryAcquireResidentProcessLease()
                                ?? throw new WhisperRuntimeException("The transcription runtime became busy before the server process could be registered.");

            var generation = Interlocked.Increment(ref _generation);
            var running = new RunningServer(handle,
                new WhisperServerEndpoint(modelId, generation, spec.BaseAddress),
                binary,
                port,
                _timeProvider.GetUtcNow(),
                residentLease);

            lock (_stateGate)
            {
                _current = running;
            }

            // A DisposeAsync that ran while this spawn was in flight tore down only what its snapshot held; this one
            // registered after that, so it would be left resident. Tear it down here. The detach/kill pair owns the
            // handle from this point, so null it out to keep the catch from acting on it twice.
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (Detach() is { } detached)
                {
                    KillDetached(detached);
                }

                handle = null;
                throw new ObjectDisposedException(nameof(WhisperServerProcessSupervisor));
            }

            return running;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException and not ObjectDisposedException)
            {
                // There is no restart loop here, so a readiness timeout, an exit-while-loading or a missing model goes
                // straight to the caller — log the cause before the sanitized message bubbles up.
                _logger.LogError(ex, "whisper-server start failed for model {ModelId}.", modelId);
            }

            handle?.TreeKill();
            handle?.Dispose();
            throw;
        }
        finally
        {
            SetStarting(starting: false);
        }
    }

    /// <summary>
    ///     Switches the resident daemon's model in place. Returns the new endpoint on success, or <see langword="null" />
    ///     when the caller should tear down and respawn instead.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Exclusive: the same compare-and-swap latch an eviction uses, so the switch succeeds only when no
    ///         transcription is in flight and, once latched, no new lease can attach. A refused latch is reported as a
    ///         busy failure rather than switching the model out from under an in-flight request.
    ///     </para>
    ///     <para>
    ///         Admission-gated on a GPU backend for the whole method: an in-place load initialises GPU weights exactly
    ///         as a spawn does, so skipping the gate here would let a model switch race another runtime's load.
    ///     </para>
    ///     <para>
    ///         A failed load can kill the server outright — the daemon exits when a model fails to initialise, after
    ///         it has already freed the previous one — so any non-2xx, health timeout or process exit returns
    ///         <see langword="null" /> and the caller respawns.
    ///     </para>
    /// </remarks>
    private async Task<WhisperServerEndpoint?> TryLoadModelAsync(RunningServer running, string modelId, CancellationToken ct)
    {
        var entry = WhisperModelCatalog.Find(modelId)
                    ?? throw new WhisperRuntimeException("The selected transcription model is not installed.");
        var modelPath = ResolveModelPath(entry);

        if (!running.TryBeginExclusive())
        {
            throw new WhisperRuntimeException("The transcription runtime is busy with another transcription.");
        }

        // The same flag a spawn raises: for the duration of an in-place load the daemon answers 503 and the model it
        // reports is the one being replaced, so a status that still said Ready would be describing a runtime that
        // cannot serve a request.
        SetStarting(starting: true);

        try
        {
            using var admissionTicket = running.Binary.Backend == WhisperBackend.Cpu
                ? null
                : await _loadAdmission.AcquireAsync(ct).ConfigureAwait(false);

            using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            loadCts.CancelAfter(_options.ModelLoadTimeout);

#pragma warning disable CA2000 // MultipartFormDataContent owns the part it is given and disposes it with itself.
            using var content = new MultipartFormDataContent
            {
                { new StringContent(modelPath), "model" }
            };
#pragma warning restore CA2000

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsync(new Uri(running.Endpoint.BaseAddress, "load"), content, loadCts.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException || (exception is OperationCanceledException && !ct.IsCancellationRequested))
            {
                _logger.LogWarning(exception, "whisper-server model switch to {ModelId} failed on the request; respawning.", modelId);
                return null;
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("whisper-server refused the model switch to {ModelId} ({StatusCode}); respawning.",
                        modelId, (int)response.StatusCode);
                    return null;
                }
            }

            // The server reports 503 while it loads, so readiness is the same health poll a spawn uses — raced against
            // the process exiting, because a failed load takes the daemon with it.
            if (!await WaitForReadyOrExitQuietlyAsync(running.Handle, running.Endpoint.BaseAddress, _options.ReadinessTimeout, ct).ConfigureAwait(false))
            {
                if (running.Handle.HasExited)
                {
                    _logger.LogWarning("whisper-server exited while loading model {ModelId}; respawning.", modelId);
                }

                return null;
            }

            var generation = Interlocked.Increment(ref _generation);
            running.SwitchModel(modelId, generation);
            running.MarkUsed(_timeProvider.GetUtcNow());
            _logger.LogInformation("whisper-server switched to model {ModelId} in place (generation {Generation}).", modelId, generation);
            return running.Endpoint;
        }
        finally
        {
            SetStarting(starting: false);
            running.EndExclusive();
        }
    }

    /// <summary>
    ///     Fails the spawn when a VAD file is configured but absent, instead of launching a daemon that would answer
    ///     with the wrong shape. The argument builder emits <c>--vad</c> and its model path together or not at all, so
    ///     an unconfigured path is a deliberate no-VAD launch and stays legal; a configured path that does not exist is
    ///     an incomplete installation, and the VAD weights are part of installation. Letting it through would either
    ///     stall readiness or produce a running daemon whose every transcription fails opaquely, and the segmenter that
    ///     consumes these segments relies on the server doing the voice-activity split.
    /// </summary>
    private void RequireConfiguredVadFile()
    {
        if (string.IsNullOrWhiteSpace(_options.VadModelPath))
        {
            return;
        }

        if (!File.Exists(_options.VadModelPath))
        {
            throw new WhisperRuntimeException("The voice-activity-detection model is not installed.");
        }
    }

    private string ResolveModelPath(WhisperModelEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_options.ModelsDirectory))
        {
            // The application layer seeds this; without it the provider has no honest way to find a weight file, and
            // guessing a location would be worse than saying so.
            throw new WhisperRuntimeException("The selected transcription model is not installed.");
        }

        var path = Path.Combine(_options.ModelsDirectory, WhisperModelCatalog.RelativeFilePath(entry));
        if (!File.Exists(path))
        {
            throw new WhisperRuntimeException("The selected transcription model is not installed.");
        }

        return path;
    }

    /// <summary>
    ///     Waits for the daemon to report healthy, racing that against the process exiting, and throws a typed failure
    ///     on either bad outcome.
    /// </summary>
    private async Task WaitForReadyOrExitAsync(IWhisperServerProcessHandle handle, Uri baseAddress, TimeSpan budget, CancellationToken ct)
    {
        if (await WaitForReadyOrExitQuietlyAsync(handle, baseAddress, budget, ct).ConfigureAwait(false))
        {
            return;
        }

        throw handle.HasExited
            ? new WhisperRuntimeException("The transcription runtime exited while loading the model. The model may be incompatible with this runtime or too large for the available memory.")
            : new WhisperRuntimeException("The transcription runtime did not become ready in time.");
    }

    /// <summary>The same race, reporting a bool so the model-switch path can respawn instead of failing the caller.</summary>
    private async Task<bool> WaitForReadyOrExitQuietlyAsync(IWhisperServerProcessHandle handle, Uri baseAddress, TimeSpan budget, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var readyTask = _readinessProbe.WaitForReadyAsync(baseAddress, budget, linkedCts.Token);
        var exitTask = WatchForExitAsync(handle, linkedCts.Token);

        var winner = await Task.WhenAny(readyTask, exitTask).ConfigureAwait(false);

        if (winner == exitTask && handle.HasExited)
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await SwallowCancellationAsync(readyTask).ConfigureAwait(false);
            return false;
        }

        await linkedCts.CancelAsync().ConfigureAwait(false);
        await SwallowCancellationAsync(exitTask).ConfigureAwait(false);

        return await readyTask.ConfigureAwait(false);
    }

    private async Task WatchForExitAsync(IWhisperServerProcessHandle handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (handle.HasExited)
            {
                return;
            }

            await Task.Delay(ProcessExitPollInterval, _timeProvider, ct).ConfigureAwait(false);
        }
    }

    private static async Task SwallowCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected: this side of the race was cancelled because the other side won.
        }
    }

    private async Task ReapIdleLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromTicks(Math.Max(_options.IdleTimeToLive.Ticks / 4, TimeSpan.FromSeconds(1).Ticks));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, _timeProvider, ct).ConfigureAwait(false);
                ReapIdleOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void ReapIdleOnce()
    {
        var running = Current;
        if (running is null)
        {
            return;
        }

        // An EXITED process is always torn down, so a dead handle never leaks even while nominally leased.
        if (running.Handle.HasExited)
        {
            TearDownCurrent();
            return;
        }

        if (_timeProvider.GetUtcNow() - running.LastUsedUtc < _options.IdleTimeToLive)
        {
            return;
        }

        // TryBeginEvict latches the daemon only when no transcription holds it, and once latched no new lease can
        // attach — so a transcription starting concurrently with this reap either wins the lease first (and the latch
        // fails, reaping on a later pass) or is refused. A leased daemon can never be tree-killed underneath it.
        if (running.TryBeginEvict())
        {
            _logger.LogInformation("Evicting idle whisper-server for model {ModelId}.", running.ModelId);
            TearDownCurrent();
        }
    }

    private void SetStarting(bool starting)
    {
        lock (_stateGate)
        {
            _starting = starting;
        }
    }

    /// <summary>Removes the daemon from the registry and returns it when this call won the race, else null.</summary>
    private RunningServer? Detach()
    {
        lock (_stateGate)
        {
            var running = _current;
            _current = null;
            return running;
        }
    }

    private void TearDownCurrent()
    {
        if (Detach() is { } detached)
        {
            KillDetached(detached);
        }
    }

    /// <summary>
    ///     Tree-kills and disposes a detached daemon, then releases the resident-process lease it held. The lease is
    ///     released HERE and not at detach time: it is what holds off an exclusive runtime mutation, and a child that
    ///     has been detached but not yet killed still has the binary and the model file open.
    /// </summary>
    private static void KillDetached(RunningServer running)
    {
        try
        {
            running.Handle.TreeKill();
        }
        finally
        {
            running.Handle.Dispose();
            running.ResidentLease.Dispose();
        }
    }

    /// <summary>
    ///     Allocates a free loopback port by bind-probing the configured range. The probe is the real guard: a
    ///     tree-kill returns before the OS reclaims the socket, so a port a dying child held is skipped here rather
    ///     than handed out.
    /// </summary>
    private int AllocatePort()
    {
        for (var port = _options.PortRangeStart; port <= _options.PortRangeEnd; port++)
        {
            if (IsPortFree(port))
            {
                return port;
            }
        }

        throw new WhisperRuntimeException("No free local port is available for the transcription runtime.");
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>The single live, registered daemon, its binary, and the state that drives reuse and eviction.</summary>
    private sealed class RunningServer(
        IWhisperServerProcessHandle handle,
        WhisperServerEndpoint endpoint,
        WhisperBinary binary,
        int port,
        DateTimeOffset startedUtc,
        IWhisperRuntimeActivityLease residentLease)
    {
        private readonly Lock _endpointGate = new();
        private int _consecutiveLivenessFailures;

        // Seeded to the spawn time, so a freshly ready daemon is not re-probed until a full interval has passed.
        private long _lastLivenessProbeTicks = startedUtc.UtcTicks;
        private long _lastUsedTicks = startedUtc.UtcTicks;

        // Lease state, mutated only by atomic compare-and-swap: >= 0 counts in-flight transcriptions; -1 is a terminal
        // latch set by the idle reaper, an eviction or a model switch. A new lease and a teardown decision therefore
        // transition the SAME word and can never both win.
        private int _leaseState;

        private WhisperServerEndpoint _endpoint = endpoint;

        public IWhisperServerProcessHandle Handle { get; } = handle;

        public WhisperBinary Binary { get; } = binary;

        public IWhisperRuntimeActivityLease ResidentLease { get; } = residentLease;

        public int Port { get; } = port;

        public WhisperServerEndpoint Endpoint
        {
            get
            {
                lock (_endpointGate)
                {
                    return _endpoint;
                }
            }
        }

        public string ModelId => Endpoint.ModelId;

        public DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

        public bool ServesModel(string modelId) =>
            string.Equals(Endpoint.ModelId, modelId, StringComparison.OrdinalIgnoreCase);

        public bool Matches(string modelId, long generation)
        {
            var current = Endpoint;
            return current.Generation == generation
                   && string.Equals(current.ModelId, modelId, StringComparison.OrdinalIgnoreCase);
        }

        public void SwitchModel(string modelId, long generation)
        {
            lock (_endpointGate)
            {
                _endpoint = _endpoint with { ModelId = modelId, Generation = generation };
            }
        }

        public void MarkUsed(DateTimeOffset now) => Interlocked.Exchange(ref _lastUsedTicks, now.UtcTicks);

        /// <summary>
        ///     Atomically claims the right to run the reuse-path liveness probe, succeeding only once per interval, so
        ///     concurrent reuses share one probe rather than each issuing their own.
        /// </summary>
        public bool TryClaimLivenessProbe(DateTimeOffset now, TimeSpan interval)
        {
            while (true)
            {
                var last = Interlocked.Read(ref _lastLivenessProbeTicks);
                if (now.UtcTicks - last < interval.Ticks)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _lastLivenessProbeTicks, now.UtcTicks, last) == last)
                {
                    return true;
                }
            }
        }

        public void ResetLivenessFailures() => Interlocked.Exchange(ref _consecutiveLivenessFailures, value: 0);

        public int RecordLivenessFailure() => Interlocked.Increment(ref _consecutiveLivenessFailures);

        /// <summary>Registers an in-flight transcription unless the daemon is latched for teardown or a switch.</summary>
        public bool TryAcquireTranscription()
        {
            while (true)
            {
                var state = Volatile.Read(ref _leaseState);
                if (state < 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _leaseState, state + 1, state) == state)
                {
                    return true;
                }
            }
        }

        public void ReleaseTranscription() => Interlocked.Decrement(ref _leaseState);

        /// <summary>Terminally latches the daemon for teardown, only when nothing is in flight.</summary>
        public bool TryBeginEvict() => Interlocked.CompareExchange(ref _leaseState, value: -1, comparand: 0) == 0;

        /// <summary>
        ///     Latches the daemon for an in-place model switch, only when nothing is in flight. Unlike
        ///     <see cref="TryBeginEvict" /> this latch is RELEASED by <see cref="EndExclusive" />, because the daemon
        ///     survives the operation.
        /// </summary>
        public bool TryBeginExclusive() => Interlocked.CompareExchange(ref _leaseState, value: -1, comparand: 0) == 0;

        public void EndExclusive() => Interlocked.CompareExchange(ref _leaseState, value: 0, comparand: -1);
    }

    /// <summary>A transcription lease over the resident daemon, holding both its lease word and the activity gate.</summary>
    private sealed class WhisperTranscriptionLease(
        RunningServer server,
        IWhisperRuntimeActivityLease activityLease,
        TimeProvider timeProvider) : IWhisperTranscriptionLease
    {
        private int _disposed;

        public void Touch() => server.MarkUsed(timeProvider.GetUtcNow());

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) != 0)
            {
                return;
            }

            server.ReleaseTranscription();
            activityLease.Dispose();
        }
    }
}
