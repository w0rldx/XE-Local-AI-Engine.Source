namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
///         non-<c>none</c> <c>--pooling</c> (embedding) flags — is built by <see cref="LlamaServerLaunchArgumentComposer.BuildLaunchSpec" />.
///     </para>
/// </remarks>
public sealed class LlamaServerProcessSupervisor : ILlamaServerProcessSupervisor, IAsyncDisposable
{
    private const string NonRetryableMarker = "LlamaServer.NonRetryable";
    private const string CapabilityIncompatibleMarker = "LlamaServer.CapabilityIncompatible";
    private const string CapabilitySafeFallbackMarker = "LlamaServer.CapabilitySafeFallback";

    // Flags a readiness TIMEOUT (process alive but slow) so the restart loop retries it at most
    // MaxReadinessTimeoutRetries times instead of the full MaxRestartAttempts — a deterministically slow/large model
    // is not a transient crash, so retrying it many times only multiplies the kill/reload thrash.
    private const string ReadinessTimeoutMarker = "LlamaServer.ReadinessTimeout";

    // The lowest llama.cpp log verbosity (-lv) that emits the model-load layer-placement banner. Measured against the
    // server default of 3, which prints an 11-line startup carrying no placement information at all. Level 4 adds ~213
    // startup lines per spawn (logged at Information — that IS the placement evidence) and ~22 lines per request
    // (demoted to Debug once serving, so the sink absorbs roughly nothing). The next level up is the per-tensor debug
    // firehose: ~1250 startup lines and ~1650 lines PER REQUEST, which no sink policy makes affordable.
    private const string PlacementProbeLogVerbosity = "4";

    /// <summary>Poll cadence for observing that a freshly spawned process exited during its readiness wait.</summary>
    private static readonly TimeSpan ProcessExitPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Poll cadence for observing that in-flight inference leases have drained during a graceful eject.</summary>
    private static readonly TimeSpan LeaseDrainPollInterval = TimeSpan.FromMilliseconds(25);

    /// <summary>Base delay between crash-restart attempts; grows linearly per attempt.</summary>
    private static readonly TimeSpan RestartBackoffStep = TimeSpan.FromMilliseconds(250);

    private readonly LlamaServerRuntimeMutationGate _runtimeMutationGate;
    private readonly LlamaServerIdleReaper _reaper;
    private readonly ILlamaCppBinaryManager _binaryManager;
    private readonly ILlamaServerCapabilityManifestProbe _capabilityManifestProbe;

    // Single-flight ensure-running gate, one semaphore per (model, role) key. Held only for the short reuse/decision
    // section, NOT for the whole spawn — the spawn itself runs detached (see _inflightSpawns).
    private readonly ConcurrentDictionary<ProcessKey, SemaphoreSlim> _ensureGates = new();

    // The in-flight, DETACHED spawn task per (model, role) key. A caller AWAITS this task but never owns its lifetime:
    // a caller cancelling its own wait does not abort the model load, which continues under its own readiness deadline
    // and leaves the model warm for the next send (the deliberate design — a user who cancels before the first token
    // does not throw away the load everyone behind them is waiting on). Exactly one runs per key at a time; it removes
    // itself on completion (success or failure) so the next ensure retries fresh.
    private readonly ConcurrentDictionary<ProcessKey, InflightSpawn> _inflightSpawns = new();
    private readonly LlamaServerExternalEndpointOptions _externalEndpoints;
    private readonly ILlamaServerHealthProbe _healthProbe;
    private readonly ILlamaFitParamsRunner _fitParamsRunner;
    private readonly ILlamaServerLaunchPolicy _launchPolicy;
    private readonly IProcessContextAllocationResolver _allocationResolver;
    private readonly IProcessLaunchAdmissionRegistry _launchAdmissions;
    private readonly ILogger<LlamaServerProcessSupervisor> _logger;
    private readonly ILlamaServerProcessLauncher _launcher;
    private readonly ILlamaCppSourceBuildActivity _sourceBuildActivity;
    private readonly IGgufModelStore _modelStore;
    private readonly LlamaServerSupervisorOptions _options;
    private readonly IInferenceProfileResolver _profileResolver;

    // Per-model developer/advanced override: extra llama-server flags the operator typed, appended after the built spec
    // on the normal serving path. Empty for every model with no override; the composition root injects the store-backed
    // resolver over the provider's empty default.
    private readonly ILlamaServerExtraLaunchArgumentsResolver _extraArgumentsResolver;

    // One running process per (model, role) key.
    private readonly ConcurrentDictionary<ProcessKey, RunningProcess> _processes = new();
    private readonly Task _reaperLoop;

    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TimeProvider _timeProvider;
    private readonly IGpuVariantSelector _variantSelector;
    private readonly TaskScheduler _detachedSpawnScheduler;

    // The process-wide GPU-load admission gate. GPU-backed spawns serialize their spawn-through-readiness window
    // through it (shared with the image supervisor) so two --fit loads never read the same free-VRAM snapshot at once.
    private readonly IGpuModelLoadAdmission _loadAdmission;

    // Node-wide record of measured GPU layer placement. Written here as models load, read by the operator-facing
    // runtime device audit; the composition root injects the singleton both sides share.
    private readonly ILlamaLayerPlacementReport _layerPlacementReport;
    private readonly ILlamaServerLoadTelemetry _loadTelemetry;

    /// <summary>
    ///     Creates a supervisor over the supplied collaborators. The reaper loop starts immediately. Constructed via DI
    ///     (same-assembly factory) or in tests — the launcher/health-probe seams are internal, so the ctor is internal.
    /// </summary>
    internal LlamaServerProcessSupervisor(ILlamaCppBinaryManager binaryManager,
        IGpuVariantSelector variantSelector,
        IGgufModelStore modelStore,
        ILlamaServerProcessLauncher launcher,
        ILlamaServerHealthProbe healthProbe,
        ILlamaServerCapabilityManifestProbe capabilityManifestProbe,
        LlamaServerSupervisorOptions options,
        IInferenceProfileResolver profileResolver,
        ILlamaServerLaunchPolicy launchPolicy,
        LlamaServerExternalEndpointOptions? externalEndpoints = null,
        TimeProvider? timeProvider = null,
        ILogger<LlamaServerProcessSupervisor>? logger = null,
        IGpuModelLoadAdmission? loadAdmission = null,
        ILlamaCppSourceBuildActivity? sourceBuildActivity = null,
        ILlamaFitParamsRunner? fitParamsRunner = null,
        IProcessContextAllocationResolver? allocationResolver = null,
        ILlamaLayerPlacementReport? layerPlacementReport = null,
        IProcessLaunchAdmissionRegistry? launchAdmissions = null,
        ILlamaServerExtraLaunchArgumentsResolver? extraArgumentsResolver = null,
        ILlamaServerLoadTelemetry? loadTelemetry = null,
        TaskScheduler? detachedSpawnScheduler = null)
    {
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
        _capabilityManifestProbe = capabilityManifestProbe ?? throw new ArgumentNullException(nameof(capabilityManifestProbe));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        _extraArgumentsResolver = extraArgumentsResolver ?? new EmptyLlamaServerExtraLaunchArgumentsResolver();
        _launchPolicy = launchPolicy ?? throw new ArgumentNullException(nameof(launchPolicy));
        _allocationResolver = allocationResolver ?? new DefaultProcessContextAllocationResolver(new LlamaServerLaunchPolicyOptions());
        _launchAdmissions = launchAdmissions ?? new ProcessLaunchAdmissionRegistry();
        _externalEndpoints = externalEndpoints ?? new LlamaServerExternalEndpointOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<LlamaServerProcessSupervisor>.Instance;
        _detachedSpawnScheduler = detachedSpawnScheduler ?? TaskScheduler.Default;

        // Absent a wired gate (a provider-only host / test), default to the no-op floor so GPU-load serialization is
        // simply off — the composition root injects the real, metric-emitting singleton shared with the image supervisor.
        _loadAdmission = loadAdmission ?? new NoOpGpuModelLoadAdmission();
        _sourceBuildActivity = sourceBuildActivity ?? new LlamaCppSourceBuildActivity();
        _fitParamsRunner = fitParamsRunner ?? new LlamaFitParamsProcessRunner();

        // A private instance keeps a provider-only host (or a test) self-satisfying; the composition root injects the
        // shared singleton so what this supervisor observes is what the runtime audit reports.
        _layerPlacementReport = layerPlacementReport ?? new LlamaLayerPlacementReport();
        _loadTelemetry = loadTelemetry ?? new NullLlamaServerLoadTelemetry();

        _runtimeMutationGate = new LlamaServerRuntimeMutationGate(typeof(LlamaServerProcessSupervisor), _shutdownCts.Token);
        _reaper = new LlamaServerIdleReaper(_processes,
            new LlamaServerPortAllocator(_options),
            _layerPlacementReport,
            _options,
            _timeProvider,
            _logger);

        _reaperLoop = Task.Run(() => _reaper.ReapIdleLoopAsync(_shutdownCts.Token), _shutdownCts.Token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_runtimeMutationGate.TryMarkDisposed())
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

        await _runtimeMutationGate.WaitForOperationsDrainedAsync().ConfigureAwait(false);

        // No new operation can enter after the disposed flag is latched, and the separate operation barrier above
        // proves every admitted operation has finished. Own the runtime gate exclusively through teardown and dispose
        // it in-place.
        await _runtimeMutationGate.EnterExclusiveForTeardownAsync().ConfigureAwait(false);
        var inflightSpawns = _inflightSpawns.Values.Select(static inflight => inflight.Task).ToArray();

        try
        {
            await Task.WhenAll(inflightSpawns).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Cancellation/failure is expected during shutdown. Completion is published only after each detached
            // spawn has removed its registry entry and released its launch ticket, so reaching here is cleanup-safe.
            _logger.LogDebug(ex, "One or more detached llama-server spawns ended while the supervisor was shutting down.");
        }

        foreach (var (key, running) in _processes.ToArray())
        {
            if (_reaper.DetachProcess(key, running) is { } detached)
            {
                LlamaServerIdleReaper.KillDetachedProcess(detached);
            }
        }

        _reaper.Dispose();
        foreach (var gate in _ensureGates.Values)
        {
            gate.Dispose();
        }

        _shutdownCts.Dispose();
        _runtimeMutationGate.Dispose();
    }

