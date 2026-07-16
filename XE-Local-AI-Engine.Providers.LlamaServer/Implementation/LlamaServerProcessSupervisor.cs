namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;

/// <summary>
///     Default <see cref="ILlamaServerProcessSupervisor" />. Owns every
///     <c>llama-server</c> child process: reuse-or-spawn per <c>(model, role)</c> with a single-flight gate, health
///     probe on start, restart-on-crash with a backoff cap, localhost port allocation with collision-retry, shared
///     idle-TTL + loaded-cap eviction + a background reaper, per-OS tree-kill teardown, and the hybrid
///     attach-to-external-endpoint path. Singleton; disposes every owned process on shutdown.
/// </summary>
/// <remarks>
///     <para>
///         All process launch / tree-kill / health I/O is delegated to the <see cref="ILlamaServerProcessLauncher" />
///         and <see cref="ILlamaServerHealthProbe" /> seams so this lifecycle logic is unit-tested without real
///         processes or network. The launch argument vector — including the mandatory <c>--jinja</c> (chat) and
///         non-<c>none</c> <c>--pooling</c> (embedding) flags — is built here by <see cref="BuildLaunchSpec" />.
///     </para>
/// </remarks>
public sealed class LlamaServerProcessSupervisor : ILlamaServerProcessSupervisor, IAsyncDisposable
{
    private const string NonRetryableMarker = "LlamaServer.NonRetryable";

    // Flags a readiness TIMEOUT (process alive but slow) so the restart loop retries it at most
    // MaxReadinessTimeoutRetries times instead of the full MaxRestartAttempts — a deterministically slow/large model
    // is not a transient crash, so retrying it many times only multiplies the kill/reload thrash.
    private const string ReadinessTimeoutMarker = "LlamaServer.ReadinessTimeout";

    /// <summary>Poll cadence for observing that a freshly spawned process exited during its readiness wait.</summary>
    private static readonly TimeSpan ProcessExitPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Poll cadence for observing that in-flight inference leases have drained during a graceful eject.</summary>
    private static readonly TimeSpan LeaseDrainPollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Base delay between crash-restart attempts; grows linearly per attempt.</summary>
    private static readonly TimeSpan RestartBackoffStep = TimeSpan.FromMilliseconds(250);

    // Guards the loaded-cap admission decision + port-set mutation so the cap can never be exceeded by a race.
    private readonly SemaphoreSlim _admissionGate = new(initialCount: 1, maxCount: 1);
    private readonly HashSet<int> _allocatedPorts = [];
    private readonly ILlamaCppBinaryManager _binaryManager;

    // Single-flight ensure-running gate, one semaphore per (model, role) key. Held only for the short reuse/decision
    // section, NOT for the whole spawn — the spawn itself runs detached (see _inflightSpawns).
    private readonly ConcurrentDictionary<ProcessKey, SemaphoreSlim> _ensureGates = new();

    // The in-flight, DETACHED spawn task per (model, role) key. A caller AWAITS this task but never owns its lifetime:
    // a caller cancelling its own wait does not abort the model load, which continues under its own readiness deadline
    // and leaves the model warm for the next send (the deliberate design — a user who cancels before the first token
    // does not throw away the load everyone behind them is waiting on). Exactly one runs per key at a time; it removes
    // itself on completion (success or failure) so the next ensure retries fresh.
    private readonly ConcurrentDictionary<ProcessKey, Task<RunningProcess>> _inflightSpawns = new();
    private readonly LlamaServerExternalEndpointOptions _externalEndpoints;
    private readonly ILlamaServerHealthProbe _healthProbe;
    private readonly ILlamaServerLaunchPolicy _launchPolicy;
    private readonly ILogger<LlamaServerProcessSupervisor> _logger;
    private readonly ILlamaServerProcessLauncher _launcher;
    private readonly IGgufModelStore _modelStore;
    private readonly LlamaServerSupervisorOptions _options;
    private readonly IInferenceProfileResolver _profileResolver;

    // One running process per (model, role) key.
    private readonly ConcurrentDictionary<ProcessKey, RunningProcess> _processes = new();
    private readonly Task _reaperLoop;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TimeProvider _timeProvider;
    private readonly IGpuVariantSelector _variantSelector;

    // AUD4-06: the process-wide GPU-load admission gate. GPU-backed spawns serialize their spawn-through-readiness window
    // through it (shared with the image supervisor) so two --fit loads never read the same free-VRAM snapshot at once.
    private readonly IGpuModelLoadAdmission _loadAdmission;
    private int _disposed;

    /// <summary>
    ///     Creates a supervisor over the supplied collaborators. The reaper loop starts immediately. Constructed via DI
    ///     (same-assembly factory) or in tests — the launcher/health-probe seams are internal, so the ctor is internal.
    /// </summary>
    internal LlamaServerProcessSupervisor(ILlamaCppBinaryManager binaryManager,
        IGpuVariantSelector variantSelector,
        IGgufModelStore modelStore,
        ILlamaServerProcessLauncher launcher,
        ILlamaServerHealthProbe healthProbe,
        LlamaServerSupervisorOptions options,
        IInferenceProfileResolver profileResolver,
        ILlamaServerLaunchPolicy launchPolicy,
        LlamaServerExternalEndpointOptions? externalEndpoints = null,
        TimeProvider? timeProvider = null,
        ILogger<LlamaServerProcessSupervisor>? logger = null,
        IGpuModelLoadAdmission? loadAdmission = null)
    {
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        _launchPolicy = launchPolicy ?? throw new ArgumentNullException(nameof(launchPolicy));
        _externalEndpoints = externalEndpoints ?? new LlamaServerExternalEndpointOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<LlamaServerProcessSupervisor>.Instance;

        // Absent a wired gate (a provider-only host / test), default to the no-op floor so GPU-load serialization is
        // simply off — the composition root injects the real, metric-emitting singleton shared with the image supervisor.
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
            TeardownProcess(key, running);
        }

        _admissionGate.Dispose();
        foreach (var gate in _ensureGates.Values)
        {
            gate.Dispose();
        }

