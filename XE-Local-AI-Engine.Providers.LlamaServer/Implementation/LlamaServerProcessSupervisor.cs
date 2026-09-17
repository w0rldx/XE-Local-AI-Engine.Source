namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
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
public sealed partial class LlamaServerProcessSupervisor : ILlamaServerProcessSupervisor, IAsyncDisposable
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

    // WHICH logical call flow is the exclusive profiling operation that pinned a process, and which process it pinned.
    // Set only around the body callback, so it identifies that operation's OWN re-entrant calls and nobody else's.
    private readonly AsyncLocal<ExclusiveProfilingScope?> _exclusiveProfiling = new();

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
        TimeProvider timeProvider,
        LlamaServerExternalEndpointOptions? externalEndpoints = null,
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
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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

                // Re-entrancy: an exclusive profiling operation holds this key's single-flight gate across its WHOLE
                // body callback, so the body's own ensure must be answered from the process that operation pinned.
                // Routed into the profiling-owned exclusion below it would park on a semaphore its own frame holds —
                // a self-deadlock, not a wait. Every OTHER caller still falls through and queues behind profiling.
                var ownProfilingProcess = GetOwnExclusiveProfilingProcess(key, out var isOwnProfilingFlow);
                if (isOwnProfilingFlow)
                {
                    // Never fall through from here: the gate is held by this flow's own frame, so a pinned process that
                    // exited during the body fails with the classified non-retryable error rather than waiting forever.
                    if (ownProfilingProcess is null)
                    {
                        throw NonRetryable("The llama-server process this exclusive measurement spawned is no longer running.");
                    }

                    ownProfilingProcess.MarkUsed(_timeProvider.GetUtcNow());
                    return ownProfilingProcess.Endpoint;
                }

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
    ///     The process the CALLER's own exclusive profiling operation pinned for <paramref name="key" />, or
    ///     <see langword="null" /> when this flow pinned none — or no longer owns what it pinned. Matched on the process
    ///     INSTANCE as well as the key, so a marker that outlived the process it named can never hand out a replacement
    ///     this flow never pinned. <paramref name="isOwnFlow" /> reports the KEY match alone: a caller that must not
    ///     touch the per-key gate, because its own frame holds it, has to know it is inside the body even when the
    ///     pinned process is gone.
    /// </summary>
    private RunningProcess? GetOwnExclusiveProfilingProcess(ProcessKey key, out bool isOwnFlow)
    {
        var scope = _exclusiveProfiling.Value;
        if (scope is not { IsActive: true } || !scope.Key.Equals(key))
        {
            isOwnFlow = false;
            return null;
        }

        isOwnFlow = true;
        return _processes.TryGetValue(key, out var running) && ReferenceEquals(running, scope.Process) && !running.Handle.HasExited
            ? running
            : null;
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

    /// <summary>
    ///     Marks the caller's flow as the exclusive profiling operation that pinned <see cref="Process" /> for
    ///     <see cref="Key" />. Carried in an <see cref="AsyncLocal{T}" /> set only around the body callback, so it
    ///     identifies that operation's OWN re-entrant calls and nobody else's. <see cref="IsActive" /> is cleared
    ///     before the body's flow unwinds: work the body DETACHED (fire-and-forget, <c>Task.Run</c>) inherited this
    ///     execution context and would otherwise still resolve as the owning flow after teardown removed the process.
    ///     Cleared, such work queues on the per-key gate and spawns its own process, exactly as any other caller does.
    /// </summary>
    private sealed class ExclusiveProfilingScope
    {
        private int _inactive;

        public ExclusiveProfilingScope(ProcessKey key, RunningProcess process)
        {
            Key = key;
            Process = process;
        }

        public ProcessKey Key { get; }

        public RunningProcess Process { get; }

        public bool IsActive => Volatile.Read(ref _inactive) == 0;

        public void Deactivate()
        {
            Interlocked.Exchange(ref _inactive, value: 1);
        }
    }

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

                            // Mark THIS flow as the owner of the pinned process for the body's duration, so the body's
                            // own re-entrant ensure and runtime-info reads for this key are answered from it instead of
                            // deadlocking on the gate this frame holds. The prior value is RESTORED, not cleared, so a
                            // nested exclusive operation for another key unwinds to its parent's marker.
                            var priorScope = _exclusiveProfiling.Value;
                            var scope = new ExclusiveProfilingScope(key, running);
                            _exclusiveProfiling.Value = scope;
                            try
                            {
                                return await body(context, ct).ConfigureAwait(false);
                            }
                            finally
                            {
                                scope.Deactivate();
                                _exclusiveProfiling.Value = priorScope;
                            }
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
    private sealed class InferenceLease : ILlamaServerInferenceLease
    {
        private readonly RunningProcess _process;
        private int _disposed;

        public InferenceLease(RunningProcess process)
        {
            _process = process;
        }

        public bool WasEjected => _process.WasEjected;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
            {
                _process.ReleaseLease();
            }
        }
    }

    /// <summary>A live, registered process and its last-used timestamp (drives idle-TTL + LRU eviction).</summary>
    internal sealed class RunningProcess
    {
        // Process-wide source of eviction claim ids: always positive, negated by the claimant when it is profiling.
        // Only ever compared for equality, so wraparound is not a real concern; 0 means "no teardown owns this".
        private static long s_nextEvictionClaim;

        private long _lastUsedTicks;

        // Seeded to the spawn time so a freshly-ready process is not re-probed until one full interval has passed.
        private long _lastLivenessProbeTicks;
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

        public RunningProcess(ILlamaServerProcessHandle handle, LlamaServerEndpoint endpoint, int port, DateTimeOffset startedUtc)
        {
            _lastUsedTicks = startedUtc.UtcTicks;
            _lastLivenessProbeTicks = startedUtc.UtcTicks;
            Handle = handle;
            Endpoint = endpoint;
            Port = port;
        }

        public ILlamaServerProcessHandle Handle { get; }

        public LlamaServerEndpoint Endpoint { get; }

        public int Port { get; }

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
        public bool TryBeginEvict(bool forProfiling, out long claim) =>
            TryBeginEvict(forProfiling, out claim, out _);

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
