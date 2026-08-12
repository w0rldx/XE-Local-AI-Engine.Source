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

    // Guards the loaded-cap admission decision + port-set mutation so the cap can never be exceeded by a race.
    private readonly SemaphoreSlim _admissionGate = new(initialCount: 1, maxCount: 1);

    // Orders ordinary ensures against the rare operator runtime MUTATION (runtime install/remove, source build,
    // exclusive profiling). Ensures take it SHARED and proceed concurrently; a mutation takes it EXCLUSIVE. What an
    // exclusive holder relies on is unchanged from the single semaphore this replaces — a mutation waits for every
    // in-flight ensure DECISION, and no new decision starts while it holds the gate — but an ensure no longer
    // head-of-line blocks an unrelated role behind its liveness probe (up to ReuseLivenessProbeTimeout, 2 s).
    // Single-flight per process is NOT this gate's job: _ensureGates already provides it per (model, role).
    private readonly AsyncSharedExclusiveGate _runtimeMutationGate = new();
    private readonly Lock _runtimeOperationSync = new();
    private readonly HashSet<int> _allocatedPorts = [];
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

    // AUD4-06: the process-wide GPU-load admission gate. GPU-backed spawns serialize their spawn-through-readiness window
    // through it (shared with the image supervisor) so two --fit loads never read the same free-VRAM snapshot at once.
    private readonly IGpuModelLoadAdmission _loadAdmission;

    // Node-wide record of measured GPU layer placement. Written here as models load, read by the operator-facing
    // runtime device audit; the composition root injects the singleton both sides share.
    private readonly ILlamaLayerPlacementReport _layerPlacementReport;
    private int _disposed;
    private int _runtimeOperationCount;
    private int _runtimeMutationActivityCount;
    private TaskCompletionSource? _runtimeOperationsDrained;

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
        ILlamaServerExtraLaunchArgumentsResolver? extraArgumentsResolver = null)
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

        // Absent a wired gate (a provider-only host / test), default to the no-op floor so GPU-load serialization is
        // simply off — the composition root injects the real, metric-emitting singleton shared with the image supervisor.
        _loadAdmission = loadAdmission ?? new NoOpGpuModelLoadAdmission();
        _sourceBuildActivity = sourceBuildActivity ?? new LlamaCppSourceBuildActivity();
        _fitParamsRunner = fitParamsRunner ?? new LlamaFitParamsProcessRunner();

        // A private instance keeps a provider-only host (or a test) self-satisfying; the composition root injects the
        // shared singleton so what this supervisor observes is what the runtime audit reports.
        _layerPlacementReport = layerPlacementReport ?? new LlamaLayerPlacementReport();

        _reaperLoop = Task.Run(() => ReapIdleLoopAsync(_shutdownCts.Token));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_runtimeOperationSync)
        {
            if (_disposed != 0)
            {
                return;
            }

            _disposed = 1;
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

        await WaitForRuntimeOperationsDrainedAsync().ConfigureAwait(false);

        // No new operation can enter after _disposed is set, and the separate operation barrier above proves every
        // admitted operation has finished. Own the runtime gate exclusively through teardown and dispose it in-place.
        await _runtimeMutationGate.EnterExclusiveAsync(CancellationToken.None).ConfigureAwait(false);
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
        _runtimeMutationGate.Dispose();
    }

    /// <inheritdoc />
    public async Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        BeginRuntimeOperation();
        try
        {
            EnsureDecision decision;

            // SHARED: this section orders against operator runtime mutations, not against other ensures. Everything it
            // touches is already safe under concurrency — the reuse probe claim is a CAS, the spawn decision runs under
            // the per-key _ensureGates single-flight, and the process/spawn tables are concurrent — so two ensures for
            // different roles run side by side instead of queueing behind each other's liveness probe.
            await EnterSharedRuntimeGateAsync(ct).ConfigureAwait(false);
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
            EndRuntimeOperation();
        }
    }

    /// <inheritdoc />
    public async Task<ILlamaServerRuntimeMutationLease?> TryAcquireRuntimeMutationLeaseAsync(CancellationToken ct)
    {
        BeginRuntimeOperation();
        var operationTransferred = false;
        Interlocked.Increment(ref _runtimeMutationActivityCount);
        try
        {
            // EXCLUSIVE: the mutation about to run replaces the runtime binaries under the supervisor, so it must see a
            // quiet supervisor — every in-flight ensure decision has finished and no new one can start.
            await EnterExclusiveRuntimeGateAsync(ct).ConfigureAwait(false);

            if (_processes.Values.Any(static process => !process.Handle.HasExited) || !_inflightSpawns.IsEmpty)
            {
                _runtimeMutationGate.ExitExclusive();
                return null;
            }

            var lease = new RuntimeMutationLease(_runtimeMutationGate,
                () =>
                {
                    Interlocked.Decrement(ref _runtimeMutationActivityCount);
                    EndRuntimeOperation();
                });
            operationTransferred = true;
            return lease;
        }
        finally
        {
            if (!operationTransferred)
            {
                Interlocked.Decrement(ref _runtimeMutationActivityCount);
                EndRuntimeOperation();
            }
        }
    }

    private void BeginRuntimeOperation()
    {
        lock (_runtimeOperationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_runtimeOperationCount++ == 0)
            {
                _runtimeOperationsDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    private void EndRuntimeOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_runtimeOperationSync)
        {
            _runtimeOperationCount--;
            if (_runtimeOperationCount == 0)
            {
                drained = _runtimeOperationsDrained;
                _runtimeOperationsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private Task WaitForRuntimeOperationsDrainedAsync()
    {
        lock (_runtimeOperationSync)
        {
            return _runtimeOperationCount == 0 ? Task.CompletedTask : _runtimeOperationsDrained!.Task;
        }
    }

    /// <summary>
    ///     Enters the runtime gate SHARED for an ordinary ensure: concurrent with other ensures, excluded by (and
    ///     excluding) an operator runtime mutation. Pairs with <see cref="AsyncSharedExclusiveGate.ExitShared" />.
    /// </summary>
    private Task EnterSharedRuntimeGateAsync(CancellationToken ct)
    {
        return EnterRuntimeGateAsync(shared: true, ct);
    }

    /// <summary>
    ///     Enters the runtime gate EXCLUSIVE for an operator runtime mutation or an exclusive profiling spawn: waits
    ///     for every in-flight ensure decision to finish and holds off every new one. Pairs with
    ///     <see cref="AsyncSharedExclusiveGate.ExitExclusive" />.
    /// </summary>
    private Task EnterExclusiveRuntimeGateAsync(CancellationToken ct)
    {
        return EnterRuntimeGateAsync(shared: false, ct);
    }

    private async Task EnterRuntimeGateAsync(bool shared, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        try
        {
            var entering = shared
                ? _runtimeMutationGate.EnterSharedAsync(linkedCancellation.Token)
                : _runtimeMutationGate.EnterExclusiveAsync(linkedCancellation.Token);
            await entering.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }

        if (Volatile.Read(ref _disposed) == 0)
        {
            return;
        }

        if (shared)
        {
            _runtimeMutationGate.ExitShared();
        }
        else
        {
            _runtimeMutationGate.ExitExclusive();
        }

        throw new ObjectDisposedException(GetType().FullName);
    }

    /// <inheritdoc />
    public bool IsKeepWarmSuppressed()
    {
        return Volatile.Read(ref _runtimeMutationActivityCount) > 0;
    }

    internal int CountInflightSpawns() =>
        _inflightSpawns.Count;

    private sealed class RuntimeMutationLease(AsyncSharedExclusiveGate gate, Action onDisposed) : ILlamaServerRuntimeMutationLease
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                gate.ExitExclusive();
                onDisposed();
            }

            return ValueTask.CompletedTask;
        }
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
        _ = Task.Run(async () =>
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
        });
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
        BeginRuntimeOperation();
        try
        {
            await EvictCoreAsync(modelName, role).ConfigureAwait(false);
        }
        finally
        {
            EndRuntimeOperation();
        }
    }

    private async Task EvictCoreAsync(string modelName, ModelRole role)
    {
        var key = new ProcessKey(modelName, role);
        if (_processes.TryGetValue(key, out var running))
        {
            await RemoveProcessAsync(key, running).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task EvictAllRolesAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        BeginRuntimeOperation();
        try
        {
            await EvictAllRolesCoreAsync(modelName).ConfigureAwait(false);
        }
        finally
        {
            EndRuntimeOperation();
        }
    }

    private async Task EvictAllRolesCoreAsync(string modelName)
    {
        foreach (var role in Enum.GetValues<ModelRole>())
        {
            await EvictCoreAsync(modelName, role).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        BeginRuntimeOperation();
        try
        {
            return await EjectCoreAsync(modelName, role, force, ct).ConfigureAwait(false);
        }
        finally
        {
            EndRuntimeOperation();
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
                await RemoveProcessAsync(key, target).ConfigureAwait(false);
            }

            return LlamaServerEjectOutcome.NotRunning;
        }

        // Mark evicting: new inference leases are refused (TryAcquireInferenceLease returns null) so the active-lease
        // count can only fall while we drain. The process stays registered and reusable until we tear it down or give up.
        target.MarkEvicting();
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
            target.ClearEvicting();
            throw;
        }

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
    public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        var key = new ProcessKey(modelName, role);
        if (!_processes.TryGetValue(key, out var running) || running.Handle.HasExited)
        {
            return LlamaServerLeaseAcquisition.NotRunning;
        }

        // A draining eject refuses new leases — and the refusal REASON is surfaced so the caller fails the request as
        // operator-ejected instead of running it leaseless under the drain (untracked by the drain, killed mid-flight
        // by the teardown, and then self-heal-respawning the just-ejected model).
        if (running.IsEvicting)
        {
            return LlamaServerLeaseAcquisition.Evicting;
        }

        // Acquire, then RE-CHECK evicting/exited: an eject that flipped the flag between the guard above and here must
        // not gain a lease that would extend its drain — release and refuse, classifying the refusal at this instant.
        running.AcquireLease();
        if (running.IsEvicting || running.Handle.HasExited)
        {
            running.ReleaseLease();
            return running.IsEvicting ? LlamaServerLeaseAcquisition.Evicting : LlamaServerLeaseAcquisition.NotRunning;
        }

#pragma warning disable CA2000 // Ownership of the lease transfers to the caller inside the returned acquisition; the interface contract obliges the caller to dispose it.
        return LlamaServerLeaseAcquisition.Granted(new InferenceLease(running));
#pragma warning restore CA2000
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
        CancellationToken ct)
    {
        var modelFilePath = await _modelStore.ResolveModelFilePathAsync(key.ModelName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modelFilePath))
        {
            throw NonRetryable("The requested model is not installed.");
        }

        // A vision model's mmproj projector companion — passed to llama-server as --mmproj so it accepts image input.
        // Chat role only (embedding/reranker never take images); null for a text-only model, which gets no --mmproj.
        var projectorFilePath = key.Role == ModelRole.Chat
            ? await _modelStore.ResolveProjectorFilePathAsync(key.ModelName, ct).ConfigureAwait(false)
            : null;

        // AUD4-09: the cold-start readiness deadline scales with the on-disk model size — a large model loads
        // proportionally slower, so a fixed constant would kill and retry it before it can finish (the audited hang). A
        // missing/unreadable size (0) falls back to the base timeout.
        var readinessTimeout = _options.ResolveReadinessTimeout(TryGetFileSizeBytes(modelFilePath));

        // A chat-role EXTERNAL-DRAFT speculative mode needs its draft GGUF present before launch — a missing file would
        // otherwise start a server that dies cryptically. Deterministic misconfiguration → non-retryable (mirrors the
        // model-not-installed guard above). draft-mtp, ngram-*, and disabled modes never reach this: MTP drafts from
        // heads inside the main model, so there is no second GGUF to find (RequiresExternalDraftModel is false).
        // The operator selects a draft model by NAME (installed chat model); resolve it to its on-disk GGUF the same way
        // the target model is resolved above so the effective launch args carry a real path. An explicit path override
        // (SpeculativeDraftModelPath), when set, wins and skips resolution.
        var speculative = _options.Speculative;
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
        var capabilityManifest = await _capabilityManifestProbe.GetManifestAsync(binary, ct).ConfigureAwait(false);
        if (!capabilityManifest.ProbeSucceeded)
        {
            throw NonRetryable("The selected llama.cpp runtime could not report its supported server options. Reinstall or rebuild the runtime and try again.");
        }

        // Resolve the launch args (frozen-profile replay or explore-mode auto-fit, or operator-supplied profiling args)
        // for this (model, role, backend) BEFORE taking the admission gate, so a slow profile read never stalls
        // admission for other keys.
        var resolved = admission?.ResolvedArguments ?? await resolveArgs(variant, ct).ConfigureAwait(false);

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
        // Replay profiling bypasses it so the supplied frozen args ARE the experiment. Explore profiling applies the
        // production policy because the helper and server must observe the same concrete context/KV/FA vector as normal
        // serving; otherwise the fit helper (behavior observed on b9692) can report unchanged `-c 0 -ngl -1` defaults
        // that are not replayable placement.
        var planSet = await BuildLaunchPlanCandidatesAsync(key,
                variant,
                resolved,
                applyLaunchPolicy,
                admission?.Allocation,
                ct)
            .ConfigureAwait(false);
        var planCandidates = planSet.Candidates;

        Exception? optimizedFailure = null;
        for (var attempt = 0; attempt < planCandidates.Count; attempt++)
        {
            var candidate = planCandidates[attempt];
            var isSafeRetry = attempt > 0;
            var port = await AdmitAndAllocatePortAsync(ct).ConfigureAwait(false);

            ILlamaServerProcessHandle? handle = null;
            var automaticCapture = applyLaunchPolicy ? new BoundedStartupCapture() : null;

            // Latches llama.cpp's layer-placement banner out of the streamed startup output. It is deliberately NOT
            // read off automaticCapture: that buffer is bounded, and at the verbosity the banner requires it is
            // printed around line 155 — outside any small window.
            var placementSniffer = applyLaunchPolicy ? new LayerPlacementSniffer() : null;

            // Flipped once this child is serving, to demote its (raised-verbosity) request chatter to Debug. It stays
            // false for the whole load, and forever on a spawn that never reaches readiness, so the placement banner
            // and every failure message are still logged at Information.
            var servingWindow = new DiagnosticVerbosityWindow();
            try
            {
                var spec = BuildLaunchSpec(key, binary.ServerExecutablePath, modelFilePath, port, variant, candidate.Resolved,
                    _options.ChatCacheReuse, speculative, candidate.Plan, _options.ChatCacheRamMiB, projectorFilePath);

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
                // The operator-profiling path is skipped because it already raises verbosity to maximum below.
                if (fitParamsCapture is null
                    && placementSniffer is not null
                    && variant != GpuVariant.Cpu
                    && !HasVerbosityArgument(spec.Arguments))
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

                handle = _launcher.Launch(spec);
                _logger.LogInformation("llama-server spawned for model {ModelName} role {Role} (pid {ProcessId}, port {Port}){LaunchPlan}.",
                    key.ModelName, key.Role, handle.ProcessId, port, DescribeLaunchPlan(candidate.Plan));

                var readyStartedUtc = _timeProvider.GetUtcNow();
                await WaitForReadyOrExitAsync(handle, spec.BaseAddress, readinessTimeout, ct).ConfigureAwait(false);
                _logger.LogInformation("llama-server ready for model {ModelName} role {Role} (pid {ProcessId}) after {ElapsedMs:F0} ms (readiness budget {BudgetSeconds:F0}s).",
                    key.ModelName, key.Role, handle.ProcessId, (_timeProvider.GetUtcNow() - readyStartedUtc).TotalMilliseconds, readinessTimeout.TotalSeconds);

                RecordObservedLayerPlacement(key, variant, placementSniffer);

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

                // AUD4-02: read the effective per-slot context the server actually loaded (best-effort) so both app-side
                // budgeters and the UI meter size against the REAL window rather than the requested/advertised one.
                var effectiveContext = await TryReadEffectiveContextAsync(spec.BaseAddress, ct).ConfigureAwait(false);

                var endpoint = new LlamaServerEndpoint(key.ModelName, key.Role, spec.BaseAddress);
                var running = new RunningProcess(handle, endpoint, port, _timeProvider.GetUtcNow())
                {
                    EffectiveContextTokens = effectiveContext,
                    SuccessfulLaunchArguments = fitParamsCapture is null ? [] : [.. spec.Arguments]
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
        }

        // Unreachable: the loop returns on success or throws on the final candidate; the fallback keeps the analyzer happy.
        throw optimizedFailure ?? new InvalidOperationException("llama-server spawn produced no launch attempt.");
    }

    /// <summary>
    ///     Builds the ordered launch-plan candidates for a spawn. The normal path (<paramref name="applyLaunchPolicy" />)
    ///     resolves the policy plan and, when it enables the GPU KV-quant + flash-attention optimization, appends a safe
    ///     (KV/FA off) fallback candidate to try once if the optimized one cannot reach readiness. Replay profiling and
    ///     any other caller that disables policy application get a single <see langword="null" /> plan.
    /// </summary>
    private async Task<LaunchPlanSet> BuildLaunchPlanCandidatesAsync(ProcessKey key,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        bool applyLaunchPolicy,
        ProcessContextAllocation? admittedAllocation,
        CancellationToken ct)
    {
        if (!applyLaunchPolicy)
        {
            return new LaunchPlanSet(null, [new(resolved, null)]);
        }

        ProcessContextAllocation allocation;
        if (admittedAllocation is null)
        {
            allocation = await _allocationResolver.ResolveAsync(key.ModelName, key.Role, variant, resolved, ct).ConfigureAwait(false)
                         ?? throw NonRetryable("The requested model's process context could not be allocated.");
        }
        else if (admittedAllocation.Source == ProcessContextAllocationSource.HardwareTier)
        {
            if (!_allocationResolver.TryGetEffectiveCommittedAllocation(admittedAllocation, out allocation)
                || !string.Equals(allocation.CacheKey, admittedAllocation.CacheKey, StringComparison.Ordinal)
                || !string.Equals(allocation.ContentIdentity, admittedAllocation.ContentIdentity, StringComparison.Ordinal)
                || allocation.ProcessContextTokens > admittedAllocation.ProcessContextTokens)
            {
                throw NonRetryable("The admitted local model context allocation is no longer valid.");
            }
        }
        else
        {
            allocation = admittedAllocation;
        }

        var plan = await _launchPolicy.ResolveAsync(key.Role, variant, resolved, allocation, ct).ConfigureAwait(false);

        // The optimized (KV-quant + fused flash-attention) config reaches the launch line from two independent sources,
        // and each gets the same one-shot safe retry: the policy plan on an explore spawn, and the frozen profile's own
        // -ctk/-ctv on a GPU replay. A CPU spawn emits no replay KV args at all, so its "safe" variant would be a
        // byte-identical second launch — no candidate there.
        if (plan.UseKvCacheQuantization)
        {
            return new LaunchPlanSet(allocation, [new(resolved, plan), new(resolved, plan.WithoutKvCacheQuantization())]);
        }

        if (variant != GpuVariant.Cpu && !resolved.ExploreMode && !string.IsNullOrWhiteSpace(resolved.KvTypeK))
        {
            return new LaunchPlanSet(allocation, [new(resolved, plan), new(resolved.WithoutKvCacheQuantization(), plan)]);
        }

        return new LaunchPlanSet(allocation, [new(resolved, plan)]);
    }

    /// <summary>One ordered launch attempt: the explore/replay args to emit and the policy plan to emit them under.</summary>
    private sealed record LaunchCandidate(ResolvedLaunchArguments Resolved, LlamaServerLaunchPlan? Plan);

    private sealed record LaunchPlanSet(ProcessContextAllocation? Allocation, List<LaunchCandidate> Candidates);

    /// <summary>
    ///     Publishes a sniffed layer-placement observation once the process is genuinely serving. Recording only after
    ///     readiness keeps a candidate that printed a banner and then failed to start out of the operator-facing report.
    ///     A partial offload is logged as a warning: the model serves, but a share of its layers run from system RAM.
    /// </summary>
    private void RecordObservedLayerPlacement(ProcessKey key, GpuVariant variant, LayerPlacementSniffer? sniffer)
    {
        if (sniffer is null || !sniffer.TryGetObservation(out var offloaded, out var total))
        {
            return;
        }

        _layerPlacementReport.Record(key.Role, variant, key.ModelName, offloaded, total);

        if (offloaded < total)
        {
            _logger.LogWarning("llama-server placed {Offloaded}/{Total} of model {ModelName} role {Role} layers on the GPU; the remainder runs from system RAM, which is substantially slower.",
                offloaded, total, key.ModelName, key.Role);
            return;
        }

        _logger.LogInformation("llama-server placed all {Total} layers of model {ModelName} role {Role} on the GPU.",
            total, key.ModelName, key.Role);
    }

    /// <summary>Whether the argument vector already sets a log verbosity, in which case the caller must not add one.</summary>
    private static bool HasVerbosityArgument(IReadOnlyList<string> arguments)
    {
        return arguments.Any(static argument =>
            argument is "-v" or "--verbose" or "--log-verbose" or "-lv" or "--verbosity" or "--log-verbosity");
    }

    /// <summary>
    ///     One spawn's "is this child serving yet" latch. The launcher reads it per forwarded line via
    ///     <see cref="IsServing" /> to decide Information vs Debug; the supervisor flips it exactly once, after
    ///     readiness. A spawn that never becomes ready never flips it, so a failed load's diagnostics stay at
    ///     Information where an operator will actually see them.
    /// </summary>
    private sealed class DiagnosticVerbosityWindow
    {
        private volatile bool _serving;

        public Func<bool> IsServing => () => _serving;

        public void MarkServing()
        {
            _serving = true;
        }
    }

    /// <summary>
    ///     Scans streamed startup output for llama.cpp's layer-placement banner and latches the first match. Both server
    ///     pipes invoke <see cref="Add" /> concurrently; once a value is latched the hot path is a single volatile read,
    ///     so the remaining (verbose) lines cost no regex.
    /// </summary>
    private sealed class LayerPlacementSniffer
    {
        private readonly Lock _gate = new();
        private int _offloaded;
        private volatile int _total;

        public void Add(string line)
        {
            if (_total > 0)
            {
                return;
            }

            if (!LlamaLayerOffloadBanner.TryParse(line, out var offloaded, out var total))
            {
                return;
            }

            lock (_gate)
            {
                if (_total > 0)
                {
                    return;
                }

                _offloaded = offloaded;
                _total = total;
            }
        }

        public bool TryGetObservation(out int offloaded, out int total)
        {
            lock (_gate)
            {
                offloaded = _offloaded;
                total = _total;
                return total > 0;
            }
        }
    }

    /// <summary>
    ///     Bounded startup diagnostics for <see cref="LlamaStartupFailureClassifier" />, retaining the MOST RECENT
    ///     lines rather than the first ones.
    /// </summary>
    /// <remarks>
    ///     Keeping the first N was safe only while the child ran at its default verbosity, where the whole startup is
    ///     about 11 lines and everything fits. It is not safe now that GPU spawns raise verbosity to read the placement
    ///     banner: measured against a real allocation failure, a default-verbosity startup put the "out of memory" text
    ///     at line 11 of 18, and the same failure at the raised verbosity put it at line 179 of 186 — behind roughly
    ///     170 lines of model-loader metadata. A first-N window would have captured only that metadata and classified
    ///     the failure as Other, silently disabling the context down-tier retry. Failure output is always at the END of
    ///     a failed startup at either verbosity, so a last-N window classifies both identically.
    /// </remarks>
    private sealed class BoundedStartupCapture
    {
        private const int MaximumCharacters = 16 * 1024;
        private const int MaximumLines = 64;
        private readonly Lock _gate = new();
        private readonly Queue<string> _lines = new();
        private int _characters;

        public void Add(string line)
        {
            // A single pathological line cannot be allowed to evict the whole window, so cap the line itself first.
            var captured = line.Length <= MaximumCharacters ? line : line[..MaximumCharacters];

            lock (_gate)
            {
                _lines.Enqueue(captured);
                _characters += captured.Length;

                while (_lines.Count > MaximumLines || (_characters > MaximumCharacters && _lines.Count > 1))
                {
                    _characters -= _lines.Dequeue().Length;
                }
            }
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate)
            {
                return [.. _lines];
            }
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
        CancellationToken ct,
        Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>? captureVramBeforeSpawn = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(launchArgs);
        ArgumentNullException.ThrowIfNull(body);
        BeginRuntimeOperation();
        try
        {
            // EXCLUSIVE: a profiling spawn must be the only model loading on the box for its measurement to mean
            // anything, so it excludes every ensure for its whole eviction + spawn window.
            await EnterExclusiveRuntimeGateAsync(ct).ConfigureAwait(false);
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
                                        .Where(pair => string.Equals(pair.Key.ModelName, modelName, StringComparison.Ordinal))
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
                    await EvictAllRolesCoreAsync(modelName).ConfigureAwait(false);

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
                                ct)
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
                                SuccessfulLaunchArguments = running.SuccessfulLaunchArguments
                            };
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
            EndRuntimeOperation();
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
        // Processes detached from the table under the gate, tree-killed after it is released (see KillDetachedProcesses).
        var detached = new List<RunningProcess>();
        await _admissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Drop any process that has already exited so its slot/port is reclaimed before the cap check.
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

            // The gate is free BEFORE any child is killed: tree-killing a multi-GB model is slow, and under the gate it
            // serialized every unrelated model's port allocation and release behind it. This spawn still waits for its
            // own victim to die before it proceeds to launch, so the VRAM the victim held is genuinely released first.
            KillDetachedProcesses(detached);
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
        LlamaServerLaunchPlan? plan = null,
        int chatCacheRamMiB = 0,
        string? projectorFilePath = null)
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
        //  - GPU explore: --fit on (auto-fit places layers/experts around the explicit -c), plus the policy -c and the
        //    KV-quant + flash-attention optimization; GPU replay: the frozen profile args verbatim. Both carry --metrics.
        //  - CPU: the policy -c (explore) or the frozen -c (replay), plus the CPU thread policy; NO --fit/--metrics/-ngl,
        //    KV stays f16 and flash-attention stays auto.
        // A null plan (replay profiling) reproduces the supplied replay vector byte-for-byte.
        AppendContextPlacementAndThreadArgs(args, variant, resolved, plan);

        if (key.Role == ModelRole.Chat)
        {
            // Mandatory for tool/function calling — without it llama-server ignores the GGUF tool grammar.
            args.Add("--jinja");

            // Vision model: the mmproj projector is what makes llama-server accept image input (OpenAI image_url content
            // parts) — without it an image in the request body is rejected. Present only for a model whose projector
            // companion was resolved locally (see IGgufModelStore.ResolveProjectorFilePathAsync); a text-only model
            // passes null and gets no flag. Chat role only — embedding/reranker servers never take images.
            if (!string.IsNullOrWhiteSpace(projectorFilePath))
            {
                args.Add("--mmproj");
                args.Add(projectorFilePath);
            }

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

            // Host-RAM prompt-cache budget. Emitted EXPLICITLY on every chat spawn because the pinned build's
            // implicit default is 8192 MiB — half the RAM of a 16 GB machine — and its limit enforcement is
            // known-ineffective on Linux under default overcommit (upstream #22629: the OOM killer fires before
            // std::bad_alloc, SIGKILLing the server past its own eviction). 0 disables the cache.
            args.Add("--cache-ram");
            args.Add(chatCacheRamMiB.ToString(CultureInfo.InvariantCulture));

            AppendSpeculativeArgs(args, speculative);
        }
        else if (key.Role == ModelRole.Embedding)
        {
            // /v1/embeddings is exposed only with --embeddings + a non-`none` pooling type.
            args.Add("--embeddings");
            args.Add("--pooling");
            args.Add("mean");
            AppendPooledForwardPassBatchArgs(args, resolved, plan);

            // One-shot forward passes have no prompt state worth caching — disable the host prompt cache instead of
            // inheriting the upstream 8192 MiB default (see the chat branch).
            args.Add("--cache-ram");
            args.Add("0");
        }
        else if (key.Role == ModelRole.Reranker)
        {
            // Reranker role. POST /v1/rerank is exposed only with --rerank (alias --reranking) + `--pooling rank`
            // (verified against b9692, re-confirmed against the pinned b10201 --help). This is MUTUALLY EXCLUSIVE with
            // the embedding branch above —
            // a rerank server scores (query, document) pairs and never gets --embeddings — and carries none of the
            // chat-only flags (--jinja, --cache-reuse, speculative). Because each role is its own branch, a single
            // process can only ever receive one role's flags, so --embeddings and --rerank never coexist.
            args.Add("--rerank");
            args.Add("--pooling");
            args.Add("rank");
            AppendPooledForwardPassBatchArgs(args, resolved, plan);

            // One-shot scoring passes have no prompt state worth caching — disable the host prompt cache instead of
            // inheriting the upstream 8192 MiB default (see the chat branch).
            args.Add("--cache-ram");
            args.Add("0");
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
            // --metrics on BOTH GPU modes. The /metrics gauges (KV bytes, slot state, cache-reused tokens) are the only
            // in-app view of what a spawn actually did, and a frozen-profile replay — the steady state on a machine
            // that has been tuned once — was previously the one GPU path that exposed none of them.
            args.Add("--metrics");

            if (resolved.ExploreMode)
            {
                // Let llama.cpp auto-fit choose + print placement. The explicit -c is RESPECTED by --fit (it fits
                // ngl/batch around it) and the KV/FA flags are not placement flags, so auto-fit stays active (verified
                // against b9692; b10201 --help confirms --fit adjusts only UNSET arguments, so the explicit -c is
                // respected).
                args.Add("--fit");
                args.Add("on");
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

    /// <summary>
    ///     Appends the physical/logical batch sizes (<c>-b</c>/<c>-ub</c>) for the POOLED roles (Embedding, Reranker),
    ///     raising them from llama.cpp's 512-token default to this spawn's context size.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>This is a correctness flag, not a tuning flag.</strong> A pooled embedding/rerank forward pass is
    ///         non-causal: the whole input must sit inside ONE physical micro-batch, because pooling has no way to carry
    ///         attention state across <c>n_ubatch</c> boundaries. llama-server therefore rejects — it does not split —
    ///         any single input longer than <c>n_ubatch</c>, with
    ///         <c>500 {"error":{"code":500,"message":"input (N tokens) is too large to process. increase the physical
    ///         batch size (current batch size: 512)"}}</c>.
    ///     </para>
    ///     <para>
    ///         Without this, the usable embedding input was llama.cpp's DEFAULT <c>n_ubatch</c> of <strong>512</strong>
    ///         tokens — NOT the <c>-c</c> we ask for (2048 by default, see
    ///         <c>LlamaServerLaunchPolicyOptions.EmbeddingContextTokens</c>) and NOT the window the model advertises.
    ///         Nothing upstream knew that: the knowledge-base chunker sizes chunks against the model's CONTEXT window,
    ///         so ordinary 2000-character markdown chunks (~520-680 real tokens) exceeded the silent 512 ceiling and
    ///         every knowledge-base document failed to index on a default node. Measured against
    ///         <c>nomic-embed-text-v1.5.Q4_K_M</c>: 11 of 12 consecutive real markdown chunks were rejected at the
    ///         default, 0 of 12 with these flags.
    ///     </para>
    ///     <para>
    ///         Safe by construction: llama.cpp CLAMPS both values down to the effective context, so requesting more than
    ///         the model supports is a no-op rather than an error (verified: <c>-ub 8192</c> against a 2048-window model
    ///         starts and reports <c>n_ctx_slot = 2048</c>). The flags also compose with <c>--fit on</c> — auto-fit sizes
    ///         placement around them rather than overriding them (verified against the in-app source build, pin b10201).
    ///         Chat is deliberately excluded: a causal decode splits across micro-batches correctly, so raising its batch
    ///         is a memory/throughput trade-off rather than a correctness fix, and <c>--fit</c> owns that decision.
    ///     </para>
    /// </remarks>
    private static void AppendPooledForwardPassBatchArgs(List<string> args, ResolvedLaunchArguments resolved, LlamaServerLaunchPlan? plan)
    {
        // Mirror whichever -c this spawn actually emitted: the policy context (explore/CPU) or the frozen replay's own
        // context. A pooled role must be able to embed anything that fits the context it advertises.
        var contextTokens = plan?.RequestedContextTokens ?? resolved.CtxSize;
        if (contextTokens <= 0)
        {
            return;
        }

        var value = contextTokens.ToString(CultureInfo.InvariantCulture);

        // -b (logical) must be >= -ub (physical); pinning both to the context satisfies that for any context size.
        args.Add("-b");
        args.Add(value);
        args.Add("-ub");
        args.Add(value);
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
            // Quantized/explicit KV requires the fused flash-attention path with matching K/V types (b9692; unchanged
            // in the pinned b10201).
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
    ///     Appends the chat-role speculative-decoding flags, one branch per <see cref="SpeculativeModeClass" />.
    ///     Disabled/default (<c>none</c>) emits nothing. A configured mode is validated first (unknown mode, or an
    ///     external-draft mode with no draft path, is a deterministic misconfiguration surfaced as a NON-RETRYABLE error
    ///     rather than a server that dies cryptically on launch). Then:
    ///     <list type="bullet">
    ///         <item>
    ///             <see cref="SpeculativeModeClass.Draftless" /> (<c>ngram-*</c>) self-speculates from context: only
    ///             <c>--spec-type</c>.
    ///         </item>
    ///         <item>
    ///             <see cref="SpeculativeModeClass.MainModelHeads" /> (<c>draft-mtp</c>) drafts from MTP heads in the main
    ///             GGUF, so NO <c>--spec-draft-model</c> and no <c>--spec-draft-ngl</c> (that knob sizes a draft-model load
    ///             that never happens). <c>--spec-draft-n-max</c> IS honoured — b10201's <c>common_speculative_n_max</c>
    ///             reads <c>draft.n_max</c> for <c>DRAFT_MTP</c> — so it is still emitted.
    ///         </item>
    ///         <item>
    ///             <see cref="SpeculativeModeClass.ExternalDraft" /> additionally emits <c>--spec-draft-model</c> and
    ///             <c>--spec-draft-ngl</c>. That drafter loads inside the chat process and is never separately ledgered or
    ///             footprint-estimated; on the primary NVIDIA path its resident VRAM is still reflected in
    ///             <c>CapacityService</c>'s free-VRAM baseline (<c>nvidia-smi memory.free</c>) so a later sub-agent
    ///             admission accounts for it, but on the non-NVIDIA total-minus-ledger fallback it stays invisible.
    ///         </item>
    ///     </list>
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

        if (speculative.ModeClass is SpeculativeModeClass.Draftless)
        {
            return;
        }

        if (speculative.RequiresExternalDraftModel)
        {
            // Validated non-empty above; the file's existence on disk is enforced on the spawn path before launch.
            args.Add("--spec-draft-model");
            args.Add(speculative.DraftModelPath!);
        }

        if (speculative.DraftMaxTokens > 0)
        {
            args.Add("--spec-draft-n-max");
            args.Add(speculative.DraftMaxTokens.ToString(CultureInfo.InvariantCulture));
        }

        if (speculative.RequiresExternalDraftModel && speculative.DraftGpuLayers is { } draftGpuLayers)
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

            // A live process with in-flight inference (an active lease) is never reaped, even past the TTL:
            // LastUsedUtc is stamped per ensure/reuse, not per token, so a single generation that legitimately outruns
            // the idle window (a raised invocation timeout on a slow CPU box) looks idle here while a request is
            // mid-flight — tree-killing it would cut a running turn off.
            if (running.ActiveLeases > 0 && !running.Handle.HasExited)
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

    /// <summary>
    ///     Evicts the least-recently-used process that is currently idle (caller holds the admission gate). The victim
    ///     is detached here — its slot and port are free the moment this returns <see langword="true" /> — and appended
    ///     to <paramref name="detached" /> for the caller to tree-kill once the gate is released.
    /// </summary>
    private bool TryEvictIdleLeastRecentlyUsed(List<RunningProcess> detached)
    {
        var now = _timeProvider.GetUtcNow();
        ProcessKey? victimKey = null;
        RunningProcess? victim = null;
        var victimRank = int.MaxValue;
        foreach (var (key, running) in _processes)
        {
            // A live profiling-pinned process is reserved for its benchmark — never select it as a cap-admission victim
            // (an EXITED pinned process is a dead handle and stays eligible so its slot/port is reclaimed).
            if (running.IsProfilingPinned && !running.Handle.HasExited)
            {
                continue;
            }

            // In-flight inference disqualifies a live process as a capacity-eviction victim for the same reason the
            // idle reaper skips it: past-TTL only means "no new request started", not "not mid-generation". This is a
            // best-effort heuristic read; the atomic claim is TryBeginEvict on the chosen victim below.
            if (running.ActiveLeases > 0 && !running.Handle.HasExited)
            {
                continue;
            }

            // Victim preference, best first:
            //   0 — exited or idle past the TTL (any role): the reaper would take it anyway.
            //   1 — in-window but unleased POOLED role (embedding/reranker). Background indexing/search touches these
            //       continuously, so on the default cap (3 = the number of roles) they otherwise pin all slots and a
            //       foreground chat model switch hard-fails for up to a full TTL window ("maximum number of local
            //       models are already loaded") — the most likely user-visible runtime failure on consumer hardware.
            //       A pooled reload costs ~1s against a chat reload's tens of seconds, so the pooled process yields.
            //   An in-window CHAT process is never a victim: keep-warm recency protection is deliberate there (the
            //   cheap error beats silently evicting a multi-GB model the user is about to reuse), and admission
            //   rejects as before when no rank qualifies.
            var isIdlePastTtl = running.Handle.HasExited || now - running.LastUsedUtc >= _options.IdleTimeToLive;
            var isPooledRole = key.Role is ModelRole.Embedding or ModelRole.Reranker;
            if (!isIdlePastTtl && !isPooledRole)
            {
                continue; // An in-window chat process is never a victim.
            }

            var rank = isIdlePastTtl ? 0 : 1;

            // A better rank always wins; within the same rank, least-recently-used wins.
            if (victim is not null && (rank != victimRank ? rank > victimRank : running.LastUsedUtc >= victim.LastUsedUtc))
            {
                continue;
            }

            victimKey = key;
            victim = running;
            victimRank = rank;
        }

        if (victimKey is null || victim is null)
        {
            return false;
        }

        // Atomically latch the chosen victim before tearing it down. If a request acquired a lease on it between the
        // heuristic scan and here, TryBeginEvict fails and no victim is admitted this round — the caller surfaces the
        // cap error rather than tree-killing a process under an active lease. An EXITED victim holds no real lease,
        // so it is torn down regardless.
        if (!victim.Handle.HasExited && !victim.TryBeginEvict())
        {
            return false;
        }

        // Free the slot/port under the gate so the new admission proceeds immediately; the kill follows outside it.
        // A lost removal race (a concurrent eject/reap already detached this victim) frees no slot of OUR doing, so
        // report no admission rather than letting the cap be overrun on someone else's teardown.
        if (DetachProcess(victimKey.Value, victim) is not { } evicted)
        {
            return false;
        }

        _logger.LogWarning("Loaded-model cap ({Cap}) reached; evicting {Idleness} llama-server for model {ModelName} role {Role} to admit a new one.",
            _options.MaxLoadedProcesses, victimRank == 0 ? "idle" : "in-window pooled", victimKey.Value.ModelName, victimKey.Value.Role);

        detached.Add(evicted);
        return true;
    }

    private void PruneExitedProcesses(List<RunningProcess> detached)
    {
        foreach (var (key, running) in _processes)
        {
            if (running.Handle.HasExited && DetachProcess(key, running) is { } exited)
            {
                detached.Add(exited);
            }
        }
    }

    private async Task RemoveProcessAsync(ProcessKey key, RunningProcess running)
    {
        // Teardown must complete even during shutdown, so it is not bound to a caller cancellation token.
        RunningProcess? detached;
        await _admissionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            detached = DetachProcess(key, running);
        }
        finally
        {
            _admissionGate.Release();
        }

        // Killed OUTSIDE the gate so a multi-GB tree-kill does not serialize unrelated admissions — but still awaited
        // by this caller, because callers (notably the profiling path's ambient-VRAM baseline) rely on the child being
        // gone when this returns.
        if (detached is not null)
        {
            KillDetachedProcess(detached);
        }
    }

    /// <summary>
    ///     Removes a process from the table, retires its measured layer placement, and releases its port reservation —
    ///     everything that makes the slot available to the next admission — WITHOUT touching the child. Caller holds
    ///     the admission gate. Returns the process when this call won the removal race (the caller then owes it a
    ///     <see cref="KillDetachedProcess" />), or <see langword="null" /> when a concurrent path already removed it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the ONLY place a process leaves <see cref="_processes" />, so it is also the only place the layer
    ///         placement has to be retired: cap-admission eviction, the idle reaper, exited-process pruning, operator
    ///         eject (drained and forced), wedged-process respawn, the pre-respawn reap of a dead entry, the profiling
    ///         teardown, and shutdown all funnel through here.
    ///     </para>
    ///     <para>
    ///         The measured placement described THIS process. Once it is gone the reading is history — and because the
    ///         report ranks any partial reading above every full one, leaving it behind would keep telling an operator
    ///         that a model they unloaded is running partly from system RAM, for the rest of the app's lifetime and
    ///         even while the model actually loaded is fully GPU-resident.
    ///     </para>
    ///     <para>
    ///         INVARIANT: the port reservation is dropped here, before the child is killed, so the reservation set
    ///         (which is what bounds the loaded-model CAP) never counts a process that is on its way out. That does not
    ///         hand the next spawn a port the dying child still holds: <see cref="AllocatePort" /> bind-probes every
    ///         candidate (<see cref="IsPortFree" />) and skips one that is still bound. The bind probe was always the
    ///         real guard — <c>TreeKill</c> (<c>kill(-pgid)</c> / closing the Windows job) returns before the OS
    ///         reclaims the socket, so releasing the port after the kill never proved availability either.
    ///     </para>
    /// </remarks>
    private RunningProcess? DetachProcess(ProcessKey key, RunningProcess running)
    {
        if (!_processes.TryRemove(new KeyValuePair<ProcessKey, RunningProcess>(key, running)))
        {
            return null; // Already removed by a concurrent path.
        }

        _layerPlacementReport.Remove(key.Role, key.ModelName);
        ReleasePort(running.Port);
        return running;
    }

    /// <summary>Tree-kills + disposes a detached process. Never called while the admission gate is held.</summary>
    private static void KillDetachedProcess(RunningProcess running)
    {
        try
        {
            running.Handle.TreeKill();
        }
        finally
        {
            running.Handle.Dispose();
        }
    }

    /// <summary>
    ///     Tree-kills every process detached during an admission decision. A teardown failure is logged, never
    ///     rethrown: the admission it trails has already succeeded (or failed with its own cap error), and turning a
    ///     kill failure into the caller's exception would both mask that error and skip the remaining victims.
    /// </summary>
    private void KillDetachedProcesses(List<RunningProcess> detached)
    {
        foreach (var running in detached)
        {
            try
            {
                KillDetachedProcess(running);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Tearing down an evicted llama-server (pid {ProcessId}) failed; its slot and port were already released.",
                    running.Handle.ProcessId);
            }
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

        /// <summary>Immutable snapshot of the exact argv for the candidate that reached readiness.</summary>
        public IReadOnlyList<string> SuccessfulLaunchArguments { get; init; } = [];

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

        /// <summary>
        ///     Atomically claims this process as a cap-admission eviction victim: sets the evicting flag (so
        ///     <see cref="LlamaServerProcessSupervisor.TryAcquireInferenceLease" />'s post-acquire re-check refuses any
        ///     racing lease) and then re-checks that no lease slipped in first. Returns <see langword="false" /> —
        ///     clearing the flag — when a lease won the race, so a process is never torn down under in-flight inference.
        /// </summary>
        public bool TryBeginEvict()
        {
            if (Interlocked.CompareExchange(ref _evicting, value: 1, comparand: 0) != 0)
            {
                return false; // An operator eject already owns this process's teardown.
            }

            if (ActiveLeases > 0)
            {
                ClearEvicting();
                return false;
            }

            return true;
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
