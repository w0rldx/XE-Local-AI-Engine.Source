namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     Default <see cref="IImageServerSupervisor" />. Owns every resident <c>sd-server</c> child process: reuse-or-spawn
///     one daemon per model behind a single-flight gate, readiness-gate on start (poll
///     <c>/sdcpp/v1/capabilities</c>), loopback port allocation with collision-retry, idle-TTL eviction with a
///     background reaper, per-OS tree-kill teardown, and a tree-kill + restart abort path. Singleton; disposes every
///     owned process on shutdown. Mirrors <c>LlamaServerProcessSupervisor</c> (reduced: no role split, no benchmark
///     profiling, no external-endpoint attach — the image runtime is one resident daemon per model).
/// </summary>
internal sealed class ImageServerProcessSupervisor : IImageServerSupervisor, IAsyncDisposable
{
    /// <summary>Poll cadence for observing that a freshly spawned process exited during its readiness wait.</summary>
    private static readonly TimeSpan ProcessExitPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly SemaphoreSlim _admissionGate = new(initialCount: 1, maxCount: 1);
    private readonly HashSet<int> _allocatedPorts = [];
    private readonly IStableDiffusionBinaryManager _binaryManager;
    private readonly ISdGpuBackendSelector _backendSelector;

    // Single-flight ensure gate, one semaphore per model key.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _ensureGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly IImageServerProcessLauncher _launcher;
    private readonly ILogger<ImageServerProcessSupervisor> _logger;
    private readonly IImageModelStore _modelStore;
    private readonly IImageRuntimeActivityGate _runtimeActivityGate;
    private readonly StableDiffusionRuntimeOptions _options;

    // One running daemon per model key.
    private readonly ConcurrentDictionary<string, RunningServer> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly IImageServerReadinessProbe _readinessProbe;
    private readonly Task _reaperLoop;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TimeProvider _timeProvider;

    // AUD4-06: the process-wide GPU-load admission gate (shared with the llama-server supervisor). A GPU-backed image
    // load serializes its spawn-through-readiness window through it so it never races an LLM load's free-VRAM read.
    private readonly IGpuModelLoadAdmission _loadAdmission;
    private int _disposed;

    /// <summary>Creates the supervisor over its collaborators. The idle reaper loop starts immediately.</summary>
    internal ImageServerProcessSupervisor(IImageModelStore modelStore,
        ISdGpuBackendSelector backendSelector,
        IStableDiffusionBinaryManager binaryManager,
        IImageServerProcessLauncher launcher,
        IImageServerReadinessProbe readinessProbe,
        StableDiffusionRuntimeOptions options,
        TimeProvider? timeProvider = null,
        ILogger<ImageServerProcessSupervisor>? logger = null,
        IGpuModelLoadAdmission? loadAdmission = null,
        IImageRuntimeActivityGate? runtimeActivityGate = null)
    {
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _backendSelector = backendSelector ?? throw new ArgumentNullException(nameof(backendSelector));
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _readinessProbe = readinessProbe ?? throw new ArgumentNullException(nameof(readinessProbe));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ImageServerProcessSupervisor>.Instance;
        _runtimeActivityGate = runtimeActivityGate ?? new ImageRuntimeActivityGate();

        // Absent a wired gate (a provider-only host / test), default to the no-op floor so GPU-load serialization is
        // simply off — the composition root injects the real singleton shared with the llama-server supervisor.
        _loadAdmission = loadAdmission ?? new NoOpGpuModelLoadAdmission();

        _reaperLoop = Task.Run(() => ReapIdleLoopAsync(_shutdownCts.Token));
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

        foreach (var (key, running) in _processes.ToArray())
        {
            if (DetachProcess(key, running) is { } detached)
            {
                KillDetachedProcess(detached);
            }
        }

        _admissionGate.Dispose();
        foreach (var gate in _ensureGates.Values)
        {
            gate.Dispose();
        }

        _shutdownCts.Dispose();
    }

    /// <inheritdoc />
    public async Task<ImageServerEndpoint> EnsureRunningAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var activityLease = _runtimeActivityGate.TryAcquireSpawnReadinessLease()
                                  ?? throw new StableDiffusionRuntimeException("The image runtime is busy with an exclusive operation.");