    /// <inheritdoc />
    public async Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _runtimeMutationGate.BeginOperation();
        try
        {
            EnsureDecision decision;

            // SHARED: this section orders against operator runtime mutations, not against other ensures. Everything it
            // touches is already safe under concurrency — the reuse probe claim is a CAS, the spawn decision runs under
            // the per-key _ensureGates single-flight, and the process/spawn tables are concurrent — so two ensures for
            // different roles run side by side instead of queueing behind each other's liveness probe.
            await _runtimeMutationGate.EnterSharedAsync(ct).ConfigureAwait(false);
            try
            {
                // Hybrid attach: a configured external endpoint short-circuits spawn/supervision entirely.
                var external = _externalEndpoints.Resolve(modelName, role);
                if (external is not null)
                {
                    return new LlamaServerEndpoint(modelName, role, external);
                }

                if (_sourceBuildActivity.ActiveBuildId is not null)
                {
                    throw new LlamaRuntimeException("A llama.cpp source build is in progress; wait for it to complete before starting a local model.");
                }

                var key = new ProcessKey(modelName, role);

                // Fast path: an already-running, live process is reused without taking the spawn gate — subject to a
                // rate-limited liveness probe so a wedged (alive but unresponsive) process is respawned instead of handed out.
                // A profiling-owned process is never handed out: its teardown evicts unconditionally, so a reuse here would
                // be killed mid-generation. Falling through queues this caller on the per-key gate profiling holds until
                // teardown, after which it spawns its own process.
                if (_processes.TryGetValue(key, out var existing) && !existing.Handle.HasExited && !existing.IsProfilingOwned)
                {
                    var reused = await TryReuseAsync(key, existing, ct).ConfigureAwait(false);
                    if (reused is not null)
                    {
                        return reused;
                    }
                }

                // Decide (under the single-flight gate, held only briefly) between a reuse and joining/starting the DETACHED
                // spawn, then await the spawn WITHOUT binding its lifetime to this caller's token.
                decision = await DecideEnsureAsync(key, ct).ConfigureAwait(false);
                if (decision.Reused is { } reusedEndpoint)
                {
                    return reusedEndpoint;
                }
            }
            finally
            {
                _runtimeMutationGate.ExitShared();
            }

            // DecideEnsureAsync has now registered the detached task in _inflightSpawns. Release the mutation ordering gate
            // before readiness completes: a mutation attempt observes the in-flight spawn and returns null instead of waiting
            // for readiness, while a mutation lease already holding the gate still prevents this ensure from reaching here.
            var running = await AwaitDetachedSpawnAsync(decision.SpawnTask!, ct).ConfigureAwait(false);
            return running.Endpoint;
        }
        finally
        {
            _runtimeMutationGate.EndOperation();
        }
    }

    /// <inheritdoc />
    public Task<ILlamaServerRuntimeMutationLease?> TryAcquireRuntimeMutationLeaseAsync(CancellationToken ct)
    {
        // A live or in-flight process blocks the mutation: swapping the runtime binaries under a loaded model would
        // pull them out from under it.
        return _runtimeMutationGate.TryAcquireLeaseAsync(() => _processes.Values.Any(static process => !process.Handle.HasExited) || !_inflightSpawns.IsEmpty,
            ct);
    }

    /// <inheritdoc />
    public bool IsKeepWarmSuppressed()
    {
        return _runtimeMutationGate.IsMutationActive;
    }

    internal int CountInflightSpawns() =>
        _inflightSpawns.Count;

    /// <summary>The registered process for a key, or <see langword="null" />. Test seam for process-state assertions.</summary>
    internal RunningProcess? GetRegisteredProcess(string modelName, ModelRole role) =>
        _processes.TryGetValue(new ProcessKey(modelName, role), out var running) ? running : null;

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
            _processes.TryGetValue(key, out var existing);

            // Profiling holds this same gate through its own teardown, so a profiling-owned entry cannot be seen here
            // today. The reuse arm is guarded anyway; the reap below is deliberately NOT, so that if the invariant ever
            // breaks a lingering entry is still torn down rather than orphaning its child process.
            var profilingOwned = existing is { IsProfilingOwned: true };

            if (existing is not null && !existing.Handle.HasExited && !profilingOwned)
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
                await _reaper.RemoveProcessAsync(key, existing).ConfigureAwait(false);
            }

            // Join the in-flight detached spawn or start one. GetOrAdd runs its factory at most once here because we hold
            // the gate, so two callers never start two spawns for the same key.
            if (_inflightSpawns.TryGetValue(key, out var inflight))
            {
                return new EnsureDecision(Reused: null, inflight.Task);
            }

            IProcessLaunchTicket? launchTicket = null;
            try
            {
                if (!_launchAdmissions.TryBeginLaunch(key.ModelName, key.Role, out var admission, out launchTicket))
                {
                    throw NonRetryable("The requested local model launch conflicts with another in-flight admission.");
                }

                var started = CreateDetachedSpawn(admission, launchTicket!);
                if (!_inflightSpawns.TryAdd(key, started))
                {
                    return new EnsureDecision(Reused: null, _inflightSpawns[key].Task);
                }

                // The published immutable in-flight record now owns the ticket. Clear the local before starting the
                // detached work so the finally below cannot release a successfully transferred launch reference.
                launchTicket = null;
                try
                {
                    StartDetachedSpawn(key, started);
                    return new EnsureDecision(Reused: null, started.Task);
                }
                catch
                {
                    _inflightSpawns.TryRemove(new KeyValuePair<ProcessKey, InflightSpawn>(key, started));
                    started.LaunchTicket.Dispose();
                    throw;
                }
            }
            finally
            {
                launchTicket?.Dispose();
            }
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
    private static InflightSpawn CreateDetachedSpawn(ProcessLaunchAdmission? admission,
        IProcessLaunchTicket launchTicket)
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

        return new InflightSpawn(completion, admission, launchTicket);
    }

    private void StartDetachedSpawn(ProcessKey key, InflightSpawn inflight)
    {
        _ = Task.Factory.StartNew(async () =>
            {
                RunningProcess? running = null;
                Exception? failure = null;
                try
                {
                    running = await SpawnWithRestartAsync(key, inflight.Admission, _shutdownCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    // Remove THIS immutable in-flight record (key+value) so a newer record under the same key is untouched.
                    _inflightSpawns.TryRemove(new KeyValuePair<ProcessKey, InflightSpawn>(key, inflight));
                    inflight.LaunchTicket.Dispose();
                }

                if (failure is not null)
                {
                    inflight.Completion.SetException(failure);
                }
                else
                {
                    inflight.Completion.SetResult(running!);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            _detachedSpawnScheduler).Unwrap();
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

    private sealed record InflightSpawn(
        TaskCompletionSource<RunningProcess> Completion,
        ProcessLaunchAdmission? Admission,
        IProcessLaunchTicket LaunchTicket)
    {
        public Task<RunningProcess> Task => Completion.Task;
    }

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
        await _reaper.RemoveProcessAsync(key, existing).ConfigureAwait(false);
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
        _runtimeMutationGate.BeginOperation();
        try
        {
            await EvictCoreAsync(modelName, role).ConfigureAwait(false);
        }
        finally
        {
            _runtimeMutationGate.EndOperation();
        }
    }

    private async Task EvictCoreAsync(string modelName, ModelRole role)
    {
        var key = new ProcessKey(modelName, role);
        if (_processes.TryGetValue(key, out var running))
        {
            await _reaper.RemoveProcessAsync(key, running).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task EvictAllRolesAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _runtimeMutationGate.BeginOperation();
        try
        {
            await EvictAllRolesCoreAsync(modelName).ConfigureAwait(false);
        }
        finally
        {
            _runtimeMutationGate.EndOperation();
        }
    }

    private async Task EvictAllRolesCoreAsync(string modelName)
    {
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            await EvictCoreAsync(modelName, role).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Profiling's pre-spawn eviction: lease-aware and two-phase, unlike the operator <see cref="EvictCoreAsync" />
    ///     force path. Every live role for the model is CLAIMED first through
    ///     <see cref="RunningProcess.TryBeginEvict(out long)" /> — the same atomic check-and-mark cap admission uses —
    ///     and only a complete set of claims is torn down. A role serving in-flight inference refuses its claim, the
    ///     claims already taken are released with <see cref="RunningProcess.ReleaseEvictionClaim" /> (an abandoned claim
    ///     would refuse every future lease on a process nobody is tearing down; an ownership-blind clear would erase an
    ///     operator eject's own mark instead), and the caller is told which role refused: a measurement
    ///     is never worth killing a live generation for, and a half-evicted model for a run that never happens is worse
    ///     than no eviction at all. Returns the refusing role, what it was serving and why, or <see langword="null" />
    ///     when every role was evicted.
    /// </summary>
    private async Task<(ModelRole Role, int ActiveLeases, LlamaServerProfilingRefusalReason Reason)?> TryEvictAllRolesForProfilingAsync(string modelName)
    {
        var claimed = new List<(ProcessKey Key, RunningProcess Process, long Claim)>();
        var exited = new List<(ProcessKey Key, RunningProcess Process, long Claim)>();
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            var key = new ProcessKey(modelName, role);
            if (!_processes.TryGetValue(key, out var running))
            {
                continue;
            }

            // An exited process holds no real lease: it is reaped with the rest so its slot and port are reclaimed,
            // and it is never claimed, so a refusal leaves an operator eject's own mark on it untouched.
            if (running.Handle.HasExited)
            {
                exited.Add((key, running, Claim: 0));
                continue;
            }

            if (!running.TryBeginEvict(forProfiling: true, out var claim, out var alreadyEvicting))
            {
                foreach (var (_, claimedProcess, claimedToken) in claimed)
                {
                    claimedProcess.ReleaseEvictionClaim(claimedToken);
                }

                // Sampled HERE, in the refusal branch: a lost compare-exchange means another teardown owns the process
                // and there is no lease count to report, so the reason carries the meaning instead of a made-up number.
                var activeLeases = alreadyEvicting ? 0 : running.ActiveLeases;
                var reason = alreadyEvicting
                    ? LlamaServerProfilingRefusalReason.EvictionAlreadyInProgress
                    : LlamaServerProfilingRefusalReason.InUse;
                _logger.LogInformation("Profiling for model {ModelName} was skipped: role {Role} could not be claimed ({Reason}, {ActiveLeases} in-flight request(s)).",
                    modelName, role, reason, activeLeases);
                return (role, activeLeases, reason);
            }

            claimed.Add((key, running, claim));
        }

        foreach (var (key, running, _) in exited.Concat(claimed))
        {
            await _reaper.RemoveProcessAsync(key, running).ConfigureAwait(false);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _runtimeMutationGate.BeginOperation();
        try
        {
            return await EjectCoreAsync(modelName, role, force, ct).ConfigureAwait(false);
        }
        finally
        {
            _runtimeMutationGate.EndOperation();
        }
    }

    private async Task<LlamaServerEjectOutcome> EjectCoreAsync(string modelName, ModelRole role, bool force, CancellationToken ct)
    {
        var key = new ProcessKey(modelName, role);
        if (!_processes.TryGetValue(key, out var target) || target.Handle.HasExited)
        {
            // Nothing live to eject. Reap a lingering dead entry so its slot/port frees, then report an idempotent no-op.
            if (target is not null)
            {
                await _reaper.RemoveProcessAsync(key, target).ConfigureAwait(false);
            }

            return LlamaServerEjectOutcome.NotRunning;
        }

        // Mark evicting: new inference leases are refused (TryAcquireInferenceLease returns null) so the active-lease
        // count can only fall while we drain. The process stays registered and reusable until we tear it down or give up.
        // The claim is kept so every release below clears only THIS eject's mark, never one a later teardown took over.
        var evictionClaim = target.MarkEvicting();
        _logger.LogInformation("Operator eject requested for model {ModelName} role {Role} (force: {Force}); draining {ActiveLeases} in-flight request(s).",
            key.ModelName, key.Role, force, target.ActiveLeases);

        bool drained;
        try
        {
            drained = await DrainLeasesAsync(target, ct).ConfigureAwait(false);
        }
        catch
        {
            // The drain itself was aborted (the eject request was cancelled mid-drain): no teardown happened, so the
            // evicting mark must not outlive this call — left set, the process would refuse every future lease forever.
            target.ReleaseEvictionClaim(evictionClaim);
            throw;
        }

        if (drained)
        {
            await _reaper.RemoveProcessAsync(key, target).ConfigureAwait(false);
            _logger.LogInformation("Operator eject completed for model {ModelName} role {Role}: drained and torn down.", key.ModelName, key.Role);
            return LlamaServerEjectOutcome.Ejected;
        }

        if (force)
        {
            // Force: tear down despite in-flight work. Mark ejected FIRST so the interrupted request's leaseholder can
            // classify the resulting connection failure as an operator eject rather than a generic provider drop.
            target.MarkEjected();
            await _reaper.RemoveProcessAsync(key, target).ConfigureAwait(false);
            _logger.LogWarning("Operator eject FORCED for model {ModelName} role {Role}: {ActiveLeases} in-flight request(s) interrupted.",
                key.ModelName, key.Role, target.ActiveLeases);
            return LlamaServerEjectOutcome.ForcedWhileBusy;
        }

        // Busy and not forced: never kill silently. Leave the process running and usable, and report that the eject
        // could not complete safely so the caller can decide (retry / force).
        target.ReleaseEvictionClaim(evictionClaim);
        _logger.LogInformation("Operator eject for model {ModelName} role {Role} did not complete: still busy after the drain window; left running.", key.ModelName, key.Role);
        return LlamaServerEjectOutcome.TimedOutStillBusy;
    }

    /// <inheritdoc />
    public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var key = new ProcessKey(modelName, role);

        if (!_processes.TryGetValue(key, out var running) || running.Handle.HasExited)
        {
            return LlamaServerLeaseAcquisition.NotRunning;
        }

        // A profiling-owned process is invisible to inference: callers ensure first and look the lease up by key
        // afterwards, so without this a chat whose own process was replaced in between would lease the transient
        // profiling process and be killed by its teardown. Reported as its OWN refusal rather than as NotRunning:
        // "not running" licenses the caller to proceed leaseless against the endpoint it already resolved, and the
        // port allocator commonly hands the measurement spawn the port the replaced process just freed — so that
        // caller would reach the profiling process anyway. The caller must re-ensure instead.
        if (running.IsProfilingOwned)
        {
            return LlamaServerLeaseAcquisition.ProfilingOwned;
        }

        // A draining eject refuses new leases — and the refusal REASON is surfaced so the caller fails the request as
        // operator-ejected instead of running it leaseless under the drain (untracked by the drain, killed mid-flight
        // by the teardown, and then self-heal-respawning the just-ejected model).
        var eviction = running.EvictionOwner;
        if (eviction != 0)
        {
            return Refusal(eviction);
        }

        // Acquire, then RE-CHECK evicting/exited: an eject that flipped the flag between the guard above and here must
        // not gain a lease that would extend its drain — release and refuse, classifying the refusal at this instant.
        running.AcquireLease();
        eviction = running.EvictionOwner;
        if (eviction != 0 || running.Handle.HasExited)
        {
            running.ReleaseLease();
            return eviction != 0 ? Refusal(eviction) : LlamaServerLeaseAcquisition.NotRunning;
        }

#pragma warning disable CA2000 // Ownership of the lease transfers to the caller inside the returned acquisition; the interface contract obliges the caller to dispose it.
        return LlamaServerLeaseAcquisition.Granted(new InferenceLease(running));
#pragma warning restore CA2000
    }

    /// <summary>
    ///     Classifies a refusal against a process whose teardown has begun. A profiling pre-spawn eviction is a
    ///     transient benchmark spawn, not an operator eject: reported as the latter, a chat fails terminally with
    ///     "the model is being ejected by the operator" for something that clears itself in seconds, and embedding and
    ///     rerank misreport the same way. Reported as its own refusal the caller lands in the bounded re-ensure arm.
    ///     Takes the owning claim the caller already read, so the classification cannot straddle two reads.
    /// </summary>
    private static LlamaServerLeaseAcquisition Refusal(long evictionOwner) =>
        evictionOwner < 0 ? LlamaServerLeaseAcquisition.ProfilingOwned : LlamaServerLeaseAcquisition.Evicting;

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
        // A profiling-owned process is excluded: its context comes from explore/replay launch args, not from serving
        // policy, so reporting it would size a chat's context budget off a measurement spawn.
        var key = new ProcessKey(modelName, role);
        if (_processes.TryGetValue(key, out var running)
            && !running.Handle.HasExited
            && !running.IsProfilingOwned
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
                healths.Add(new LlamaServerProcessHealth(key.ModelName, key.Role, IsResponsive: false, "Process has exited.", HasExited: true));
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
    private async Task<RunningProcess> SpawnWithRestartAsync(ProcessKey key,
        ProcessLaunchAdmission? admission,
        CancellationToken ct)
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
                return await SpawnOnceAsync(key, admission, ct).ConfigureAwait(false);
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
    private Task<RunningProcess> SpawnOnceAsync(ProcessKey key,
        ProcessLaunchAdmission? admission,
        CancellationToken ct)
    {
        // The resolver is awaited inside the core (after variant selection, before admission) exactly as before — a
        // slow profile read never stalls admission for other keys. No startup capture, no forced --metrics. The launch
        // policy (deterministic -c, GPU KV/FA, CPU threads) applies to this normal serving path.
        return SpawnCoreAsync(key,
            (variant, c) => _profileResolver.ResolveAsync(key.ModelName, key.Role, variant, c),
            startupCapture: null,
            fitParamsCapture: null,
            ensureMetrics: false,
            applyLaunchPolicy: true,
            admission,
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
    ///     <paramref name="startupCapture" />, <paramref name="fitParamsCapture" />, and
    ///     <paramref name="ensureMetrics" /> are their normal-path defaults (<see langword="null" />,
    ///     <see langword="null" />, <see langword="false" />), the built spec is identical to the legacy spawn.
    /// </remarks>
    private async Task<RunningProcess> SpawnCoreAsync(ProcessKey key,
        Func<GpuVariant, CancellationToken, Task<ResolvedLaunchArguments>> resolveArgs,
        Action<string>? startupCapture,
        Action<string>? fitParamsCapture,
        bool ensureMetrics,
        bool applyLaunchPolicy,
        ProcessLaunchAdmission? admission,
        CancellationToken ct,
        LlamaServerBenchmarkLaunchPolicy? benchmarkPolicy = null,
        bool profilingOwned = false)
    {
        var modelFilePath = await _modelStore.ResolveModelFilePathAsync(key.ModelName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modelFilePath))
        {
            throw NonRetryable("The requested model is not installed.");
        }

        // A LoRA-adapter model has no weights of its own: llama-server loads the installed BASE model and applies this
        // entry's file as --lora. Resolving it here (rather than inside the spec builder) keeps every size-derived
        // decision below — the readiness deadline in particular — accounting for the bytes actually loaded.
        string? adapterFilePath = null;
        var adapterSizeBytes = 0L;
        try
        {
            if (await _modelStore.ResolveAdapterLaunchAsync(key.ModelName, ct).ConfigureAwait(false) is { } adapterLaunch)
            {
                modelFilePath = adapterLaunch.BaseModelFilePath;
                adapterFilePath = adapterLaunch.AdapterFilePath;
                adapterSizeBytes = adapterLaunch.AdapterSizeBytes;
            }
        }
        catch (GgufAdapterBaseModelMissingException exception)
        {
            throw NonRetryable(exception.Message);
        }

        // A vision model's mmproj projector companion — passed to llama-server as --mmproj so it accepts image input.
        // Chat role only (embedding/reranker never take images); null for a text-only model, which gets no --mmproj.
        var projectorFilePath = key.Role == ModelRole.Chat
            ? await _modelStore.ResolveProjectorFilePathAsync(key.ModelName, ct).ConfigureAwait(false)
            : null;

        // The cold-start readiness deadline scales with the on-disk model size — a large model loads
        // proportionally slower, so a fixed constant would kill and retry it before it can finish (the audited hang). A
        // missing/unreadable size (0) falls back to the base timeout.
        var readinessTimeout = _options.ResolveReadinessTimeout(TryGetFileSizeBytes(modelFilePath) + adapterSizeBytes);

        // A chat-role EXTERNAL-DRAFT speculative mode needs its draft GGUF present before launch — a missing file would
        // otherwise start a server that dies cryptically. Deterministic misconfiguration → non-retryable (mirrors the
        // model-not-installed guard above). draft-mtp, ngram-*, and disabled modes never reach this: MTP drafts from
        // heads inside the main model, so there is no second GGUF to find (RequiresExternalDraftModel is false).
        // The operator selects a draft model by NAME (installed chat model); resolve it to its on-disk GGUF the same way
        // the target model is resolved above so the effective launch args carry a real path. An explicit path override
        // (SpeculativeDraftModelPath), when set, wins and skips resolution.
        var launchTuning = LlamaServerLaunchArgumentComposer.ResolveChatLaunchTuning(benchmarkPolicy, _options);
        var speculative = launchTuning.Speculative;
        if (key.Role == ModelRole.Chat && speculative.RequiresExternalDraftModel)
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
        else if (key.Role == ModelRole.Chat
                 && speculative.ModeClass is SpeculativeModeClass.MainModelHeads
                 && (!string.IsNullOrWhiteSpace(speculative.DraftModelPath) || !string.IsNullOrWhiteSpace(_options.SpeculativeDraftModelName)))
        {
            // Ignored, not rejected: settings saved before this contract was corrected were REQUIRED to name a draft
            // model for draft-mtp, so rejecting would turn every such install into a non-retryable launch failure on
            // upgrade. Clearing the path keeps the launch spec honest — nothing downstream can emit a stale draft flag.
            speculative = speculative with
            {
                DraftModelPath = null
            };

            _logger.LogInformation("Speculative mode {Mode} drafts from the main model's own MTP heads; the configured draft model is ignored.",
                speculative.NormalizedMode);
        }

        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        if (admission is not null)
        {
            if (variant != admission.Variant)
            {
                throw NonRetryable("The admitted local model launch no longer matches the selected runtime.");
            }

            var facts = await _modelStore.ResolveModelFootprintFactsAsync(key.ModelName, ct).ConfigureAwait(false);
            var contentIdentity = facts?.ContentIdentity ?? $"{key.ModelName}:{facts?.FileSizeBytes ?? TryGetFileSizeBytes(modelFilePath)}";
            if (!string.Equals(contentIdentity, admission.Allocation.ContentIdentity, StringComparison.Ordinal))
            {
                throw NonRetryable("The admitted local model launch no longer matches the installed model.");
            }
        }

        var binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);

        // Everything below keys off `variant`: the VRAM admission gate, the placement sniffer, the launch policy and,
        // through LlamaServerLaunchProjection, every GPU argument (it gates -ngl/--fit/-ctk on `variant != Cpu`). A
        // serve can hand back a build of a DIFFERENT variant than was asked for — the managed source-build record is
        // authoritative and wins when the selector's cached signal has not been seeded yet — so follow the binary that
        // is actually being launched. Selecting Cpu and serving a CUDA build would otherwise spawn it with no offload
        // at all. The admission identity check above deliberately stays on the SELECTED variant: that is the variant
        // the admission was granted against.
        variant = binary.Variant;
        var capabilityManifest = await _capabilityManifestProbe.GetManifestAsync(binary, ct).ConfigureAwait(false);
        if (!capabilityManifest.ProbeSucceeded)
        {
            throw NonRetryable("The selected llama.cpp runtime could not report its supported server options. Reinstall or rebuild the runtime and try again.");
        }

        // Resolve the launch args (frozen-profile replay or explore-mode auto-fit, or operator-supplied profiling args)
        // for this (model, role, backend) BEFORE taking the admission gate, so a slow profile read never stalls
        // admission for other keys.
        // The ticket describes the variant it was granted against, which the override above can have moved off. Once it
        // has, NOTHING the ticket carries applies to this spawn: its arguments were resolved for another backend, and
        // its allocation was sized for one — a CPU admission carries no GPU bytes, so spending it on a GPU load would
        // put that load outside VRAM capacity accounting and let concurrent spawns oversubscribe the device. Drop it
        // and take the unadmitted path, which resolves both against the variant actually being launched.
        var admitted = admission?.Variant == variant ? admission : null;
        var resolved = admitted?.ResolvedArguments ?? await resolveArgs(variant, ct).ConfigureAwait(false);

        // Per-model developer/advanced override: extra llama-server flags the operator typed. Resolved here (alongside
        // the profile args, BEFORE the admission gate) so a slow store read never stalls admission for other keys, and
        // ONLY on the normal serving path — a benchmark/profiling spawn (applyLaunchPolicy false) must stay a pure
        // measurement, so the operator's experimentation flags never perturb it. The app-managed flags are already
        // stripped by the resolver: reachability (-m/--model/--host/--port) AND the memory-fit placement family
        // (-c/-ngl/-ts/-ot/-ctk/-ctv/-fa/--parallel/-b/-ub), whose values the allocation + policy above already decided
        // and recorded in the ledger — so what remains is sampling/decoding tuning only. Those are appended after the
        // built spec below, where a later scalar flag overrides the bundled tuning default (llama.cpp is last-wins).
        // Never throws.
        var extraLaunchArgs = applyLaunchPolicy
            ? await _extraArgumentsResolver.ResolveAsync(key.ModelName, key.Role, ct).ConfigureAwait(false)
            : [];

        // Serialize the spawn-through-readiness window of GPU-backed loads process-wide (shared with the image
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

        // The central launch policy fills in the deterministic context (-c), the GPU KV-cache
        // quantization + flash-attention optimization, and the CPU thread policy the audited launch defaults omitted.
        // Replay profiling bypasses it so the supplied frozen args ARE the experiment. Explore profiling applies the
        // production policy because the helper and server must observe the same concrete context/KV/FA vector as normal
        // serving; otherwise the fit helper (behavior observed on b9692) can report unchanged `-c 0 -ngl -1` defaults
        // that are not replayable placement.
        var planSet = await BuildLaunchPlanCandidatesAsync(key,
                variant,
                resolved,
                applyLaunchPolicy,
                admitted?.Allocation,
                ct)
            .ConfigureAwait(false);
        var planCandidates = planSet.Candidates;

        Exception? optimizedFailure = null;
        for (var attempt = 0; attempt < planCandidates.Count; attempt++)
        {
            var candidate = planCandidates[attempt];
            var isSafeRetry = candidate.AttemptKind == LlamaServerLoadAttemptKind.SafeRetry;
            var port = await _reaper.AdmitAndAllocatePortAsync(ct).ConfigureAwait(false);

            ILlamaServerProcessHandle? handle = null;
            long? readinessStartedTimestamp = null;
            var readinessRecorded = false;
            var automaticCapture = applyLaunchPolicy ? new LlamaServerBoundedStartupCapture() : null;

            // Latches llama.cpp's layer-placement banner out of the streamed startup output. It is deliberately NOT
            // read off automaticCapture: that buffer is bounded, and at the verbosity the banner requires it is
            // printed around line 155 — outside any small window.
            var placementSniffer = variant == GpuVariant.Cpu ? null : new LlamaServerLayerPlacementSniffer();

            // Flipped once this child is serving, to demote its (raised-verbosity) request chatter to Debug. It stays
            // false for the whole load, and forever on a spawn that never reaches readiness, so the placement banner
            // and every failure message are still logged at Information.
            var servingWindow = new LlamaServerDiagnosticVerbosityWindow();
            try
            {
                var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(key, binary.ServerExecutablePath, modelFilePath, port, variant, candidate.Resolved,
                    launchTuning.ChatCacheReuse,
                    speculative,
                    candidate.Plan,
                    launchTuning.ChatCacheRamMiB,
                    projectorFilePath,
                    adapterFilePath);

                // Append the operator's per-model extra flags LAST so they win over the bundled tuning defaults
                // (llama.cpp is last-wins for scalar flags). Placed BEFORE the diagnostic --metrics / -lv fill-ins below
                // so those checks see an operator-supplied --metrics/-lv and do not duplicate it.
                if (extraLaunchArgs.Count > 0)
                {
                    spec = spec with
                    {
                        Arguments = [.. spec.Arguments, .. extraLaunchArgs]
                    };
                }

                // Benchmark spawns need /metrics on ANY variant. Both GPU modes emit it themselves, so this now only
                // fills in the CPU case — and guards against a future spec shape that omits it.
                if (ensureMetrics && !spec.Arguments.Contains("--metrics", StringComparer.Ordinal))
                {
                    spec = spec with
                    {
                        Arguments = [.. spec.Arguments, "--metrics"]
                    };
                }

                // Raise log verbosity just enough to make llama.cpp print how many layers actually landed on the GPU.
                // At the server default the whole startup is 11 lines and says nothing about placement, so a model
                // whose weights spilled into system RAM is indistinguishable from one that fully fit.
                //
                // EVERY GPU spawn pays this, deliberately. Placement under auto-fit is decided against the FREE VRAM at
                // load time, so the same model can be fully resident when loaded alone and partly resident when loaded
                // beside two others — measuring once and reusing the answer would report a number that is no longer
                // true. Each spawn is a fresh process, so each gets a fresh reading. The sink cost is paid back by
                // demoting this child's request chatter to Debug once it is serving (see servingWindow).
                //
                // The operator-profiling EXPLORE path is skipped because it already raises verbosity to maximum below.
                // A benchmark spawn is the exception among profiling spawns: it takes a fit-params capture like every
                // other profiling spawn, but it never reaches the explore `-v` branch (its args are a replay), so
                // without this it was the one measurement that could not say where its own layers landed — exactly the
                // spawn whose placement a later reader most needs. It pays the same servingWindow demotion.
                if ((fitParamsCapture is null || benchmarkPolicy is not null)
                    && placementSniffer is not null
                    && variant != GpuVariant.Cpu
                    && !LlamaServerLaunchArgumentComposer.HasVerbosityArgument(spec.Arguments))
                {
                    spec = spec with
                    {
                        Arguments = [.. spec.Arguments, "-lv", PlacementProbeLogVerbosity],
                        ShouldDemoteForwardedLines = servingWindow.IsServing
                    };
                }

                // Operator profiling spawns capture both pipes; the normal path leaves the sink null (spec unchanged).
                if (startupCapture is not null || automaticCapture is not null || placementSniffer is not null)
                {
                    // The sink is wired for the process's LIFETIME, but the two automatic buffers behind it are
                    // startup-only: the placement banner is read at readiness and the failure-classifier window is only
                    // ever read from this attempt's catch block, which readiness has ruled out. Detaching them at
                    // readiness turns every serving-time forwarded line from a Lock + string copy into one volatile
                    // read. The operator profiling sink is NOT detached — that output was explicitly requested.
                    var isServing = servingWindow.IsServing;
                    spec = spec with
                    {
                        StartupCapture = line =>
                        {
                            startupCapture?.Invoke(line);
                            if (isServing())
                            {
                                return;
                            }

                            automaticCapture?.Add(line);
                            placementSniffer?.Add(line);
                        }
                    };
                }

                if (fitParamsCapture is not null
                    && candidate.Resolved.ExploreMode
                    && variant != GpuVariant.Cpu
                    && !spec.Arguments.Contains("-v", StringComparer.Ordinal)
                    && !spec.Arguments.Contains("--verbose", StringComparer.Ordinal))
                {
                    // The fit helper (observed on b9692) leaves -ngl at its automatic sentinel when the initial
                    // placement already fits. Verbose
                    // load_tensors output is the authoritative proof that automatic placement meant every layer was
                    // offloaded; the fit parser uses that proof to normalize replay to explicit all-layers (-2).
                    spec = spec with
                    {
                        Arguments = [.. spec.Arguments, "-v"]
                    };
                }

                var capabilityDecision = LlamaServerCapabilityGate.Apply(spec, capabilityManifest, ensureMetrics);
                if (!capabilityDecision.IsCompatible)
                {
                    throw CapabilityIncompatible(capabilityDecision.SanitizedError!, capabilityDecision.CanTrySafeFallback);
                }

                spec = capabilityDecision.Spec;
                if (capabilityDecision.OmittedOptions.Count > 0)
                {
                    _logger.LogWarning("The selected llama-server runtime lacks optional capabilities {Options}; those launch optimizations were omitted.",
                        string.Join(", ", capabilityDecision.OmittedOptions));
                }

                IReadOnlyList<string>? fittedArgsForSuccessfulAttempt = null;
                if (fitParamsCapture is not null && candidate.Resolved.ExploreMode && variant != GpuVariant.Cpu)
                {
                    var fitResult = await _fitParamsRunner.RunAsync(spec, ct).ConfigureAwait(false);
                    if (fitResult.Status == LlamaFitParamsRunStatus.Succeeded)
                    {
                        fittedArgsForSuccessfulAttempt = fitResult.StandardOutput;
                    }
                    else if (fitResult.Status == LlamaFitParamsRunStatus.MissingCapability)
                    {
                        _logger.LogWarning(
                            "The resolved llama.cpp runtime does not expose the sibling llama-fit-params capability; the live explore spawn will remain auto-fit, but no placement profile will be drafted.");
                    }
                    else
                    {
                        _logger.LogWarning("llama-fit-params acquisition failed ({Reason}); the live explore spawn will remain auto-fit, but no placement profile will be drafted.",
                            fitResult.FailureReason ?? "unknown failure");
                    }
                }

                readinessStartedTimestamp = _timeProvider.GetTimestamp();
                handle = _launcher.Launch(spec);
                _logger.LogInformation("llama-server spawned for model {ModelName} role {Role} (pid {ProcessId}, port {Port}){LaunchPlan}.",
                    key.ModelName, key.Role, handle.ProcessId, port, LlamaServerLaunchArgumentComposer.DescribeLaunchPlan(candidate.Plan));

                await WaitForReadyOrExitAsync(handle, spec.BaseAddress, readinessTimeout, ct).ConfigureAwait(false);
                var readinessDuration = _timeProvider.GetElapsedTime(readinessStartedTimestamp.Value);
                _logger.LogInformation("llama-server ready for model {ModelName} role {Role} (pid {ProcessId}) after {ElapsedMs:F0} ms (readiness budget {BudgetSeconds:F0}s).",
                    key.ModelName, key.Role, handle.ProcessId, readinessDuration.TotalMilliseconds, readinessTimeout.TotalSeconds);

                var placement = RecordObservedLayerPlacement(key, variant, placementSniffer);
                var loadObservation = RecordLoadTelemetry(key,
                    variant,
                    capabilityManifest.Version ?? binary.Version,
                    capabilityManifest.ExecutableSha256,
                    readinessDuration,
                    LlamaServerReadinessOutcome.Ready,
                    placement.Outcome,
                    candidate.AttemptKind,
                    speculative);
                readinessRecorded = true;

                // The load window is over and the banner has been read. From here the child's raised-verbosity output is
                // per-request chatter nobody asked to persist: drop it to Debug AND detach the automatic startup
                // capture (same latch, see the StartupCapture wiring above). Deliberately after
                // RecordObservedLayerPlacement, and never reached on a spawn that failed to become ready — both
                // buffers must stay live for the whole load window.
                servingWindow.MarkServing();

                // Publish helper output only for the candidate that actually reached readiness. If the optimized
                // production policy failed and the safe plan retried, output from the failed candidate must not be frozen.
                if (fittedArgsForSuccessfulAttempt is not null)
                {
                    foreach (var line in fittedArgsForSuccessfulAttempt)
                    {
                        fitParamsCapture!(line);
                    }
                }

                // Read the effective per-slot context the server actually loaded (best-effort) so both app-side
                // budgeters and the UI meter size against the REAL window rather than the requested/advertised one.
                var effectiveContext = await TryReadEffectiveContextAsync(spec.BaseAddress, ct).ConfigureAwait(false);

                // A benchmark spawn — and only a benchmark spawn — records what it actually launched, once the process is
                // genuinely serving. Assembly is non-throwing by construction (every unreadable fact becomes null), so
                // a receipt can never turn a healthy measurement into a failed run.
                //
                // The projection is read back out of the FINAL argv rather than recomputed from (variant, resolved,
                // plan, role, tuning): the capability gate above can drop an optional flag the intended projection
                // still claims, and the operator's extra arguments were appended after it. An unparseable vector falls
                // back to the intended shape — a describable launch is worth more than no receipt at all.
                var launchReceipt = benchmarkPolicy is null
                    ? null
                    : BuildBenchmarkLaunchReceipt(variant,
                        capabilityManifest.Version ?? binary.Version,
                        capabilityManifest.ExecutableSha256,
                        LlamaServerLaunchProjection.TryFromArguments(spec.Arguments)
                        ?? LlamaServerLaunchProjection.From(variant,
                            candidate.Resolved,
                            candidate.Plan,
                            key.Role,
                            launchTuning.ChatCacheReuse,
                            launchTuning.ChatCacheRamMiB),
                        new LlamaServerLaunchAuxAssets(!string.IsNullOrWhiteSpace(adapterFilePath),
                            !string.IsNullOrWhiteSpace(projectorFilePath),
                            !string.IsNullOrWhiteSpace(speculative.DraftModelPath)),
                        placement,
                        effectiveContext,
                        benchmarkPolicy,
                        handle.ProcessId,
                        capabilityDecision.OmittedOptions);

                var endpoint = new LlamaServerEndpoint(key.ModelName, key.Role, spec.BaseAddress);
                var running = new RunningProcess(handle, endpoint, port, _timeProvider.GetUtcNow())
                {
                    EffectiveContextTokens = effectiveContext,
                    SuccessfulLaunchArguments = fitParamsCapture is null ? [] : [.. spec.Arguments],
                    LoadObservation = loadObservation,
                    LaunchReceipt = launchReceipt,
                    IsProfilingOwned = profilingOwned
                };
                _processes[key] = running;

                if (isSafeRetry)
                {
                    // The safe config reached readiness where the optimized (KV-quant + flash-attention) config could
                    // not — so the optimized config is the culprit for THIS backend (not a broken model, which would
                    // fail the safe config too). Record it so subsequent spawns skip the known-bad optimized config.
                    // WithoutKvCacheQuantization() leaves the plan's KvCacheType intact, so the verdict is keyed on
                    // the node's CURRENT selection. On the replay branch that is not read off the frozen profile: it
                    // coincides with the profile's frozen type only because D13 stales a profile whose frozen type
                    // differs from the selection, so a mismatching pair cannot replay in the first place.
                    if (candidate.Plan is { CpuMoe: true })
                    {
                        // An expert-offload spawn is the most VRAM-marginal launch on the box, so a one-shot success
                        // without KV quantization proves nothing about KV: the primary may have failed on placement or
                        // transient pressure. Recording it would disable the optimized config for EVERY model on this
                        // backend from one model's failure, so it is logged as inconclusive instead.
                        _logger.LogInformation("Safe-retry readiness for expert-offload model {ModelName} role {Role} is inconclusive about the optimized KV config; nothing recorded for backend {Variant}.",
                            key.ModelName,
                            key.Role,
                            variant);
                    }
                    else if (candidate.Plan is { } safeRetryPlan)
                    {
                        await _launchPolicy.RecordOptimizedConfigFailedAsync(variant, safeRetryPlan.KvCacheType, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        // Unreachable today: every SafeRetry candidate is built from a plan. An empty key would be
                        // unmatchable by every read, silently discarding the verdict, so say so instead.
                        _logger.LogWarning("Safe-retry readiness for model {ModelName} role {Role} carried no launch plan; the optimized-config failure was not recorded.",
                            key.ModelName,
                            key.Role);
                    }
                }

                return running;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                RecordIncompleteReadinessAttempt(LlamaServerReadinessOutcome.Cancelled);
                handle?.TreeKill();
                handle?.Dispose();
                await _reaper.ReleaseReservedPortAsync(port).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                RecordIncompleteReadinessAttempt(LlamaServerReadinessOutcome.Failed);
                // Launch/readiness failed: tree-kill the half-started child and free its reserved port (under the
                // admission gate, since the reserved-port set backs the cap count) before deciding whether to fall back.
                handle?.TreeKill();
                handle?.Dispose();
                await _reaper.ReleaseReservedPortAsync(port).ConfigureAwait(false);

                // A capability rejection is known before process launch, so retrying an optimized/safe candidate cannot
                // change the outcome. Other non-retryable errors can still be caused by the optimized child exiting
                // during load; preserve the paid-for one-shot safe KV/FA fallback for that case.
                if (ex.Data.Contains(CapabilityIncompatibleMarker))
                {
                    if (ex.Data.Contains(CapabilitySafeFallbackMarker)
                        && !isSafeRetry
                        && attempt + 1 < planCandidates.Count)
                    {
                        optimizedFailure = ex;
                        _logger.LogWarning(
                            "The selected llama-server runtime does not support the optimized KV-cache/Flash Attention vector for model {ModelName} role {Role}; using the explicit safe candidate.",
                            key.ModelName,
                            key.Role);
                        continue;
                    }

                    throw;
                }

                // The OPTIMIZED attempt failed and a safe candidate remains: remember the error and retry ONCE with the
                // safe (KV/FA off) config. Any other failure (the safe attempt, or a spawn with no fallback candidate)
                // propagates exactly as before — including the readiness-timeout marker the restart loop keys on.
                if (!isSafeRetry && attempt + 1 < planCandidates.Count)
                {
                    optimizedFailure = ex;
                    _logger.LogWarning(ex,
                        "llama-server optimized launch (KV-cache quant + flash attention) failed for model {ModelName} role {Role} on backend {Variant}; retrying once with the safe config.",
                        key.ModelName, key.Role, variant);
                    continue;
                }

                if (automaticCapture is not null
                    && LlamaStartupFailureClassifier.Classify(automaticCapture.Snapshot()) == LlamaStartupFailureKind.OutOfMemory
                    && planSet.Allocation is not null
                    && _allocationResolver.TryDownTierAfterOutOfMemory(planSet.Allocation, out var downTiered))
                {
                    planSet = planSet with
                    {
                        Allocation = downTiered
                    };
                    var downTierPlan = await _launchPolicy.ResolveAsync(key.Role, variant, candidate.Resolved, downTiered, ct).ConfigureAwait(false);
                    planCandidates.Add(candidate with
                    {
                        Plan = candidate.Plan is { UseKvCacheQuantization: false }
                            ? downTierPlan.WithoutKvCacheQuantization()
                            : downTierPlan
                    });
                    _logger.LogWarning("llama-server automatic context allocation encountered a classified startup OOM; retrying at context tier {ContextTokens}.",
                        downTiered.ProcessContextTokens);
                    continue;
                }

                throw;
            }

            void RecordIncompleteReadinessAttempt(LlamaServerReadinessOutcome outcome)
            {
                if (readinessRecorded || readinessStartedTimestamp is not { } started)
                {
                    return;
                }

                _ = RecordLoadTelemetry(key,
                    variant,
                    capabilityManifest.Version ?? binary.Version,
                    capabilityManifest.ExecutableSha256,
                    _timeProvider.GetElapsedTime(started),
                    outcome,
                    variant == GpuVariant.Cpu ? LlamaServerPlacementOutcome.Cpu : LlamaServerPlacementOutcome.Unknown,
                    candidate.AttemptKind,
                    speculative);
                readinessRecorded = true;
            }
        }

        // Unreachable: the loop returns on success or throws on the final candidate; the fallback keeps the analyzer happy.
        throw optimizedFailure ?? new InvalidOperationException("llama-server spawn produced no launch attempt.");
    }

    private Task<LlamaServerLaunchPlanSet> BuildLaunchPlanCandidatesAsync(ProcessKey key,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        bool applyLaunchPolicy,
        ProcessContextAllocation? admittedAllocation,
        CancellationToken ct)
    {
        var builder = new LlamaServerLaunchCandidateBuilder(_allocationResolver, _launchPolicy);
        return builder.BuildAsync(key, variant, resolved, applyLaunchPolicy, admittedAllocation, NonRetryable, ct);
    }

    /// <summary>
    ///     Publishes a sniffed layer-placement observation once the process is genuinely serving. Recording only after
    ///     readiness keeps a candidate that printed a banner and then failed to start out of the operator-facing report.
    ///     A partial or zero offload is logged as a warning: the model serves, but a share of its layers — or all of
    ///     them — run from system RAM. The raw counts travel with the class so a reader sees 38/49 rather than only
    ///     "partial".
    /// </summary>
    private LlamaServerLaunchPlacement RecordObservedLayerPlacement(ProcessKey key,
        GpuVariant variant,
        LlamaServerLayerPlacementSniffer? sniffer)
    {
        if (sniffer is null || !sniffer.TryGetObservation(out var offloaded, out var total))
        {
            return new LlamaServerLaunchPlacement(variant == GpuVariant.Cpu ? LlamaServerPlacementOutcome.Cpu : LlamaServerPlacementOutcome.Unknown,
                OffloadedLayers: null,
                TotalLayers: null);
        }

        _layerPlacementReport.Record(key.Role, variant, key.ModelName, offloaded, total);

        // 0/N is its own outcome, not the extreme end of a partial offload: a GPU build serving entirely from system RAM
        // is a different fact about a measurement than one that placed most of its layers.
        if (offloaded <= 0)
        {
            _logger.LogWarning("llama-server placed NONE of model {ModelName} role {Role}'s {Total} layers on the GPU; the whole model runs from system RAM, which is substantially slower.",
                key.ModelName, key.Role, total);
            return new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.None, offloaded, total);
        }

        if (offloaded < total)
        {
            _logger.LogWarning("llama-server placed {Offloaded}/{Total} of model {ModelName} role {Role} layers on the GPU; the remainder runs from system RAM, which is substantially slower.",
                offloaded, total, key.ModelName, key.Role);
            return new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.Partial, offloaded, total);
        }

        _logger.LogInformation("llama-server placed all {Total} layers of model {ModelName} role {Role} on the GPU.",
            total, key.ModelName, key.Role);
        return new LlamaServerLaunchPlacement(LlamaServerPlacementOutcome.Full, offloaded, total);
    }

    /// <summary>
    ///     Assembles the benchmark launch receipt. Non-throwing by construction: the only fact that can fail to be read
    ///     is the running image digest, and <see cref="TryComputeRunningImageSha256" /> reports that failure as
    ///     <see langword="null" /> rather than as an exception, so a receipt never costs a run its measurement.
    /// </summary>
    internal static LlamaServerLaunchReceipt BuildBenchmarkLaunchReceipt(GpuVariant variant,
        string? executableVersion,
        string? manifestSha256,
        LlamaServerLaunchProjection launchProjection,
        LlamaServerLaunchAuxAssets auxAssets,
        LlamaServerLaunchPlacement placement,
        int? effectiveContextTokens,
        LlamaServerBenchmarkLaunchPolicy benchmarkLaunchPolicy,
        int processId,
        IReadOnlyList<string>? omittedOptions = null)
    {
        return new LlamaServerLaunchReceipt(LlamaServerLaunchReceipt.CurrentVersion,
            variant,
            DescribeOperatingSystem(),
            executableVersion,
            TryComputeRunningImageSha256(processId),
            manifestSha256,
            launchProjection,
            auxAssets,
            placement,
            effectiveContextTokens,
            benchmarkLaunchPolicy)
        {
            OmittedOptions = omittedOptions ?? []
        };
    }

    /// <summary>
    ///     Hashes the image the LIVE process is running, rather than the executable path the launch resolved — those two
    ///     disagree exactly when it matters, because a runtime can be replaced on disk between launch and readiness.
    ///     Returns <see langword="null" /> whenever the running image cannot be read; an unreadable digest is a fact
    ///     worth recording as absent, never a reason to fail a benchmark.
    /// </summary>
    internal static string? TryComputeRunningImageSha256(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            string? imagePath;
            if (OperatingSystem.IsLinux())
            {
                // /proc/<pid>/exe resolves through the kernel to the mapped image even when the file was replaced or
                // unlinked after launch, which is the whole point of reading it instead of the resolved path.
                imagePath = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/exe";
            }
            else
            {
                using var process = Process.GetProcessById(processId);
                imagePath = process.MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            using var stream = File.OpenRead(imagePath);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The host OS as a stable, bounded token — enough to tell a Metal host from a CUDA one, and nothing more.</summary>
    private static string DescribeOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "macos";
        }

        return OperatingSystem.IsLinux() ? "linux" : "unknown";
    }

    private LlamaServerLoadObservation RecordLoadTelemetry(ProcessKey key,
        GpuVariant variant,
        string runtimeVersion,
        string? runtimeSha256,
        TimeSpan readinessDuration,
        LlamaServerReadinessOutcome outcome,
        LlamaServerPlacementOutcome placement,
        LlamaServerLoadAttemptKind attemptKind,
        SpeculativeDecodingSettings speculative)
    {
        var speculativeModeClass = key.Role == ModelRole.Chat
            ? speculative.ModeClass ?? SpeculativeModeClass.Disabled
            : SpeculativeModeClass.Disabled;
        var observation = new LlamaServerLoadObservation(key.Role,
            variant,
            runtimeVersion,
            runtimeSha256,
            Math.Max(0d, readinessDuration.TotalMilliseconds),
            outcome,
            placement,
            attemptKind,
            speculativeModeClass);
        try
        {
            _loadTelemetry.RecordLoad(observation);
        }
        catch (Exception exception)
        {
            // Telemetry is report-only. A broken exporter must never change launch/fallback/admission behavior.
            _logger.LogDebug(exception, "llama-server load telemetry observer failed.");
        }

        return observation;
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
    public Task<T> RunExclusiveProfilingAsync<T>(string modelName,
        ModelRole role,
        ResolvedLaunchArguments launchArgs,
        bool enableMetrics,
        Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body,
        CancellationToken ct,
        Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>? captureVramBeforeSpawn = null) =>
        RunExclusiveProfilingCoreAsync(modelName,
            role,
            launchArgs,
            enableMetrics,
            body,
            captureVramBeforeSpawn,
            benchmarkPolicy: null,
            ct);

    /// <inheritdoc />
    public Task<T> RunExclusiveBenchmarkAsync<T>(string modelName,
        ModelRole role,
        ResolvedLaunchArguments launchArgs,
        LlamaServerBenchmarkLaunchPolicy launchPolicy,
        Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(launchPolicy);
        if (!launchPolicy.IsSupported)
        {
            throw new ArgumentException("The frozen benchmark launch policy is unsupported.", nameof(launchPolicy));
        }

        return RunExclusiveProfilingCoreAsync(modelName,
            role,
            launchArgs,
            enableMetrics: false,
            body,
            captureVramBeforeSpawn: null,
            launchPolicy,
            ct);
    }

    private async Task<T> RunExclusiveProfilingCoreAsync<T>(string modelName,
        ModelRole role,
        ResolvedLaunchArguments launchArgs,
        bool enableMetrics,
        Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body,
        Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>? captureVramBeforeSpawn,
        LlamaServerBenchmarkLaunchPolicy? benchmarkPolicy,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(launchArgs);
        ArgumentNullException.ThrowIfNull(body);
        _runtimeMutationGate.BeginOperation();
        try
        {
            // EXCLUSIVE: a profiling spawn must be the only model loading on the box for its measurement to mean
            // anything, so it excludes every ensure for its whole eviction + spawn window.
            await _runtimeMutationGate.EnterExclusiveAsync(ct).ConfigureAwait(false);
            var runtimeGateHeld = true;
            try
            {
                var key = new ProcessKey(modelName, role);

                // Take the SAME single-flight gate the normal ensure path uses, so a concurrent user EnsureRunningAsync for this
                // key queues behind the exclusive profiling spawn instead of racing it.
                var gate = _ensureGates.GetOrAdd(key, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // A sibling-role ensure may already have registered a detached spawn before this profiling operation
                    // acquired the runtime-mutation gate. No NEW ensure can register while this gate is held, so snapshot and
                    // await every same-model spawn that is already in flight before evicting. A failed spawn leaves no live
                    // process to evict and must not block profiling; caller cancellation still aborts the profiling request.
                    var siblingSpawns = _inflightSpawns
                                        .Where(pair => string.Equals(pair.Key.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
                                        .Select(static pair => pair.Value.Task)
                                        .ToArray();
                    foreach (var siblingSpawn in siblingSpawns)
                    {
                        try
                        {
                            await siblingSpawn.WaitAsync(ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception)
                        {
                            // The detached spawn faulted or was cancelled independently; its own cleanup removes the entry.
                        }
                    }

                    // Explicitly evict every warm role for this model before capturing ambient VRAM. Admission only
                    // auto-evicts an IDLE LRU victim, so a freshly-used sibling role would otherwise survive, contaminate
                    // the pre-spawn baseline, and make the profiling spawn non-exclusive. The runtime-mutation gate is
                    // still held here, so no new ensure decision can repopulate any role until after this spawn registers.
                    // A role serving in-flight inference refuses the eviction and the whole run is skipped, evicting nothing.
                    if (await TryEvictAllRolesForProfilingAsync(modelName).ConfigureAwait(false) is { } refusal)
                    {
                        throw new LlamaServerProfilingRefusedException(modelName, refusal.Role, refusal.ActiveLeases, refusal.Reason);
                    }

                    var preSpawnVram = captureVramBeforeSpawn is null
                        ? null
                        : await captureVramBeforeSpawn(ct).ConfigureAwait(false);

                    // Thread-safe per-line sink backing the StartupCapture callback (both server pipes Enqueue concurrently).
                    var startupOutput = new ConcurrentQueue<string>();
                    var fitParamsOutput = new ConcurrentQueue<string>();

                    // Replay profiling uses the supplied frozen args verbatim. Explore profiling bypasses only the profile
                    // resolver and applies the same launch policy as normal serving so helper/server placement evidence is
                    // production-equivalent rather than derived from unset llama.cpp defaults.
                    IProcessLaunchTicket? profilingTicket = null;
                    try
                    {
                        if (!_launchAdmissions.TryBeginLaunch(modelName, role, out var profilingAdmission, out profilingTicket)
                            || profilingAdmission is not null)
                        {
                            throw NonRetryable("The profiling launch conflicts with another in-flight admission.");
                        }

                        using var ownedProfilingTicket = profilingTicket;
                        profilingTicket = null;
                        var running = await SpawnCoreAsync(key,
                                (_, _) => Task.FromResult(launchArgs),
                                startupOutput.Enqueue,
                                fitParamsOutput.Enqueue,
                                ensureMetrics: enableMetrics,
                                applyLaunchPolicy: launchArgs.ExploreMode,
                                admission: null,
                                ct,
                                benchmarkPolicy,
                                profilingOwned: true)
                            .ConfigureAwait(false);

                        // The profiling process is registered, so mutation attempts now observe it and return null. Release
                        // the ordering gate while retaining the separate operation barrier through body cleanup.
                        _runtimeMutationGate.ExitExclusive();
                        runtimeGateHeld = false;

                        // Pin against idle eviction for the whole benchmark — the process is never marked-used during the body,
                        // so without the pin the reaper would treat it as idle past the TTL and tear it down mid-measurement.
                        running.Pin();
                        try
                        {
                            var context = new LlamaServerProfilingContext(running.Endpoint,
                                startupOutput.ToArray(),
                                fitParamsOutput.ToArray(),
                                running.Handle.ProcessId)
                            {
                                PreSpawnVram = preSpawnVram,
                                SuccessfulLaunchArguments = running.SuccessfulLaunchArguments,
                                LoadObservation = running.LoadObservation,
                                LaunchReceipt = running.LaunchReceipt
                            };
                            return await body(context, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            // Always unpin + evict the transient profiling process, even on body throw or cancellation.
                            running.Unpin();
                            await _reaper.RemoveProcessAsync(key, running).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        profilingTicket?.Dispose();
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
            finally
            {
                if (runtimeGateHeld)
                {
                    _runtimeMutationGate.ExitExclusive();
                }
            }
        }
        finally
        {
            _runtimeMutationGate.EndOperation();
        }
    }

    /// <summary>Builds a sanitized failure flagged as a deterministic (non-retryable) policy/config outcome.</summary>
    internal static LlamaRuntimeException NonRetryable(string sanitizedMessage)
    {
        var ex = new LlamaRuntimeException(sanitizedMessage);
        ex.Data[NonRetryableMarker] = true;
        return ex;
    }

    private static LlamaRuntimeException CapabilityIncompatible(string sanitizedMessage, bool canTrySafeFallback)
    {
        var exception = NonRetryable(sanitizedMessage);
        exception.Data[CapabilityIncompatibleMarker] = true;
        if (canTrySafeFallback)
        {
            exception.Data[CapabilitySafeFallbackMarker] = true;
        }

        return exception;
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
    internal sealed class RunningProcess(ILlamaServerProcessHandle handle, LlamaServerEndpoint endpoint, int port, DateTimeOffset startedUtc)
    {
        // Process-wide source of eviction claim ids: always positive, negated by the claimant when it is profiling.
        // Only ever compared for equality, so wraparound is not a real concern; 0 means "no teardown owns this".
        private static long s_nextEvictionClaim;

        private long _lastUsedTicks = startedUtc.UtcTicks;

        // Seeded to the spawn time so a freshly-ready process is not re-probed until one full interval has passed.
        private long _lastLivenessProbeTicks = startedUtc.UtcTicks;
        private int _consecutiveLivenessFailures;
        private int _profilingPinned;
        private int _activeLeases;

        // WHICH teardown owns this process, not merely THAT one does: two claimants can hold the mark in sequence, and
        // a rollback that cleared it unconditionally erased the mark the other one was still relying on.
        // The SIGN carries the origin: negative for a profiling pre-spawn eviction, positive for an operator eject or
        // a cap-admission reap. One field, so a reader classifies from a single read and can never see the owner and
        // the origin out of step — which would report a live eject as a transient benchmark spawn.
        private long _evictionOwner;
        private int _ejected;

        public ILlamaServerProcessHandle Handle { get; } = handle;

        public LlamaServerEndpoint Endpoint { get; } = endpoint;

        public int Port { get; } = port;

        /// <summary>
        ///     The effective per-slot context window (<c>/props default_generation_settings.n_ctx</c>) the server
        ///     actually loaded, captured once after readiness. <see langword="null" /> when <c>/props</c> was unavailable.
        /// </summary>
        public int? EffectiveContextTokens { get; init; }

        /// <summary>Immutable snapshot of the exact argv for the candidate that reached readiness.</summary>
        public IReadOnlyList<string> SuccessfulLaunchArguments { get; init; } = [];

        /// <summary>Content-free load/readiness observation for operator profiling correlation.</summary>
        public LlamaServerLoadObservation? LoadObservation { get; init; }

        /// <summary>What this spawn actually launched. Benchmark spawns only; null for every other spawn.</summary>
        public LlamaServerLaunchReceipt? LaunchReceipt { get; init; }

        /// <summary>
        ///     <see langword="true" /> for the transient process an exclusive profiling run spawned for its own
        ///     measurement. Set as part of registration, so it holds from the first instant the process is visible in
        ///     <see cref="LlamaServerProcessSupervisor._processes" /> until profiling's teardown removes it — normal
        ///     inference never reuses it, and is never killed by that teardown. Deliberately independent of
        ///     <see cref="IsProfilingPinned" />, which is only set after registration and cleared before removal.
        ///     A refused chat parks on the per-key single-flight gate while holding only the SHARED runtime-mutation
        ///     gate, which profiling no longer holds by then, so the wait is bounded by the profiling body and stays
        ///     cancellable by the caller's token — it cannot invert against the exclusive gate.
        /// </summary>
        public bool IsProfilingOwned { get; init; }

        public DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

        /// <summary>
        ///     <see langword="true" /> while an operator profiling benchmark owns this process; the idle reaper and the
        ///     cap-admission LRU eviction skip a pinned, non-exited process so it is never torn down mid-measurement.
        /// </summary>
        public bool IsProfilingPinned => Volatile.Read(ref _profilingPinned) != 0;

        /// <summary>Number of in-flight inference requests currently leasing this process (drives graceful-eject drain).</summary>
        public int ActiveLeases => Volatile.Read(ref _activeLeases);

        /// <summary><see langword="true" /> once a teardown has begun for this process — new leases are refused.</summary>
        public bool IsEvicting => EvictionOwner != 0;

        /// <summary>
        ///     The claim currently owning this process's teardown: 0 when none, negative when it was taken by a
        ///     profiling pre-spawn eviction, positive otherwise. One read answers both "is a teardown running" and
        ///     "whose", which is what lets a refusal be classified atomically.
        /// </summary>
        public long EvictionOwner => Volatile.Read(ref _evictionOwner);

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

        /// <summary>
        ///     Marks the process evicting so new leases are refused while an eject drains the in-flight ones, and
        ///     returns the claim that now owns the mark. Unconditional, unlike <see cref="TryBeginEvict(out long)" />:
        ///     an operator eject proceeds even over a claim someone else holds, and taking OWNERSHIP is what stops
        ///     that other claimant's rollback from clearing the mark this eject is draining behind.
        /// </summary>
        public long MarkEvicting()
        {
            var claim = NextEvictionClaim(forProfiling: false);
            Interlocked.Exchange(ref _evictionOwner, claim);
            return claim;
        }

        /// <summary>
        ///     Atomically claims this process as a cap-admission eviction victim: sets the evicting mark (so
        ///     <see cref="LlamaServerProcessSupervisor.TryAcquireInferenceLease" />'s post-acquire re-check refuses any
        ///     racing lease) and then re-checks that no lease slipped in first. Returns <see langword="false" /> —
        ///     releasing the claim — when a lease won the race, so a process is never torn down under in-flight
        ///     inference. <paramref name="claim" /> is the token to pass to <see cref="ReleaseEvictionClaim" />, and is
        ///     0 on any failure.
        /// </summary>
        public bool TryBeginEvict(bool forProfiling, out long claim) => TryBeginEvict(forProfiling, out claim, out _);

        /// <summary>
        ///     As <see cref="TryBeginEvict(bool, out long)" />, additionally reporting WHICH failure occurred:
        ///     <paramref name="alreadyEvicting" /> is <see langword="true" /> when another teardown already owned this
        ///     process (the compare-exchange lost) and <see langword="false" /> when an in-flight lease won the race.
        ///     A caller that reports the refusal needs the distinction — the lost-exchange case has no lease count.
        /// </summary>
        public bool TryBeginEvict(bool forProfiling, out long claim, out bool alreadyEvicting)
        {
            alreadyEvicting = false;
            claim = NextEvictionClaim(forProfiling);
            if (Interlocked.CompareExchange(ref _evictionOwner, claim, comparand: 0) != 0)
            {
                claim = 0;
                alreadyEvicting = true;
                return false; // A teardown already owns this process.
            }

            if (ActiveLeases > 0)
            {
                ReleaseEvictionClaim(claim);
                claim = 0;
                return false;
            }

            return true;
        }

        /// <summary>
        ///     Clears the evicting mark, but ONLY while <paramref name="claim" /> still owns it — a graceful eject that
        ///     timed out, or a profiling pre-spawn eviction rolling its claims back. A claim another teardown has since
        ///     taken over is left alone: clearing it would re-open leasing on a process that eject is between its drain
        ///     check and its teardown of, and the new request would be killed by that teardown.
        /// </summary>
        public void ReleaseEvictionClaim(long claim)
        {
            if (claim != 0)
            {
                _ = Interlocked.CompareExchange(ref _evictionOwner, value: 0, claim);
            }
        }

        /// <summary>A fresh claim, negated when the claimant is a profiling pre-spawn eviction — see <see cref="EvictionOwner" />.</summary>
        private static long NextEvictionClaim(bool forProfiling)
        {
            var claim = Interlocked.Increment(ref s_nextEvictionClaim);
            return forProfiling ? -claim : claim;
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