        _shutdownCts.Dispose();
    }

    /// <inheritdoc />
    public async Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Hybrid attach: a configured external endpoint short-circuits spawn/supervision entirely.
        var external = _externalEndpoints.Resolve(modelName, role);
        if (external is not null)
        {
            return new LlamaServerEndpoint(modelName, role, external);
        }

        var key = new ProcessKey(modelName, role);

        // Fast path: an already-running, live process is reused without taking the spawn gate — subject to a
        // rate-limited liveness probe so a wedged (alive but unresponsive) process is respawned instead of handed out.
        if (_processes.TryGetValue(key, out var existing) && !existing.Handle.HasExited)
        {
            var reused = await TryReuseAsync(key, existing, ct).ConfigureAwait(false);
            if (reused is not null)
            {
                return reused;
            }
        }

        // Decide (under the single-flight gate, held only briefly) between a reuse and joining/starting the DETACHED
        // spawn, then await the spawn WITHOUT binding its lifetime to this caller's token.
        var decision = await DecideEnsureAsync(key, ct).ConfigureAwait(false);
        if (decision.Reused is { } reusedEndpoint)
        {
            return reusedEndpoint;
        }

        var running = await AwaitDetachedSpawnAsync(decision.SpawnTask!, ct).ConfigureAwait(false);
        return running.Endpoint;
    }

    /// <summary>
    ///     The single-flight decision, taken under the per-key gate held only briefly: reuse a now-registered process,
    ///     or return the shared DETACHED spawn task (creating it if none is in flight). The gate is released before the
    ///     caller awaits the spawn, so a caller cancelling its wait cannot leave the gate held — and because the spawn is
    ///     the shared <see cref="_inflightSpawns" /> task, concurrent callers still spawn exactly once.
    /// </summary>
    private async Task<EnsureDecision> DecideEnsureAsync(ProcessKey key, CancellationToken ct)
    {
        var gate = _ensureGates.GetOrAdd(key, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — a detached spawn may have registered a live process while we waited (it
            // registers into _processes before removing itself from _inflightSpawns, so a reuse here never misses it).
            if (_processes.TryGetValue(key, out var existing) && !existing.Handle.HasExited)
            {
                var reused = await TryReuseAsync(key, existing, ct).ConfigureAwait(false);
                if (reused is not null)
                {
                    return new EnsureDecision(reused, SpawnTask: null);
                }
            }

            // A crashed/exited/wedged process lingering under this key is reaped before respawn (a wedged one was already
            // torn down by TryReuseAsync; RemoveProcessAsync is idempotent on the instance so the extra call is a no-op).
            if (existing is not null)
            {
                await RemoveProcessAsync(key, existing).ConfigureAwait(false);
            }

            // Join the in-flight detached spawn or start one. GetOrAdd runs its factory at most once here because we hold
            // the gate, so two callers never start two spawns for the same key.
            var spawnTask = _inflightSpawns.GetOrAdd(key, StartDetachedSpawn);
            return new EnsureDecision(Reused: null, spawnTask);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     Starts the detached spawn for <paramref name="key" /> on its OWN lifetime (the shutdown token, never a
    ///     caller's), so the load runs to completion regardless of whether a waiting caller cancels. The spawn registers
    ///     the process into <see cref="_processes" /> (inside <see cref="SpawnCoreAsync" />) BEFORE this task removes
    ///     itself from <see cref="_inflightSpawns" />, so a concurrent reuse-check never sees "neither in-flight nor
    ///     registered". On failure the spawn tears down its own half-started child and the in-flight entry is dropped so
    ///     the next ensure retries fresh.
    /// </summary>
    private Task<RunningProcess> StartDetachedSpawn(ProcessKey key)
    {
        var completion = new TaskCompletionSource<RunningProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = completion.Task;

        // Guarantee a faulted detached spawn is observed even if every waiting caller has abandoned its wait (e.g. all
        // callers cancelled, or the spawn is cancelled on shutdown), so it can never surface as an UnobservedTaskException.
        // Awaiting callers still receive the exception — this continuation only marks it observed.
        _ = task.ContinueWith(static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _ = Task.Run(async () =>
        {
            try
            {
                var running = await SpawnWithRestartAsync(key, _shutdownCts.Token).ConfigureAwait(false);
                completion.SetResult(running);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                // Remove THIS task (key+value) so a concurrent GetOrAdd that already captured a newer task is untouched.
                _inflightSpawns.TryRemove(new KeyValuePair<ProcessKey, Task<RunningProcess>>(key, task));
            }
        });

        return task;
    }

    /// <summary>
    ///     Awaits the shared detached spawn with the CALLER's token, but never cancels the spawn itself: a cancelled
    ///     caller merely abandons its wait (its <see cref="OperationCanceledException" /> propagates) while the load
    ///     continues in the background and the model becomes warm for the next send. INVARIANT: caller cancellation
    ///     never aborts an in-flight model load.
    /// </summary>
    private static Task<RunningProcess> AwaitDetachedSpawnAsync(Task<RunningProcess> spawnTask, CancellationToken ct)
    {
        return spawnTask.WaitAsync(ct);
    }

    /// <summary>The outcome of <see cref="DecideEnsureAsync" />: a reused endpoint XOR the shared detached spawn task.</summary>
    private readonly record struct EnsureDecision(LlamaServerEndpoint? Reused, Task<RunningProcess>? SpawnTask);

    /// <summary>
    ///     Reuse decision for an already-registered, not-yet-exited process: hands back its endpoint when it is healthy
    ///     enough, or returns <see langword="null" /> after tearing it down when it is wedged (alive but unresponsive to
    ///     <see cref="LlamaServerSupervisorOptions.MaxReuseLivenessFailures" /> consecutive liveness probes). The liveness
    ///     probe is rate-limited to at most one per <see cref="LlamaServerSupervisorOptions.ReuseLivenessProbeInterval" />
    ///     per process, so the hot path stays cheap — between probes the endpoint is reused with no HTTP.
    /// </summary>
    private async Task<LlamaServerEndpoint?> TryReuseAsync(ProcessKey key, RunningProcess existing, CancellationToken ct)
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

        // A failed probe: count it. Under the threshold the process is still handed out — a single transient probe
        // failure must never tear down a busy server. At/above the threshold it is treated as wedged.
        var failures = existing.RecordLivenessFailure();
        if (failures < _options.MaxReuseLivenessFailures)
        {
            existing.MarkUsed(_timeProvider.GetUtcNow());
            return existing.Endpoint;
        }

        // Wedged: the process is alive but has failed the liveness probe N consecutive times, so every reuse refreshes
        // LastUsedUtc and the idle reaper never sees it. Tear it down here so the caller respawns a fresh server instead
        // of being handed the hung endpoint forever.
        _logger.LogWarning("llama-server for model {ModelName} role {Role} is wedged ({Failures} consecutive failed liveness probes); tree-killing to respawn.",
            key.ModelName, key.Role, failures);
        await RemoveProcessAsync(key, existing).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    ///     Runs one liveness probe bounded by <see cref="LlamaServerSupervisorOptions.ReuseLivenessProbeTimeout" /> so a
    ///     hung server that accepts the socket but never answers cannot stall the reuse hot path for the whole HTTP-client
    ///     timeout. A probe that times out (the caller's own token is NOT cancelled) counts as not-responsive.
    /// </summary>
    private async Task<bool> ProbeResponsiveWithTimeoutAsync(Uri baseAddress, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.ReuseLivenessProbeTimeout);
        try
        {
            return await _healthProbe.CheckResponsiveAsync(baseAddress, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The probe exceeded its own budget (not a caller cancellation) — treat the server as unresponsive.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task EvictAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        var key = new ProcessKey(modelName, role);
        if (_processes.TryGetValue(key, out var running))
        {
            await RemoveProcessAsync(key, running).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var key = new ProcessKey(modelName, role);
        if (!_processes.TryGetValue(key, out var target) || target.Handle.HasExited)
        {
            // Nothing live to eject. Reap a lingering dead entry so its slot/port frees, then report an idempotent no-op.
            if (target is not null)
            {
                await RemoveProcessAsync(key, target).ConfigureAwait(false);
            }

            return LlamaServerEjectOutcome.NotRunning;
        }

        // Mark evicting: new inference leases are refused (TryAcquireInferenceLease returns null) so the active-lease
        // count can only fall while we drain. The process stays registered and reusable until we tear it down or give up.
        target.MarkEvicting();
        _logger.LogInformation("Operator eject requested for model {ModelName} role {Role} (force: {Force}); draining {ActiveLeases} in-flight request(s).",
            key.ModelName, key.Role, force, target.ActiveLeases);

        var drained = await DrainLeasesAsync(target, ct).ConfigureAwait(false);
        if (drained)
        {
            await RemoveProcessAsync(key, target).ConfigureAwait(false);
            _logger.LogInformation("Operator eject completed for model {ModelName} role {Role}: drained and torn down.", key.ModelName, key.Role);
            return LlamaServerEjectOutcome.Ejected;
        }

        if (force)
        {
            // Force: tear down despite in-flight work. Mark ejected FIRST so the interrupted request's leaseholder can
            // classify the resulting connection failure as an operator eject rather than a generic provider drop.
            target.MarkEjected();
            await RemoveProcessAsync(key, target).ConfigureAwait(false);
            _logger.LogWarning("Operator eject FORCED for model {ModelName} role {Role}: {ActiveLeases} in-flight request(s) interrupted.",
                key.ModelName, key.Role, target.ActiveLeases);
            return LlamaServerEjectOutcome.ForcedWhileBusy;
        }

        // Busy and not forced: never kill silently. Leave the process running and usable, and report that the eject
        // could not complete safely so the caller can decide (retry / force).
        target.ClearEvicting();
        _logger.LogInformation("Operator eject for model {ModelName} role {Role} did not complete: still busy after the drain window; left running.", key.ModelName, key.Role);
        return LlamaServerEjectOutcome.TimedOutStillBusy;
    }

    /// <inheritdoc />
    public ILlamaServerInferenceLease? TryAcquireInferenceLease(string modelName, ModelRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var key = new ProcessKey(modelName, role);
        if (!_processes.TryGetValue(key, out var running) || running.Handle.HasExited || running.IsEvicting)
        {
            return null;
        }

        // Acquire, then RE-CHECK evicting/exited: an eject that flipped the flag between the guard above and here must
        // not gain a lease that would extend its drain — release and refuse so the caller proceeds leaseless.
        running.AcquireLease();
        if (running.IsEvicting || running.Handle.HasExited)
        {
            running.ReleaseLease();
            return null;
        }

        return new InferenceLease(running);
    }

    /// <summary>
    ///     Waits (bounded by <see cref="LlamaServerSupervisorOptions.EjectDrainTimeout" />) for a process's active
    ///     inference leases to drain to zero. Returns <see langword="true" /> when drained within the window (an idle
    ///     process returns immediately), <see langword="false" /> when the window elapsed with work still in flight. The
    ///     drain window is real-time bounded (not the injected clock) since it is an actual wall-clock wait; a caller
    ///     cancellation propagates as an <see cref="OperationCanceledException" />.
    /// </summary>
    private async Task<bool> DrainLeasesAsync(RunningProcess running, CancellationToken ct)
    {
        if (running.ActiveLeases == 0)
        {
            return true;
        }

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainCts.CancelAfter(_options.EjectDrainTimeout);
        try
        {
            while (running.ActiveLeases > 0)
            {
                await Task.Delay(LeaseDrainPollInterval, drainCts.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The drain window elapsed with work still in flight (not a caller cancellation).
            return running.ActiveLeases == 0;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct)
    {
        // Snapshot the current processes and probe each one's liveness for the diagnostics surface.
        var snapshot = _processes.ToArray();
        return CheckHealthCoreAsync(snapshot, ct);
    }

    /// <inheritdoc />
    public int CountRunningProcesses()
    {
        // Hot-path count: a synchronous in-memory read of the process table with NO health/HTTP probe. Only handles that
        // have not exited count; the idle reaper removes dead entries, but a just-crashed handle may linger until then.
        var count = 0;
        foreach (var (_, running) in _processes)
        {
            if (!running.Handle.HasExited)
            {
                count++;
            }
        }

        return count;
    }

    /// <inheritdoc />
    public LlamaServerRuntimeInfo? GetRuntimeInfo(string modelName, ModelRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // Synchronous in-memory read (no HTTP): the effective context was captured once after readiness. Null when the
        // process is not running, has exited, or /props did not report a usable value.
        var key = new ProcessKey(modelName, role);
        if (_processes.TryGetValue(key, out var running)
            && !running.Handle.HasExited
            && running.EffectiveContextTokens is { } effectiveContext)
        {
            return new LlamaServerRuntimeInfo(effectiveContext);
        }

        return null;
    }

    private async Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthCoreAsync(KeyValuePair<ProcessKey, RunningProcess>[] snapshot,
        CancellationToken ct)
    {
        var healths = new List<LlamaServerProcessHealth>(snapshot.Length);
        foreach (var (key, running) in snapshot)
        {
            if (running.Handle.HasExited)
            {
                healths.Add(new LlamaServerProcessHealth(key.ModelName, key.Role, IsResponsive: false, "Process has exited."));
                continue;
            }

            var responsive = await _healthProbe.CheckResponsiveAsync(running.Endpoint.BaseAddress, ct).ConfigureAwait(false);
            healths.Add(new LlamaServerProcessHealth(key.ModelName,
                key.Role,
                responsive,
                responsive ? "Responsive." : "Not responding to health probe."));
        }

        return healths;
    }

    /// <summary>
    ///     Spawns the process for <paramref name="key" />, retrying on a failed start up to the restart cap with a
    ///     linear backoff; exceeding the cap surfaces a sanitized <see cref="LlamaRuntimeException" />.
    /// </summary>
    private async Task<RunningProcess> SpawnWithRestartAsync(ProcessKey key, CancellationToken ct)
    {
        Exception? lastError = null;
        var readinessTimeoutRetries = 0;
        for (var attempt = 0; attempt < _options.MaxRestartAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(RestartBackoffStep * attempt, _timeProvider, ct).ConfigureAwait(false);
            }

            try
            {
                return await SpawnOnceAsync(key, ct).ConfigureAwait(false);
            }
            catch (LlamaRuntimeException ex) when (ex.Data.Contains(NonRetryableMarker))
            {
                // Deterministic failures (cap reached, model not installed, no free port, crash-on-load) are policy
                // outcomes, not transient crashes — surface them as-is instead of burning retries on a guaranteed re-failure.
                _logger.LogError(ex, "llama-server start failed for model {ModelName} role {Role}: {Reason}",
                    key.ModelName, key.Role, ex.Message);
                throw;
            }
            catch (LlamaRuntimeException ex) when (ex.Data.Contains(ReadinessTimeoutMarker))
            {
                // A readiness TIMEOUT (process alive but slow to load) is not a transient crash: retrying it many times
                // just multiplies the kill/reload thrash (the audited ~6 min stall). Retry it at most
                // MaxReadinessTimeoutRetries times — independent of MaxRestartAttempts — then surface the classified
                // "did not become ready" failure.
                lastError = ex;
                readinessTimeoutRetries++;
                _logger.LogWarning(ex, "llama-server readiness timed out for model {ModelName} role {Role} (readiness-timeout attempt {ReadinessAttempt}; {MaxReadinessRetries} retry(ies) allowed).",
                    key.ModelName, key.Role, readinessTimeoutRetries, _options.MaxReadinessTimeoutRetries);

                if (readinessTimeoutRetries > _options.MaxReadinessTimeoutRetries)
                {
                    _logger.LogError(ex, "llama-server did not become ready for model {ModelName} role {Role} after {ReadinessAttempts} readiness attempt(s); not retrying further.",
                        key.ModelName, key.Role, readinessTimeoutRetries);
                    throw;
                }
            }
            catch (GpuModelLoadAdmissionTimeoutException ex)
            {
                // A bounded GPU-load admission wait elapsed (another model load did not become ready in time). It is not
                // a transient crash, so surface its sanitized message immediately rather than burning restart attempts
                // re-queuing behind a still-contended gate.
                _logger.LogError(ex, "llama-server spawn for model {ModelName} role {Role} could not acquire GPU-load admission in time.",
                    key.ModelName, key.Role);
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        // Retries exhausted: surface (and now log) the LAST underlying cause, which the sanitized wrapper otherwise hides.
        _logger.LogError(lastError, "llama-server failed to start for model {ModelName} role {Role} after {Attempts} attempt(s).",
            key.ModelName, key.Role, _options.MaxRestartAttempts);
        throw new LlamaRuntimeException("The local model runtime failed to start after several attempts. Check available memory and try again.",
            lastError ?? new InvalidOperationException("Spawn failed."));
    }

    /// <summary>
    ///     One normal spawn attempt: admit under the cap, allocate a port, launch, health-probe, register. The launch
    ///     args come from the profile resolver (frozen-profile replay or explore-mode auto-fit) for this
    ///     <c>(model, role, backend)</c>.
    /// </summary>
    private Task<RunningProcess> SpawnOnceAsync(ProcessKey key, CancellationToken ct)
    {
        // The resolver is awaited inside the core (after variant selection, before admission) exactly as before — a
        // slow profile read never stalls admission for other keys. No startup capture, no forced --metrics. The launch
        // policy (deterministic -c, GPU KV/FA, CPU threads) applies to this normal serving path.
        return SpawnCoreAsync(key,
            (variant, c) => _profileResolver.ResolveAsync(key.ModelName, key.Role, variant, c),
            startupCapture: null,
            ensureMetrics: false,
            applyLaunchPolicy: true,
            ct);
    }

    /// <summary>
    ///     Shared spawn core for both the resolver-driven normal path and the explicit-args operator profiling path:
    ///     resolve the model file + variant + binary, obtain the launch args via <paramref name="resolveArgs" /> BEFORE
    ///     taking the admission gate, then admit under the cap, allocate a port, launch, health-probe, and register.
    /// </summary>
    /// <remarks>
    ///     The <paramref name="resolveArgs" /> delegate is awaited at the same point the profile resolver used to be, so
    ///     admission ordering and the "a slow profile read never stalls admission" invariant are unchanged. When
    ///     <paramref name="startupCapture" /> and <paramref name="ensureMetrics" /> are both their normal-path defaults
    ///     (<see langword="null" /> / <see langword="false" />) the built spec is identical to the legacy spawn.
    /// </remarks>
    private async Task<RunningProcess> SpawnCoreAsync(ProcessKey key,
        Func<GpuVariant, CancellationToken, Task<ResolvedLaunchArguments>> resolveArgs,
        Action<string>? startupCapture,
        bool ensureMetrics,
        bool applyLaunchPolicy,
        CancellationToken ct)
    {
        var modelFilePath = await _modelStore.ResolveModelFilePathAsync(key.ModelName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modelFilePath))
        {
            throw NonRetryable("The requested model is not installed.");
        }

        // AUD4-09: the cold-start readiness deadline scales with the on-disk model size — a large model loads
        // proportionally slower, so a fixed constant would kill and retry it before it can finish (the audited hang). A
        // missing/unreadable size (0) falls back to the base timeout.
        var readinessTimeout = _options.ResolveReadinessTimeout(TryGetFileSizeBytes(modelFilePath));

        // A chat-role draft-* speculative mode needs its draft GGUF present before launch — a missing file would
        // otherwise start a server that dies cryptically. Deterministic misconfiguration → non-retryable (mirrors the
        // model-not-installed guard above). ngram-* and disabled modes never reach this (IsDraftMode is false).
        // The operator selects a draft model by NAME (installed chat model); resolve it to its on-disk GGUF the same way
        // the target model is resolved above so the effective launch args carry a real path. An explicit path override
        // (SpeculativeDraftModelPath), when set, wins and skips resolution.
        var speculative = _options.Speculative;
        if (key.Role == ModelRole.Chat && speculative.IsDraftMode)
        {
            var draftModelPath = speculative.DraftModelPath;
            if (string.IsNullOrWhiteSpace(draftModelPath) && !string.IsNullOrWhiteSpace(_options.SpeculativeDraftModelName))
            {
                draftModelPath = await _modelStore.ResolveModelFilePathAsync(_options.SpeculativeDraftModelName, ct).ConfigureAwait(false);
                speculative = speculative with
                {
                    DraftModelPath = draftModelPath
                };
            }

            if (string.IsNullOrWhiteSpace(draftModelPath) || !File.Exists(draftModelPath))
            {
                throw NonRetryable("Speculative decoding is set to a draft model, but the configured draft model file was not found. Check the draft model or disable speculative decoding.");
            }
        }

        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        var binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);

        // Resolve the launch args (frozen-profile replay or explore-mode auto-fit, or operator-supplied profiling args)
        // for this (model, role, backend) BEFORE taking the admission gate, so a slow profile read never stalls
        // admission for other keys.
        var resolved = await resolveArgs(variant, ct).ConfigureAwait(false);

        // AUD4-06: serialize the spawn-through-readiness window of GPU-backed loads process-wide (shared with the image
        // supervisor) so two --fit loads never read the same free-VRAM snapshot at once and oversubscribe the device.
        // CPU loads bypass — they do not contend for VRAM. The gate is acquired here (after variant selection + arg
        // resolution, immediately before the admission cap decision that may evict an idle process to free VRAM) so the
        // freed VRAM is seen only by THIS load's --fit; the ticket releases on ready OR any failure via the using scope,
        // and the next waiter then proceeds with a fresh free-VRAM read (the re-evaluation). Because this core runs
        // under the detached-spawn/shutdown token (not the first caller's), a caller cancelling its wait never leaves
        // the gate held. The ticket deliberately spans BOTH launch-plan attempts below (optimized + safe retry) — the
        // retry is part of the same load window, and another load interleaving between the attempts would re-race the
        // free-VRAM read the gate exists to serialize.
        using var admissionTicket = variant == GpuVariant.Cpu
            ? null
            : await _loadAdmission.AcquireAsync(ct).ConfigureAwait(false);

        // AUD4-02/05/17: the central launch policy fills in the deterministic context (-c), the GPU KV-cache
        // quantization + flash-attention optimization, and the CPU thread policy the audited launch defaults omitted.
        // Operator profiling spawns bypass it (applyLaunchPolicy: false) so the supplied args ARE the experiment.
        var planCandidates = await BuildLaunchPlanCandidatesAsync(key, variant, resolved, applyLaunchPolicy, ct).ConfigureAwait(false);

        Exception? optimizedFailure = null;
        for (var attempt = 0; attempt < planCandidates.Count; attempt++)
        {
            var candidate = planCandidates[attempt];
            var isSafeRetry = attempt > 0;
            var port = await AdmitAndAllocatePortAsync(ct).ConfigureAwait(false);

            ILlamaServerProcessHandle? handle = null;
            try
            {
                var spec = BuildLaunchSpec(key, binary.ServerExecutablePath, modelFilePath, port, variant, resolved,
                    _options.ChatCacheReuse, speculative, candidate);

                // Benchmark replay spawns need /metrics (explore already carries --metrics); only append when missing.
                if (ensureMetrics && !spec.Arguments.Contains("--metrics", StringComparer.Ordinal))
                {
                    spec = spec with
                    {
                        Arguments = [.. spec.Arguments, "--metrics"]
                    };
                }

                // Operator profiling spawns capture both pipes; the normal path leaves the sink null (spec unchanged).
                if (startupCapture is not null)
                {
                    spec = spec with
                    {
                        StartupCapture = startupCapture
                    };
                }

                handle = _launcher.Launch(spec);
                _logger.LogInformation("llama-server spawned for model {ModelName} role {Role} (pid {ProcessId}, port {Port}){LaunchPlan}.",
                    key.ModelName, key.Role, handle.ProcessId, port, DescribeLaunchPlan(candidate));

                var readyStartedUtc = _timeProvider.GetUtcNow();
                await WaitForReadyOrExitAsync(handle, spec.BaseAddress, readinessTimeout, ct).ConfigureAwait(false);
                _logger.LogInformation("llama-server ready for model {ModelName} role {Role} (pid {ProcessId}) after {ElapsedMs:F0} ms (readiness budget {BudgetSeconds:F0}s).",
                    key.ModelName, key.Role, handle.ProcessId, (_timeProvider.GetUtcNow() - readyStartedUtc).TotalMilliseconds, readinessTimeout.TotalSeconds);

                // AUD4-02: read the effective per-slot context the server actually loaded (best-effort) so both app-side
                // budgeters and the UI meter size against the REAL window rather than the requested/advertised one.
                var effectiveContext = await TryReadEffectiveContextAsync(spec.BaseAddress, ct).ConfigureAwait(false);

                var endpoint = new LlamaServerEndpoint(key.ModelName, key.Role, spec.BaseAddress);
                var running = new RunningProcess(handle, endpoint, port, _timeProvider.GetUtcNow())
                {
                    EffectiveContextTokens = effectiveContext
                };
                _processes[key] = running;

                if (isSafeRetry)
                {
                    // The safe config reached readiness where the optimized (KV-quant + flash-attention) config could
                    // not — so the optimized config is the culprit for THIS backend (not a broken model, which would
                    // fail the safe config too). Record it so subsequent spawns skip the known-bad optimized config.
                    await _launchPolicy.RecordOptimizedConfigFailedAsync(variant, ct).ConfigureAwait(false);
                }

                return running;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                handle?.TreeKill();
                handle?.Dispose();
                await ReleaseReservedPortAsync(port).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                // Launch/readiness failed: tree-kill the half-started child and free its reserved port (under the
                // admission gate, since the reserved-port set backs the cap count) before deciding whether to fall back.
                handle?.TreeKill();
                handle?.Dispose();
                await ReleaseReservedPortAsync(port).ConfigureAwait(false);

                // The OPTIMIZED attempt failed and a safe candidate remains: remember the error and retry ONCE with the
                // safe (KV/FA off) config. Any other failure (the safe attempt, or a spawn with no fallback candidate)
                // propagates exactly as before — including the readiness-timeout marker the restart loop keys on.
                if (!isSafeRetry && attempt + 1 < planCandidates.Count)
                {
                    optimizedFailure = ex;
                    _logger.LogWarning(ex, "llama-server optimized launch (KV-cache quant + flash attention) failed for model {ModelName} role {Role} on backend {Variant}; retrying once with the safe config.",
                        key.ModelName, key.Role, variant);
                    continue;
                }

                throw;
            }
        }

        // Unreachable: the loop returns on success or throws on the final candidate; the fallback keeps the analyzer happy.
        throw optimizedFailure ?? new InvalidOperationException("llama-server spawn produced no launch attempt.");
    }

    /// <summary>
    ///     Builds the ordered launch-plan candidates for a spawn. The normal path (<paramref name="applyLaunchPolicy" />)
    ///     resolves the policy plan and, when it enables the GPU KV-quant + flash-attention optimization, appends a safe
    ///     (KV/FA off) fallback candidate to try once if the optimized one cannot reach readiness. Operator profiling and
    ///     replay-without-optimization spawns get a single <see langword="null" /> plan (today's byte-for-byte behavior).
    /// </summary>
    private async Task<IReadOnlyList<LlamaServerLaunchPlan?>> BuildLaunchPlanCandidatesAsync(ProcessKey key,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        bool applyLaunchPolicy,
        CancellationToken ct)
    {
        if (!applyLaunchPolicy)
        {
            return [null];
        }

        var trainContext = await TryResolveTrainContextAsync(key.ModelName, ct).ConfigureAwait(false);
        var plan = await _launchPolicy.ResolveAsync(key.Role, variant, resolved, trainContext, ct).ConfigureAwait(false);

        return plan.UseKvCacheQuantization
            ? [plan, plan.WithoutKvCacheQuantization()]
            : [plan];
    }

    /// <summary>Reads the model's advertised train context (GGUF header), returning null when unknown — never fatal.</summary>
    private async Task<long?> TryResolveTrainContextAsync(string modelName, CancellationToken ct)
    {
        try
        {
            var facts = await _modelStore.ResolveModelFootprintFactsAsync(modelName, ct).ConfigureAwait(false);
            return facts?.ContextLength;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogDebug(exception, "Resolving the train context for model {ModelName} failed; the requested context will not be capped.", modelName);
            return null;
        }
    }

    /// <summary>Best-effort read of the running server's effective context window from /props; null when unavailable.</summary>
    private async Task<int?> TryReadEffectiveContextAsync(Uri baseAddress, CancellationToken ct)
    {
        try
        {
            return await _healthProbe.TryReadEffectiveContextTokensAsync(baseAddress, ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Never let a /props hiccup discard a model that reached readiness; effective context simply stays unknown.
            _logger.LogDebug(exception, "Reading the effective context window from /props failed; effective context is unknown.");
            return null;
        }
    }

    /// <summary>A compact, path-free launch-plan summary appended to the spawn log line (empty for a policy-less spawn).</summary>
    private static string DescribeLaunchPlan(LlamaServerLaunchPlan? plan)
    {
        if (plan is not { } resolvedPlan)
        {
            return string.Empty;
        }

        var parts = new List<string>(capacity: 3);
        if (resolvedPlan.RequestedContextTokens is { } ctx)
        {
            parts.Add($"ctx={ctx.ToString(CultureInfo.InvariantCulture)}");
        }

        if (resolvedPlan.UseKvCacheQuantization)
        {
            parts.Add($"kv={resolvedPlan.KvCacheType}+fa");
        }

        if (resolvedPlan.CpuThreads is { } threads)
        {
            parts.Add($"threads={threads.ToString(CultureInfo.InvariantCulture)}/{resolvedPlan.CpuThreadsBatch?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        }

        return parts.Count == 0 ? string.Empty : " [" + string.Join(", ", parts) + "]";
    }

    /// <summary>
    ///     Waits for a freshly launched process to pass its readiness probe, racing that wait against the process
    ///     exiting. A child that dies during load (an incompatible model, or a context that will not fit in the
    ///     available memory) is detected the instant it exits and surfaced as a NON-RETRYABLE failure — instead of
    ///     polling <c>/health</c> against a dead endpoint for the full readiness budget and then retrying. A
    ///     crash-on-load is deterministic, so retrying it only multiplies the stall by <c>MaxRestartAttempts</c>.
    /// </summary>
    private async Task WaitForReadyOrExitAsync(ILlamaServerProcessHandle handle, Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
    {
        // Cancel the losing side the instant the other wins, so neither the /health poll nor the exit-watcher is left
        // running after the race is decided.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var readyTask = _healthProbe.WaitForReadyAsync(baseAddress, readinessTimeout, linkedCts.Token);
        var exitTask = WatchForExitAsync(handle, linkedCts.Token);

        var winner = await Task.WhenAny(readyTask, exitTask).ConfigureAwait(false);

        if (winner == exitTask && handle.HasExited)
        {
            // The child exited before it ever became ready: a deterministic load failure. Stop the abandoned /health
            // poll and surface a sanitized, non-retryable error (no file paths) so the caller fails fast.
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await SwallowCancellationAsync(readyTask).ConfigureAwait(false);
            throw NonRetryable("The local model runtime exited while loading the model. The model may be incompatible with this runtime or too large for the available memory.");
        }

        // Readiness settled first: stop the exit-watcher and honor the existing outcome — a genuine timeout (process
        // still alive but slow) stays a retryable "did not become ready in time".
        await linkedCts.CancelAsync().ConfigureAwait(false);
        await SwallowCancellationAsync(exitTask).ConfigureAwait(false);

        if (!await readyTask.ConfigureAwait(false))
        {
            _logger.LogWarning("llama-server (pid {ProcessId}) did not become ready within {TimeoutSeconds:F0}s.",
                handle.ProcessId, readinessTimeout.TotalSeconds);
            throw ReadinessTimedOut("The local model runtime did not become ready in time.");
        }
    }

    /// <summary>Polls the process's exit flag until it exits or the wait is cancelled (readiness won the race).</summary>
    private async Task WatchForExitAsync(ILlamaServerProcessHandle handle, CancellationToken ct)
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

    /// <summary>Awaits the losing side of the readiness race, absorbing the cancellation it throws when abandoned.</summary>
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

    /// <inheritdoc />
    public async Task<T> RunExclusiveProfilingAsync<T>(string modelName,
        ModelRole role,
        ResolvedLaunchArguments launchArgs,
        bool enableMetrics,
        Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(launchArgs);
        ArgumentNullException.ThrowIfNull(body);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var key = new ProcessKey(modelName, role);

        // Take the SAME single-flight gate the normal ensure path uses, so a concurrent user EnsureRunningAsync for this
        // key queues behind the exclusive profiling spawn instead of racing it.
        var gate = _ensureGates.GetOrAdd(key, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Explicitly evict any warm process for this key — admission only auto-evicts an IDLE LRU victim, so a
            // freshly-used warm process would otherwise survive and the profiling spawn would not be exclusive.
            await EvictAsync(modelName, role, ct).ConfigureAwait(false);

            // Thread-safe per-line sink backing the StartupCapture callback (both server pipes Enqueue concurrently).
            var startupOutput = new ConcurrentQueue<string>();

            // Spawn exactly one process with the operator-supplied args verbatim (bypass BOTH the profile resolver and
            // the launch policy — the supplied args ARE the experiment being measured).
            var running = await SpawnCoreAsync(key,
                    (_, _) => Task.FromResult(launchArgs),
                    startupOutput.Enqueue,
                    ensureMetrics: enableMetrics,
                    applyLaunchPolicy: false,
                    ct)
                .ConfigureAwait(false);

            // Pin against idle eviction for the whole benchmark — the process is never marked-used during the body, so
            // without the pin the reaper would treat it as idle past the TTL and tear it down mid-measurement.
            running.Pin();
            try
            {
                var context = new LlamaServerProfilingContext(running.Endpoint, startupOutput.ToArray());
                return await body(context, ct).ConfigureAwait(false);
            }
            finally
            {
                // Always unpin + evict the transient profiling process, even on body throw or cancellation.
                running.Unpin();
                await RemoveProcessAsync(key, running).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    ///     Reserves a slot under the loaded-cap (evicting an idle LRU process to make room when possible) and allocates
    ///     a free localhost port. The admission gate serializes the cap decision so it can never be raced past.
    /// </summary>
    /// <remarks>
    ///     The cap is measured by the <em>reserved-port</em> count, not the registered-process count. A port is
    ///     allocated here (under the gate) and held until the process registers, fails, or is evicted — so the count
    ///     already includes in-flight spawns. Counting registered processes instead would let two concurrent distinct
    ///     <c>(model, role)</c> spawns (which take distinct ensure-gates) both pass the check at count <c>N</c> before
    ///     either registers, overrunning the cap.
    /// </remarks>
    private async Task<int> AdmitAndAllocatePortAsync(CancellationToken ct)
    {
        await _admissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Drop any process that has already exited so its slot/port is reclaimed before the cap check.
            PruneExitedProcesses();

            if (_allocatedPorts.Count >= _options.MaxLoadedProcesses && !TryEvictIdleLeastRecentlyUsed())
            {
                throw CapReached();
            }

            return AllocatePort();
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    /// <summary>
    ///     Builds the exact, ordered llama-server argument vector for a <c>(model, role)</c> on a port.
    ///     <paramref name="chatCacheReuse" /> is the chat-role <c>--cache-reuse</c> window
    ///     (<see cref="LlamaServerSupervisorOptions.ChatCacheReuse" />); <c>0</c> omits the flag.
    ///     <paramref name="speculative" /> is the chat-role speculative-decoding config
    ///     (<see cref="LlamaServerSupervisorOptions.Speculative" />); disabled/default emits no <c>--spec-*</c> flags.
    /// </summary>
    internal static LlamaServerLaunchSpec BuildLaunchSpec(ProcessKey key,
        string executablePath,
        string modelFilePath,
        int port,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        int chatCacheReuse,
        SpeculativeDecodingSettings speculative = default,
        LlamaServerLaunchPlan? plan = null)
    {
        var args = new List<string>
        {
            "-m",
            modelFilePath,
            "--host",
            "127.0.0.1", // localhost-only bind
            "--port",
            port.ToString(CultureInfo.InvariantCulture),

            // Single-slot serving (the locked design — one in-flight request per (model, role) process). Pinning
            // --parallel 1 stops llama-server from auto-selecting n_parallel=4, which reserves 4x the KV cache and
            // starves --fit's weight offload: with the auto default, fit spills weights to system RAM to make room for
            // KV slots that are never used, so a model that would fit on the GPU runs slow on the CPU instead.
            "--parallel",
            "1",

            // Skip the empty-run warmup. On a large model it can take 45-110s and overrun the readiness budget, which
            // tree-kills the half-ready process and respawns it in a loop (observed as a chat inter-chunk stall and an
            // explore "did not become ready in time"). The model serves correctly without it — the readiness probe and
            // the first real request warm it naturally — so dropping it makes startup fast and reliable at any size.
            "--no-warmup"
        };

        // Context (-c), placement, KV-cache/flash-attention, and CPU threads. The variant selects the llama.cpp BUILD
        // (Cuda/Vulkan vs pure CPU). Precedence lives in the launch policy that produced `plan`; here we just emit:
        //  - GPU explore: --fit on + --metrics (auto-fit places layers/experts around the explicit -c), plus the policy
        //    -c and the KV-quant + flash-attention optimization; GPU replay: the frozen profile args verbatim.
        //  - CPU: the policy -c (explore) or the frozen -c (replay), plus the CPU thread policy; NO --fit/--metrics/-ngl,
        //    KV stays f16 and flash-attention stays auto.
        // A null plan (operator profiling) reproduces the pre-policy behavior byte-for-byte.
        AppendContextPlacementAndThreadArgs(args, variant, resolved, plan);

        if (key.Role == ModelRole.Chat)
        {
            // Mandatory for tool/function calling — without it llama-server ignores the GGUF tool grammar.
            args.Add("--jinja");

            // Prompt-cache prefix reuse. The app resends the full selected-path history every turn, so cache-reuse
            // lets llama-server reuse the unchanged prompt prefix via KV cache shifting instead of reprocessing it —
            // a large time-to-first-token win on multi-turn chat/agent conversations. A positive window enables the
            // flag; 0 (upstream default) omits it. Chat role only: an embedding server does one-shot forward passes
            // with no shared conversational prefix to reuse, so the flag is meaningless there. This is a server-launch
            // flag independent of the OpenAI-compat request body (which exposes no cache_prompt/n_keep field).
            if (chatCacheReuse > 0)
            {
                args.Add("--cache-reuse");
                args.Add(chatCacheReuse.ToString(CultureInfo.InvariantCulture));
            }

            AppendSpeculativeArgs(args, speculative);
        }
        else if (key.Role == ModelRole.Embedding)
        {
            // /v1/embeddings is exposed only with --embeddings + a non-`none` pooling type.
            args.Add("--embeddings");
            args.Add("--pooling");
            args.Add("mean");
        }
        else if (key.Role == ModelRole.Reranker)
        {
            // Reranker role. POST /v1/rerank is exposed only with --rerank (alias --reranking) + `--pooling rank`
            // (verified against llama.cpp release b9692). This is MUTUALLY EXCLUSIVE with the embedding branch above —
            // a rerank server scores (query, document) pairs and never gets --embeddings — and carries none of the
            // chat-only flags (--jinja, --cache-reuse, speculative). Because each role is its own branch, a single
            // process can only ever receive one role's flags, so --embeddings and --rerank never coexist.
            args.Add("--rerank");
            args.Add("--pooling");
            args.Add("rank");
        }
        else
        {
            // Explicit guard: a ModelRole added later must not silently inherit the reranker flags. Fail loudly so the
            // new role's launch args are a deliberate decision here rather than an accident of the branch order.
            throw new ArgumentOutOfRangeException(nameof(key),
                key.Role,
                $"No llama-server launch arguments are defined for model role '{key.Role}'.");
        }

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? Environment.CurrentDirectory;
        return new LlamaServerLaunchSpec(key.ModelName, key.Role, executablePath, args, port, workingDirectory);
    }

    /// <summary>
    ///     Appends the context (<c>-c</c>), placement, KV-cache/flash-attention, and CPU-thread args for a spawn, per the
    ///     variant + explore/replay mode + policy <paramref name="plan" />. See the call-site comment for the matrix.
    /// </summary>
    private static void AppendContextPlacementAndThreadArgs(List<string> args,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        LlamaServerLaunchPlan? plan)
    {
        if (variant != GpuVariant.Cpu)
        {
            if (resolved.ExploreMode)
            {
                // Let llama.cpp auto-fit choose + print placement; --metrics exposes the gauges the benchmark reads.
                // The explicit -c is RESPECTED by --fit (it fits ngl/batch around it) and the KV/FA flags are not
                // placement flags, so auto-fit stays active (verified against b9692).
                args.Add("--fit");
                args.Add("on");
                args.Add("--metrics");
                AppendPolicyContextArgs(args, plan);
                AppendPolicyKvCacheAndFlashAttentionArgs(args, plan);
            }
            else
            {
                AppendReplayArgs(args, resolved);
            }

            return;
        }

        // CPU build: NO GPU placement/replay args (-ngl/-ts/-ot/-ctk) and NO --fit/--metrics — a frozen GPU profile does
        // not transfer to a CPU spawn. It gets ONLY the deterministic policy context (-c) and the CPU thread policy; KV
        // stays f16 and flash-attention stays auto. A null plan (operator profiling) emits neither, matching the old
        // "CPU emits no gpu/fit args" behavior byte-for-byte.
        AppendPolicyContextArgs(args, plan);
        AppendCpuThreadArgs(args, plan);
    }

    /// <summary>Appends the policy's requested context (<c>-c</c>) when set (a frozen replay owns its own -c instead).</summary>
    private static void AppendPolicyContextArgs(List<string> args, LlamaServerLaunchPlan? plan)
    {
        if (plan is { RequestedContextTokens: { } contextTokens })
        {
            args.Add("-c");
            args.Add(contextTokens.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Appends the GPU KV-cache quantization + fused flash-attention args (<c>-fa on -ctk/-ctv &lt;type&gt;</c>) when the plan enables them.</summary>
    private static void AppendPolicyKvCacheAndFlashAttentionArgs(List<string> args, LlamaServerLaunchPlan? plan)
    {
        if (plan is { UseKvCacheQuantization: true } resolvedPlan && !string.IsNullOrWhiteSpace(resolvedPlan.KvCacheType))
        {
            // Quantized/explicit KV requires the fused flash-attention path with matching K/V types (b9692).
            args.Add("-fa");
            args.Add("on");
            args.Add("-ctk");
            args.Add(resolvedPlan.KvCacheType);
            args.Add("-ctv");
            args.Add(resolvedPlan.KvCacheType);
        }
    }

    /// <summary>Appends the CPU thread policy (<c>-t</c>/<c>-tb</c>) when the plan carries thread counts (CPU build only).</summary>
    private static void AppendCpuThreadArgs(List<string> args, LlamaServerLaunchPlan? plan)
    {
        if (plan is { CpuThreads: { } threads })
        {
            args.Add("-t");
            args.Add(threads.ToString(CultureInfo.InvariantCulture));
        }

        if (plan is { CpuThreadsBatch: { } threadsBatch })
        {
            args.Add("-tb");
            args.Add(threadsBatch.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    ///     Replays a frozen/explored profile verbatim (<c>-c/-ngl/-ts/-ot</c> + matched <c>-ctk/-ctv</c> with
    ///     <c>--flash-attn on</c>). <c>--fit</c> is intentionally absent — any explicit fit-arg disables auto-fit, so
    ///     replay and explore are mutually exclusive per run. The launch policy never overrides these (highest precedence).
    /// </summary>
    private static void AppendReplayArgs(List<string> args, ResolvedLaunchArguments resolved)
    {
        args.Add("-c");
        args.Add(resolved.CtxSize.ToString(CultureInfo.InvariantCulture));

        if (resolved.NGpuLayers is { } gpuLayers)
        {
            args.Add("--n-gpu-layers");
            args.Add(gpuLayers.ToString(CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(resolved.TensorSplit))
        {
            args.Add("-ts");
            args.Add(resolved.TensorSplit);
        }

        if (!string.IsNullOrWhiteSpace(resolved.OverrideTensor))
        {
            args.Add("-ot");
            args.Add(resolved.OverrideTensor);
        }

        if (!string.IsNullOrWhiteSpace(resolved.KvTypeK) && !string.IsNullOrWhiteSpace(resolved.KvTypeV))
        {
            // Matching-type rule + flash-attention invariant (enforced upstream in ResolvedLaunchArguments.Replay):
            // the fused FA path needs equal K/V types and --flash-attn on.
            args.Add("-ctk");
            args.Add(resolved.KvTypeK);
            args.Add("-ctv");
            args.Add(resolved.KvTypeV);
            args.Add("--flash-attn");
            args.Add("on");
        }
    }

    /// <summary>
    ///     Appends the chat-role speculative-decoding flags. Disabled/default (<c>none</c>) emits nothing. A configured
    ///     mode is validated first (unknown mode, or a <c>draft-*</c> mode with no draft path, is a deterministic
    ///     misconfiguration surfaced as a NON-RETRYABLE error rather than a server that dies cryptically on launch).
    ///     <c>draft-*</c> modes emit <c>--spec-draft-model</c> (the drafter loads inside the chat process and is never
    ///     separately ledgered or footprint-estimated; on the primary NVIDIA path its resident VRAM is still reflected in
    ///     <c>CapacityService</c>'s free-VRAM baseline — <c>nvidia-smi memory.free</c> — so a later sub-agent admission
    ///     accounts for it, but on the non-NVIDIA total-minus-ledger fallback it stays invisible) plus
    ///     <c>--spec-draft-n-max</c>/<c>-ngl</c> when set; <c>ngram-*</c> modes self-speculate and emit only <c>--spec-type</c>.
    /// </summary>
    private static void AppendSpeculativeArgs(List<string> args, in SpeculativeDecodingSettings speculative)
    {
        if (!speculative.IsEnabled)
        {
            return;
        }

        if (!speculative.TryValidate(out var error))
        {
            throw NonRetryable(error!);
        }

        args.Add("--spec-type");
        args.Add(speculative.NormalizedMode);

        if (!speculative.IsDraftMode)
        {
            return;
        }

        // Validated non-empty above; the file's existence on disk is enforced on the spawn path before launch.
        args.Add("--spec-draft-model");
        args.Add(speculative.DraftModelPath!);

        if (speculative.DraftMaxTokens > 0)
        {
            args.Add("--spec-draft-n-max");
            args.Add(speculative.DraftMaxTokens.ToString(CultureInfo.InvariantCulture));
        }

        if (speculative.DraftGpuLayers is { } draftGpuLayers)
        {
            args.Add("--spec-draft-ngl");
            args.Add(draftGpuLayers.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Background reaper: evicts processes idle beyond <see cref="LlamaServerSupervisorOptions.IdleTimeToLive" />.</summary>
    private async Task ReapIdleLoopAsync(CancellationToken ct)
    {
        // Re-check at a fraction of the TTL so eviction latency stays bounded without busy-spinning.
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
            // A live profiling-pinned process is never idle-evicted mid-benchmark; an EXITED one is still reaped below
            // so a dead handle never leaks even while pinned.
            if (running.IsProfilingPinned && !running.Handle.HasExited)
            {
                continue;
            }

            if (running.Handle.HasExited || now - running.LastUsedUtc >= _options.IdleTimeToLive)
            {
                if (!running.Handle.HasExited)
                {
                    _logger.LogInformation("Evicting idle llama-server for model {ModelName} role {Role} (idle {IdleSeconds:F0}s past TTL {TtlSeconds:F0}s).",
                        key.ModelName, key.Role, (now - running.LastUsedUtc).TotalSeconds, _options.IdleTimeToLive.TotalSeconds);
                }

                await RemoveProcessAsync(key, running).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Evicts the least-recently-used process that is currently idle (caller holds the admission gate).</summary>
    private bool TryEvictIdleLeastRecentlyUsed()
    {
        var now = _timeProvider.GetUtcNow();
        ProcessKey? victimKey = null;
        RunningProcess? victim = null;
        foreach (var (key, running) in _processes)
        {
            // A live profiling-pinned process is reserved for its benchmark — never select it as a cap-admission victim
            // (an EXITED pinned process is a dead handle and stays eligible so its slot/port is reclaimed).
            if (running.IsProfilingPinned && !running.Handle.HasExited)
            {
                continue;
            }

            if (now - running.LastUsedUtc < _options.IdleTimeToLive && !running.Handle.HasExited)
            {
                continue; // Not idle — never evict an in-window process to admit a new one.
            }

            if (victim is null || running.LastUsedUtc < victim.LastUsedUtc)
            {
                victimKey = key;
                victim = running;
            }
        }

        if (victimKey is null || victim is null)
        {
            return false;
        }

        _logger.LogWarning("Loaded-model cap ({Cap}) reached; evicting idle llama-server for model {ModelName} role {Role} to admit a new one.",
            _options.MaxLoadedProcesses, victimKey.Value.ModelName, victimKey.Value.Role);

        // Synchronous teardown under the admission gate: free the slot/port before the new admission proceeds.
        TeardownProcess(victimKey.Value, victim);
        return true;
    }

    private void PruneExitedProcesses()
    {
        foreach (var (key, running) in _processes)
        {
            if (running.Handle.HasExited)
            {
                TeardownProcess(key, running);
            }
        }
    }

    private async Task RemoveProcessAsync(ProcessKey key, RunningProcess running)
    {
        // Teardown must complete even during shutdown, so it is not bound to a caller cancellation token.
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            TeardownProcess(key, running);
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    /// <summary>Tree-kills + disposes a process and releases its port. Caller holds the admission gate.</summary>
    private void TeardownProcess(ProcessKey key, RunningProcess running)
    {
        if (!_processes.TryRemove(new KeyValuePair<ProcessKey, RunningProcess>(key, running)))
        {
            return; // Already removed by a concurrent path.
        }

        try
        {
            running.Handle.TreeKill();
        }
        finally
        {
            running.Handle.Dispose();
            ReleasePort(running.Port);
        }
    }

    /// <summary>Allocates a free port from the configured range (caller holds the admission gate).</summary>
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

        throw NonRetryable("No free local port is available for the model runtime.");
    }

    private void ReleasePort(int port)
    {
        _allocatedPorts.Remove(port);
    }

    /// <summary>
    ///     Releases a reserved port for a spawn that never registered (launch/readiness failure), taking the admission
    ///     gate so the reserved-port set (which backs the cap count) is mutated under the same lock as allocation.
    /// </summary>
    private async Task ReleaseReservedPortAsync(int port)
    {
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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
        // Probe by binding loopback; collision (another process owns it) means skip-and-retry.
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

    private static LlamaRuntimeException CapReached()
    {
        return NonRetryable("The maximum number of local models are already loaded. Unload a model or raise the limit, then try again.");
    }

    /// <summary>Builds a sanitized failure flagged as a deterministic (non-retryable) policy/config outcome.</summary>
    private static LlamaRuntimeException NonRetryable(string sanitizedMessage)
    {
        var ex = new LlamaRuntimeException(sanitizedMessage);
        ex.Data[NonRetryableMarker] = true;
        return ex;
    }

    /// <summary>
    ///     Builds a sanitized readiness-TIMEOUT failure (process alive but slow to load). Flagged so the restart loop
    ///     retries it at most <see cref="LlamaServerSupervisorOptions.MaxReadinessTimeoutRetries" /> times rather than
    ///     the full restart cap.
    /// </summary>
    private static LlamaRuntimeException ReadinessTimedOut(string sanitizedMessage)
    {
        var ex = new LlamaRuntimeException(sanitizedMessage);
        ex.Data[ReadinessTimeoutMarker] = true;
        return ex;
    }

    /// <summary>Reads a model file's on-disk size, returning 0 when the path is missing/unreadable (→ base readiness timeout).</summary>
    private static long TryGetFileSizeBytes(string modelFilePath)
    {
        try
        {
            var info = new FileInfo(modelFilePath);
            return info.Exists ? info.Length : 0L;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0L;
        }
    }

    /// <summary>Identifies a process by the model it serves and the role (chat vs embedding).</summary>
    internal readonly record struct ProcessKey(string ModelName, ModelRole Role)
    {
        public bool Equals(ProcessKey other)
        {
            return Role == other.Role && string.Equals(ModelName, other.ModelName, StringComparison.OrdinalIgnoreCase);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(ModelName), Role);
        }
    }

    /// <summary>
    ///     A reference-counted inference lease over a <see cref="RunningProcess" />. Disposal releases the lease exactly
    ///     once. <see cref="WasEjected" /> mirrors the underlying process so an in-flight request that fails right after a
    ///     force-eject classifies the drop as an operator eject rather than a generic provider failure.
    /// </summary>
    private sealed class InferenceLease(RunningProcess process) : ILlamaServerInferenceLease
    {
        private int _disposed;

        public bool WasEjected => process.WasEjected;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
            {
                process.ReleaseLease();
            }
        }
    }

    /// <summary>A live, registered process and its last-used timestamp (drives idle-TTL + LRU eviction).</summary>
    private sealed class RunningProcess(ILlamaServerProcessHandle handle, LlamaServerEndpoint endpoint, int port, DateTimeOffset startedUtc)
    {
        private long _lastUsedTicks = startedUtc.UtcTicks;

        // Seeded to the spawn time so a freshly-ready process is not re-probed until one full interval has passed.
        private long _lastLivenessProbeTicks = startedUtc.UtcTicks;
        private int _consecutiveLivenessFailures;
        private int _profilingPinned;
        private int _activeLeases;
        private int _evicting;
        private int _ejected;

        public ILlamaServerProcessHandle Handle { get; } = handle;

        public LlamaServerEndpoint Endpoint { get; } = endpoint;

        public int Port { get; } = port;

        /// <summary>
        ///     The effective per-slot context window (<c>/props default_generation_settings.n_ctx</c>) the server
        ///     actually loaded, captured once after readiness. <see langword="null" /> when <c>/props</c> was unavailable.
        /// </summary>
        public int? EffectiveContextTokens { get; init; }

        public DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

        /// <summary>
        ///     <see langword="true" /> while an operator profiling benchmark owns this process; the idle reaper and the
        ///     cap-admission LRU eviction skip a pinned, non-exited process so it is never torn down mid-measurement.
        /// </summary>
        public bool IsProfilingPinned => Volatile.Read(ref _profilingPinned) != 0;

        /// <summary>Number of in-flight inference requests currently leasing this process (drives graceful-eject drain).</summary>
        public int ActiveLeases => Volatile.Read(ref _activeLeases);

        /// <summary><see langword="true" /> once an operator eject has begun for this process — new leases are refused.</summary>
        public bool IsEvicting => Volatile.Read(ref _evicting) != 0;

        /// <summary><see langword="true" /> once this process was force-ejected while in-flight work still held a lease.</summary>
        public bool WasEjected => Volatile.Read(ref _ejected) != 0;

        /// <summary>Registers an in-flight inference request against this process.</summary>
        public void AcquireLease()
        {
            Interlocked.Increment(ref _activeLeases);
        }

        /// <summary>Releases a previously-acquired inference lease.</summary>
        public void ReleaseLease()
        {
            Interlocked.Decrement(ref _activeLeases);
        }

        /// <summary>Marks the process evicting so new leases are refused while an eject drains the in-flight ones.</summary>
        public void MarkEvicting()
        {
            Interlocked.Exchange(ref _evicting, value: 1);
        }

        /// <summary>Clears the evicting flag (a graceful eject that timed out and left the process running).</summary>
        public void ClearEvicting()
        {
            Interlocked.Exchange(ref _evicting, value: 0);
        }

        /// <summary>Records a force-eject with in-flight work so the leaseholder classifies the drop as an operator eject.</summary>
        public void MarkEjected()
        {
            Interlocked.Exchange(ref _ejected, value: 1);
        }

        public void MarkUsed(DateTimeOffset now)
        {
            Interlocked.Exchange(ref _lastUsedTicks, now.UtcTicks);
        }

        /// <summary>
        ///     Atomically claims the right to run the reuse-path liveness probe: succeeds (advancing the probe clock to
        ///     <paramref name="now" />) only when at least <paramref name="interval" /> has elapsed since the last claim.
        ///     Serializes probes across concurrent reuses so at most one HTTP probe runs per process per interval.
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

        /// <summary>Reserves this process for a profiling benchmark, exempting it from idle eviction.</summary>
        public void Pin()
        {
            Interlocked.Exchange(ref _profilingPinned, value: 1);
        }

        /// <summary>Releases the profiling reservation so normal idle eviction resumes.</summary>
        public void Unpin()
        {
            Interlocked.Exchange(ref _profilingPinned, value: 0);
        }
    }
}