        // Fast path: an already-running, live daemon is reused without taking the spawn gate — subject to a rate-limited
        // liveness probe so a wedged (alive but unresponsive) daemon is respawned instead of handed out.
        if (_processes.TryGetValue(modelName, out var existing) && !existing.Handle.HasExited)
        {
            var reused = await TryReuseAsync(modelName, existing, ct).ConfigureAwait(false);
            if (reused is not null)
            {
                return reused;
            }
        }

        return await SpawnUnderGateAsync(modelName, evictFirst: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ImageServerEndpoint> RestartAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var activityLease = _runtimeActivityGate.TryAcquireSpawnReadinessLease()
                                  ?? throw new StableDiffusionRuntimeException("The image runtime is busy with an exclusive operation.");

        // Abort path: tear down the running daemon (dropping its one in-flight job) and spawn a fresh one.
        return await SpawnUnderGateAsync(modelName, evictFirst: true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task EvictAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (_processes.TryGetValue(modelName, out var running))
        {
            await RemoveProcessAsync(modelName, running).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<ImageServerEvictAllResult> EvictAllAsync(CancellationToken ct)
    {
        var reservation = _runtimeActivityGate.TryAcquireEvictionReservation();
        if (reservation is null)
        {
            return new ImageServerEvictAllResult(false, _runtimeActivityGate.GetSnapshot());
        }

        using (reservation)
        {
            // Daemons detached from the table under the gate, tree-killed after it is released (see KillDetachedProcesses).
            var detached = new List<RunningServer>();
            await _admissionGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var (key, running) in _processes.ToArray())
                {
                    if (DetachProcess(key, running) is { } evicted)
                    {
                        detached.Add(evicted);
                    }
                }
            }
            finally
            {
                _admissionGate.Release();
            }

            // Killed outside the gate but BEFORE the snapshot below: each kill releases that daemon's resident-process
            // lease, and the snapshot this returns is the operator's report of what the eviction left behind.
            KillDetachedProcesses(detached);
        }

        return new ImageServerEvictAllResult(true, _runtimeActivityGate.GetSnapshot());
    }

    /// <inheritdoc />
    public IImageServerJobLease? TryAcquireJobLease(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (!_processes.TryGetValue(modelName, out var running) || running.Handle.HasExited)
        {
            return null;
        }

        // Atomically register the lease, refusing if an idle reaper / cap evictor has already latched this daemon for
        // eviction (lease acquisition and idle eviction transition one atomic word, so a teardown
        // decision and a new lease can never both win — the previous plain increment could be granted after the reaper
        // read "no active jobs" but before it tree-killed the daemon). Then confirm the daemon is still the registered,
        // live one: a forced teardown (restart/evict/dispose) that removed it between the lookup and here means the lease
        // would guard a dead handle, so release and return null (leaseless).
        if (!running.TryAcquireJob())
        {
            return null;
        }

        if (running.Handle.HasExited || !_processes.TryGetValue(modelName, out var current) || !ReferenceEquals(current, running))
        {
            running.ReleaseJob();
            return null;
        }

        running.MarkUsed(_timeProvider.GetUtcNow());
        return new ImageJobLease(running, _timeProvider);
    }

    /// <summary>
    ///     Reuse decision for an already-registered, not-yet-exited daemon: hands back its endpoint when it is healthy
    ///     enough, or returns <see langword="null" /> after tearing it down when it is wedged (alive but unresponsive to
    ///     <see cref="StableDiffusionRuntimeOptions.MaxReuseLivenessFailures" /> consecutive liveness probes). The probe is
    ///     rate-limited to at most one per <see cref="StableDiffusionRuntimeOptions.ReuseLivenessProbeInterval" /> per
    ///     daemon, so the hot path stays cheap — between probes the endpoint is reused with no HTTP.
    /// </summary>
    private async Task<ImageServerEndpoint?> TryReuseAsync(string modelName, RunningServer existing, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();

        // Rate limit: only the caller that wins the probe claim issues the HTTP probe this interval; every other caller
        // (and every reuse inside the interval) is handed the endpoint immediately with no probe.
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

        // A failed probe: count it. Under the threshold the daemon is still handed out — a single transient probe failure
        // must never tear down a busy daemon. At/above the threshold it is treated as wedged.
        var failures = existing.RecordLivenessFailure();
        if (failures < _options.MaxReuseLivenessFailures)
        {
            existing.MarkUsed(_timeProvider.GetUtcNow());
            return existing.Endpoint;
        }

        // Wedged: the daemon is alive but has failed the liveness probe N consecutive times, so every reuse refreshes
        // LastUsedUtc and the idle reaper never sees it. Tear it down here so the caller respawns a fresh daemon instead
        // of being handed the hung endpoint forever.
        _logger.LogWarning("sd-server for model {ModelName} is wedged ({Failures} consecutive failed liveness probes); tree-killing to respawn.",
            modelName, failures);
        await RemoveProcessAsync(modelName, existing).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    ///     Runs one liveness probe bounded by <see cref="StableDiffusionRuntimeOptions.ReuseLivenessProbeTimeout" /> so a
    ///     hung daemon that accepts the socket but never answers cannot stall the reuse hot path for the whole HTTP-client
    ///     timeout. A probe that times out (the caller's own token is NOT cancelled) counts as not-responsive.
    /// </summary>
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
            // The probe exceeded its own budget (not a caller cancellation) — treat the daemon as unresponsive.
            return false;
        }
    }

    private async Task<ImageServerEndpoint> SpawnUnderGateAsync(string modelName, bool evictFirst, CancellationToken ct)
    {
        var gate = _ensureGates.GetOrAdd(modelName, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — another caller may have spawned while we waited (ensure path only).
            if (!evictFirst && _processes.TryGetValue(modelName, out var existing) && !existing.Handle.HasExited)
            {
                var reused = await TryReuseAsync(modelName, existing, ct).ConfigureAwait(false);
                if (reused is not null)
                {
                    return reused;
                }
            }

            // A stale/exited/wedged (or, on restart, the outgoing) daemon under this key is reaped before respawn (a
            // wedged one was already torn down by TryReuseAsync; RemoveProcessAsync is idempotent so the retry is a no-op).
            if (_processes.TryGetValue(modelName, out var stale))
            {
                await RemoveProcessAsync(modelName, stale).ConfigureAwait(false);
            }

            var running = await SpawnOnceAsync(modelName, ct).ConfigureAwait(false);
            return running.Endpoint;
        }
        finally
        {
            try
            {
                gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // DisposeAsync disposes the per-model ensure gates. When a dispose races a spawn that is
                // unwinding on the shutdown-linked token (this spawn holds this gate), the gate can already be disposed
                // here — the release is moot at teardown. Swallow it so the real unwind cause (the OperationCanceledException
                // from the cancelled readiness wait) surfaces to the caller instead of a leaked ObjectDisposedException.
            }
        }
    }

    /// <summary>Resolves the model file-set + backend + binary, builds args, launches, readiness-gates, and registers.</summary>
    private async Task<RunningServer> SpawnOnceAsync(string modelName, CancellationToken ct)
    {
        var parts = await _modelStore.ResolveModelPartsAsync(modelName, ct).ConfigureAwait(false);
        if (parts is not { Count: > 0 })
        {
            throw new StableDiffusionRuntimeException("The requested image model is not installed.");
        }

        var backend = await _backendSelector.SelectBackendAsync(ct).ConfigureAwait(false);
        var binary = await _binaryManager.EnsureBinaryAsync(backend, ct).ConfigureAwait(false);

        // Link the spawn/readiness window to the supervisor's shutdown token so a DisposeAsync racing this
        // spawn cancels the readiness wait — the catch below then tree-kills the launched handle instead of leaving it
        // orphaned (DisposeAsync tears down only the _processes snapshot it sees, and this spawn registers into _processes
        // only after readiness). A caller cancellation (ct) still aborts too; either source unwinds through the catch.
        using var spawnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        var spawnCt = spawnCts.Token;

        // AUD4-06: serialize the spawn-through-readiness window of a GPU-backed image load through the SAME process-wide
        // gate the llama-server supervisor uses, so an image load and an LLM load never race two --fit / free-VRAM reads.
        // The binary's OWN backend decides (a bring-your-own override may serve a different backend than the host probe
        // selected); a CPU backend bypasses. The ticket releases on ready OR any failure via the using scope. sd-server
        // has no restart loop, so an admission timeout surfaces straight to the caller.
        using var admissionTicket = binary.Backend == SdGpuBackend.Cpu
            ? null
            : await _loadAdmission.AcquireAsync(spawnCt).ConfigureAwait(false);

        var port = await AllocatePortAsync(spawnCt).ConfigureAwait(false);

        IImageServerProcessHandle? handle = null;
        try
        {
            // The binary's OWN backend drives the launch flags — a bring-your-own override may serve a different backend
            // than the host probe selected.
            var spec = ImageServerArgumentBuilder.Build(modelName,
                binary.ServerExecutablePath,
                parts,
                binary.Backend,
                port,
                _options,
                Environment.ProcessorCount);

            handle = _launcher.Launch(spec);
            _logger.LogInformation("sd-server spawned for model {ModelName} (pid {ProcessId}, port {Port}).",
                modelName, handle.ProcessId, port);

            // The readiness wait IS the model-load wait (sd-server binds only once loading completes), so the budget is
            // sized against the file-set rather than being flat — see ImageServerReadinessBudget.
            var readinessBudget = ImageServerReadinessBudget.For(parts, _options);
            var readyStartedUtc = _timeProvider.GetUtcNow();
            await WaitForReadyOrExitAsync(handle, spec.BaseAddress, readinessBudget, spawnCt).ConfigureAwait(false);
            _logger.LogInformation("sd-server ready for model {ModelName} (pid {ProcessId}) after {ElapsedMs:F0} ms.",
                modelName, handle.ProcessId, (_timeProvider.GetUtcNow() - readyStartedUtc).TotalMilliseconds);

            var endpoint = new ImageServerEndpoint(modelName, spec.BaseAddress);
            var residentLease = _runtimeActivityGate.TryAcquireResidentProcessLease()
                                ?? throw new StableDiffusionRuntimeException("The image runtime became busy before the server process could be registered.");
            var running = new RunningServer(handle, endpoint, port, _timeProvider.GetUtcNow(), residentLease);
            _processes[modelName] = running;

            // A DisposeAsync that ran while this spawn was in flight tore down only the daemons present in its
            // teardown snapshot; this one registered AFTER that snapshot, so if disposal is now observed it would be left
            // resident (orphaned). Tear it down here. The detach/kill pair (or, on a lost removal race, the concurrent
            // path that won it) owns the kill/dispose/port-release, so null the handle to keep the catch below from
            // acting on it again; the ObjectDisposedException is excluded from the error log.
            if (Volatile.Read(ref _disposed) != 0)
            {
                if (DetachProcess(modelName, running) is { } detached)
                {
                    KillDetachedProcess(detached);
                }

                handle = null;
                throw new ObjectDisposedException(nameof(ImageServerProcessSupervisor));
            }

            return running;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException and not ObjectDisposedException)
            {
                // Image spawn has no restart loop, so a readiness timeout / exit-while-loading / not-installed surfaces
                // straight to the caller — log the cause here (Error) before the sanitized message bubbles up.
                _logger.LogError(ex, "sd-server start failed for model {ModelName}.", modelName);
            }

            handle?.TreeKill();
            handle?.Dispose();
            await ReleaseReservedPortAsync(port).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Waits for the freshly launched daemon to answer <c>/sdcpp/v1/capabilities</c>, racing that against the process
    ///     exiting. sd-server binds its socket only after a successful model load, so an exit-before-ready is a
    ///     deterministic load failure: surface it immediately instead of polling a dead endpoint for the full budget.
    /// </summary>
    private async Task WaitForReadyOrExitAsync(IImageServerProcessHandle handle, Uri baseAddress, TimeSpan budget, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var readyTask = _readinessProbe.WaitForReadyAsync(baseAddress, budget, linkedCts.Token);
        var exitTask = WatchForExitAsync(handle, linkedCts.Token);

        var winner = await Task.WhenAny(readyTask, exitTask).ConfigureAwait(false);

        if (winner == exitTask && handle.HasExited)
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await SwallowCancellationAsync(readyTask).ConfigureAwait(false);
            throw new StableDiffusionRuntimeException("The image runtime exited while loading the model. The model may be incompatible with this runtime or too large for the available memory.");
        }

        await linkedCts.CancelAsync().ConfigureAwait(false);
        await SwallowCancellationAsync(exitTask).ConfigureAwait(false);

        if (!await readyTask.ConfigureAwait(false))
        {
            throw new StableDiffusionRuntimeException("The image runtime did not become ready in time.");
        }
    }

    private async Task WatchForExitAsync(IImageServerProcessHandle handle, CancellationToken ct)
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
            // Expected: this task was cancelled because the other side of the race won.
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
                await ReapIdleOnceAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ReapIdleOnceAsync()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (key, running) in _processes.ToArray())
        {
            // An EXITED process is always torn down so a dead handle never leaks, even while nominally leased.
            if (running.Handle.HasExited)
            {
                await RemoveProcessAsync(key, running).ConfigureAwait(false);
                continue;
            }

            // Not yet idle past the TTL — leave it. LastUsedUtc is stamped per ensure/reuse (not per generation step),
            // so a single long image job looks idle here while it is mid-flight.
            if (now - running.LastUsedUtc < _options.IdleTimeToLive)
            {
                continue;
            }

            // Idle past the TTL. TryBeginEvict atomically latches the daemon for eviction ONLY when no job lease is held,
            // and once latched no new lease can attach — so a generation that starts concurrently with this reap either
            // wins the lease first (TryBeginEvict then fails and we skip, reaping on a later pass) or is refused, but we
            // can never tree-kill a daemon under an active lease.
            if (running.TryBeginEvict())
            {
                _logger.LogInformation("Evicting idle sd-server for model {ModelName} (idle {IdleSeconds:F0}s past TTL {TtlSeconds:F0}s).",
                    key, (now - running.LastUsedUtc).TotalSeconds, _options.IdleTimeToLive.TotalSeconds);
                await RemoveProcessAsync(key, running).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Reserves a slot under the loaded cap (evicting an idle LRU daemon to make room when possible) and allocates a
    ///     free loopback port. The admission gate serializes the cap decision so it can never be raced past. The cap is
    ///     measured by the reserved-port count (which already includes in-flight spawns), mirroring the llama supervisor.
    /// </summary>
    private async Task<int> AllocatePortAsync(CancellationToken ct)
    {
        // Daemons detached from the table under the gate, tree-killed after it is released (see KillDetachedProcesses).
        var detached = new List<RunningServer>();
        await _admissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Drop any daemon that has already exited so its slot/port is reclaimed before the cap check.
            PruneExitedProcesses(detached);

            if (_allocatedPorts.Count >= _options.MaxLoadedProcesses && !TryEvictIdleLeastRecentlyUsed(detached))
            {
                throw CapReached();
            }

            return AllocatePort();
        }
        finally
        {
            _admissionGate.Release();

            // The gate is free BEFORE any child is killed: tree-killing a multi-GB image model is slow, and under the
            // gate it serialized every unrelated model's port allocation and release behind it. This spawn still waits
            // for its own victim to die before it returns to launch, so the VRAM the victim held is genuinely released
            // before the incoming model loads.
            KillDetachedProcesses(detached);
        }
    }

    /// <summary>
    ///     Frees a slot for a new admission by evicting the least-recently-used daemon that is not mid-generation
    ///     (caller holds the admission gate).
    ///     <para>
    ///         A daemon past its idle TTL is always preferred. When none is, an in-window but <b>unleased</b> daemon is
    ///         evicted anyway. That fallback exists because the image cap is <c>1</c>: without it the TTL — a reaper
    ///         threshold, not an admission rule — becomes a fifteen-minute lockout in which switching image models fails
    ///         outright with "the maximum number of local image models are already loaded", and the app offers no way
    ///         out. An unleased daemon has no request in flight, so evicting it costs a reload and nothing else, whereas
    ///         refusing costs the operator the feature. A leased daemon is still never a victim.
    ///     </para>
    ///     <para>
    ///         The victim is detached here — its slot and port are free the moment this returns <see langword="true" /> —
    ///         and appended to <paramref name="detached" /> for the caller to tree-kill once the gate is released.
    ///     </para>
    /// </summary>
    private bool TryEvictIdleLeastRecentlyUsed(List<RunningServer> detached)
    {
        var now = _timeProvider.GetUtcNow();
        string? victimKey = null;
        RunningServer? victim = null;
        var victimIsIdlePastTtl = false;

        foreach (var (key, running) in _processes)
        {
            // In-flight generation disqualifies a live daemon as a cap-eviction victim for the same reason the idle
            // reaper skips it: past-TTL only means "no new job started", not "not mid-generation". This is a
            // best-effort heuristic read; the atomic claim is TryBeginEvict on the chosen victim below.
            if (running.IsLeased && !running.Handle.HasExited)
            {
                continue;
            }

            var isIdlePastTtl = running.Handle.HasExited || now - running.LastUsedUtc >= _options.IdleTimeToLive;

            // A past-TTL candidate always outranks an in-window one; within the same rank, least-recently-used wins.
            if (victim is not null && (victimIsIdlePastTtl != isIdlePastTtl
                    ? !isIdlePastTtl
                    : running.LastUsedUtc >= victim.LastUsedUtc))
            {
                continue;
            }

            victimKey = key;
            victim = running;
            victimIsIdlePastTtl = isIdlePastTtl;
        }

        if (victimKey is null || victim is null)
        {
            return false;
        }

        // Atomically latch the chosen victim before tearing it down. If a generation acquired a lease on it between the
        // heuristic scan and here, TryBeginEvict fails and we admit no victim this round — the caller surfaces a
        // retryable cap error rather than tree-killing a daemon under an active lease. An EXITED victim
        // holds no real lease, so it is torn down regardless.
        if (!victim.Handle.HasExited && !victim.TryBeginEvict())
        {
            return false;
        }

        // Free the slot/port under the gate so the new admission proceeds immediately; the kill follows outside it.
        // A lost removal race (a concurrent evict/reap already detached this victim) frees no slot of OUR doing, so
        // report no admission rather than letting the cap be overrun on someone else's teardown.
        if (DetachProcess(victimKey, victim) is not { } evicted)
        {
            return false;
        }

        _logger.LogWarning("Loaded-model cap ({Cap}) reached; evicting {Idleness} sd-server for model {ModelName} to admit a new one.",
            _options.MaxLoadedProcesses, victimIsIdlePastTtl ? "idle" : "in-window but unleased", victimKey);

        detached.Add(evicted);
        return true;
    }

    private static StableDiffusionRuntimeException CapReached()
    {
        return new StableDiffusionRuntimeException("The maximum number of local image models are already loaded. Unload a model or raise the limit, then try again.");
    }

    private void PruneExitedProcesses(List<RunningServer> detached)
    {
        foreach (var (key, running) in _processes)
        {
            if (running.Handle.HasExited && DetachProcess(key, running) is { } exited)
            {
                detached.Add(exited);
            }
        }
    }

    private async Task RemoveProcessAsync(string key, RunningServer running)
    {
        // Teardown must complete even during shutdown, so it is not bound to a caller cancellation token.
        RunningServer? detached;
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            detached = DetachProcess(key, running);
        }
        finally
        {
            _admissionGate.Release();
        }

        // Killed OUTSIDE the gate so a multi-GB tree-kill does not serialize unrelated admissions — but still completed
        // before this returns, because every caller here depends on the child actually being gone: the ensure/restart
        // path reaps the outgoing daemon through this call and then immediately respawns under the same key (at the
        // default cap of one, the replacement's load must not overlap the outgoing model's VRAM), and the wedged-daemon
        // and idle-reaper paths must not leave a second child alive against the same model files.
        if (detached is not null)
        {
            KillDetachedProcess(detached);
        }
    }

    /// <summary>
    ///     Removes a daemon from the table and releases its port reservation — everything that makes the slot available
    ///     to the next admission — WITHOUT touching the child. Caller holds the admission gate. Returns the daemon when
    ///     this call won the removal race (the caller then owes it a <see cref="KillDetachedProcess" />), or
    ///     <see langword="null" /> when a concurrent path already removed it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         INVARIANT: the port reservation is dropped here, before the child is killed, so the reservation set
    ///         (which is what bounds the loaded-model CAP) never counts a daemon that is on its way out. That does not
    ///         hand the next spawn a port the dying child still holds: <see cref="AllocatePort" /> bind-probes every
    ///         candidate (<see cref="IsPortFree" />) and skips one that is still bound. The bind probe was always the
    ///         real guard — <c>TreeKill</c> returns before the OS reclaims the socket, so releasing the port after the
    ///         kill never proved availability either.
    ///     </para>
    ///     <para>
    ///         The resident-process lease is deliberately NOT released here. Unlike the slot and the port it has no
    ///         equivalent of the bind probe behind it: it is what holds off an exclusive runtime mutation
    ///         (<see cref="IImageRuntimeActivityGate.TryAcquireMutationReservation" /> admits only at zero resident
    ///         processes), and a child that has been detached but not yet killed still has the model files open. It is
    ///         released in <see cref="KillDetachedProcess" /> instead, once the child is actually down.
    ///     </para>
    /// </remarks>
    private RunningServer? DetachProcess(string key, RunningServer running)
    {
        if (!_processes.TryRemove(new KeyValuePair<string, RunningServer>(key, running)))
        {
            return null; // Already removed by a concurrent path.
        }

        ReleasePort(running.Port);
        return running;
    }

    /// <summary>
    ///     Tree-kills + disposes a detached daemon, then releases the resident-process lease it held. Never called while
    ///     the admission gate is held.
    /// </summary>
    private static void KillDetachedProcess(RunningServer running)
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
    ///     Tree-kills every daemon detached during an admission decision. A teardown failure is logged, never rethrown:
    ///     the admission it trails has already succeeded (or failed with its own cap error), and turning a kill failure
    ///     into the caller's exception would both mask that error and skip the remaining victims.
    /// </summary>
    private void KillDetachedProcesses(List<RunningServer> detached)
    {
        foreach (var running in detached)
        {
            try
            {
                KillDetachedProcess(running);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Tearing down an evicted sd-server (pid {ProcessId}) failed; its slot and port were already released.",
                    running.Handle.ProcessId);
            }
        }
    }

    private int AllocatePort()
    {
        for (var port = _options.PortRangeStart; port <= _options.PortRangeEnd; port++)
        {
            if (_allocatedPorts.Contains(port) || !IsPortFree(port))
            {
                continue;
            }

            _allocatedPorts.Add(port);
            return port;
        }

        throw new StableDiffusionRuntimeException("No free local port is available for the image runtime.");
    }

    private void ReleasePort(int port)
    {
        _allocatedPorts.Remove(port);
    }

    private async Task ReleaseReservedPortAsync(int port)
    {
        try
        {
            await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // A spawn cancelled by DisposeAsync (its readiness is linked to the shutdown token) unwinds
            // through here to release its reserved port, but DisposeAsync may already have disposed the admission gate.
            // The disposal teardown reaps every registered daemon's port anyway, and no concurrent allocator remains, so
            // dropping this release is safe — never surface an ObjectDisposedException from the shutdown unwind.
            return;
        }

        try
        {
            ReleasePort(port);
        }
        finally
        {
            _admissionGate.Release();
        }
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

    /// <summary>A live, registered daemon and its last-used timestamp (drives idle-TTL eviction).</summary>
    private sealed class RunningServer(
        IImageServerProcessHandle handle,
        ImageServerEndpoint endpoint,
        int port,
        DateTimeOffset startedUtc,
        IImageRuntimeActivityLease residentLease)
    {
        private long _lastUsedTicks = startedUtc.UtcTicks;

        // Seeded to the spawn time so a freshly-ready daemon is not re-probed until one full interval has passed.
        private long _lastLivenessProbeTicks = startedUtc.UtcTicks;
        private int _consecutiveLivenessFailures;

        // Lease/eviction state, mutated only by atomic CAS: >= 0 is the count of in-flight generations
        // holding this daemon; -1 is a terminal "evicting" latch set by the idle reaper / cap evictor. A new lease
        // (TryAcquireJob) and an eviction decision (TryBeginEvict) therefore transition the SAME word, so they can never
        // both win — closing the window where the old plain increment could be granted after the reaper had read
        // "no active jobs" but before it tree-killed the daemon.
        private int _leaseState;

        public IImageServerProcessHandle Handle { get; } = handle;

        public IImageRuntimeActivityLease ResidentLease { get; } = residentLease;

        /// <summary>Whether a generation currently leases this daemon — best-effort read for the evictor's victim heuristic; the atomic claim is <see cref="TryBeginEvict" />.</summary>
        public bool IsLeased => Volatile.Read(ref _leaseState) > 0;

        public ImageServerEndpoint Endpoint { get; } = endpoint;

        public int Port { get; } = port;

        public DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

        public void MarkUsed(DateTimeOffset now)
        {
            Interlocked.Exchange(ref _lastUsedTicks, now.UtcTicks);
        }

        /// <summary>
        ///     Atomically claims the right to run the reuse-path liveness probe: succeeds (advancing the probe clock to
        ///     <paramref name="now" />) only when at least <paramref name="interval" /> has elapsed since the last claim.
        ///     Serializes probes across concurrent reuses so at most one HTTP probe runs per daemon per interval.
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

        /// <summary>Resets the consecutive-failure count after a successful liveness probe.</summary>
        public void ResetLivenessFailures()
        {
            Interlocked.Exchange(ref _consecutiveLivenessFailures, value: 0);
        }

        /// <summary>Records a failed liveness probe and returns the new consecutive-failure count.</summary>
        public int RecordLivenessFailure()
        {
            return Interlocked.Increment(ref _consecutiveLivenessFailures);
        }

        /// <summary>
        ///     Atomically registers an in-flight generation against this daemon, unless it is already latched for
        ///     eviction. Returns <see langword="false" /> when an idle reaper / cap evictor has begun tearing it down,
        ///     in which case the caller must proceed leaseless rather than over a to-be-killed daemon.
        /// </summary>
        public bool TryAcquireJob()
        {
            while (true)
            {
                var state = Volatile.Read(ref _leaseState);
                if (state < 0)
                {
                    return false; // Evicting: no new lease may attach.
                }

                if (Interlocked.CompareExchange(ref _leaseState, state + 1, state) == state)
                {
                    return true;
                }
            }
        }

        /// <summary>Releases a previously-acquired job lease.</summary>
        public void ReleaseJob()
        {
            // Only ever called by a holder that won TryAcquireJob (state was >= 1), so this can never touch the evicting
            // latch: TryBeginEvict requires state == 0 and so cannot have fired while this lease was held.
            Interlocked.Decrement(ref _leaseState);
        }

        /// <summary>
        ///     Atomically latches this daemon as evicting, but only when no job lease is currently held. Once latched,
        ///     <see cref="TryAcquireJob" /> refuses, so the caller may tear the daemon down without cutting off a
        ///     generation that began after a plain "no active jobs" read. Returns <see langword="false" />
        ///     when a lease is active — the daemon must then be left alone and reaped on a later pass.
        /// </summary>
        public bool TryBeginEvict()
        {
            return Interlocked.CompareExchange(ref _leaseState, value: -1, comparand: 0) == 0;
        }
    }

    /// <summary>
    ///     Active-job lease over a <see cref="RunningServer" />. Holds the daemon against idle reaping / LRU
    ///     eviction for the duration of one generation; <see cref="Touch" /> refreshes its last-used clock each poll.
    /// </summary>
    private sealed class ImageJobLease : IImageServerJobLease
    {
        private readonly RunningServer _server;
        private readonly TimeProvider _timeProvider;
        private int _disposed;

        public ImageJobLease(RunningServer server, TimeProvider timeProvider)
        {
            _server = server;
            _timeProvider = timeProvider;
        }

        public void Touch()
        {
            _server.MarkUsed(_timeProvider.GetUtcNow());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
            {
                _server.ReleaseJob();
            }
        }
    }
}
