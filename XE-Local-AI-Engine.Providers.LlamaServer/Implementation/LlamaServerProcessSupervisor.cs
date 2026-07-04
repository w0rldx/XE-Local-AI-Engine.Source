namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
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

    /// <summary>Cold-start readiness budget for a freshly spawned process before its first request.</summary>
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Poll cadence for observing that a freshly spawned process exited during its readiness wait.</summary>
    private static readonly TimeSpan ProcessExitPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Base delay between crash-restart attempts; grows linearly per attempt.</summary>
    private static readonly TimeSpan RestartBackoffStep = TimeSpan.FromMilliseconds(250);

    // Guards the loaded-cap admission decision + port-set mutation so the cap can never be exceeded by a race.
    private readonly SemaphoreSlim _admissionGate = new(initialCount: 1, maxCount: 1);
    private readonly HashSet<int> _allocatedPorts = [];
    private readonly ILlamaCppBinaryManager _binaryManager;

    // Single-flight ensure-running gate, one semaphore per (model, role) key.
    private readonly ConcurrentDictionary<ProcessKey, SemaphoreSlim> _ensureGates = new();
    private readonly LlamaServerExternalEndpointOptions _externalEndpoints;
    private readonly ILlamaServerHealthProbe _healthProbe;
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
        LlamaServerExternalEndpointOptions? externalEndpoints = null,
        TimeProvider? timeProvider = null)
    {
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _profileResolver = profileResolver ?? throw new ArgumentNullException(nameof(profileResolver));
        _externalEndpoints = externalEndpoints ?? new LlamaServerExternalEndpointOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;

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

        // Single-flight: concurrent callers for the same key spawn exactly once.
        var gate = _ensureGates.GetOrAdd(key, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — another caller may have spawned while we waited.
            if (_processes.TryGetValue(key, out existing) && !existing.Handle.HasExited)
            {
                var reused = await TryReuseAsync(key, existing, ct).ConfigureAwait(false);
                if (reused is not null)
                {
                    return reused;
                }
            }

            // A crashed/exited/wedged process lingering under this key is reaped before respawn (a wedged one was already
            // torn down by TryReuseAsync; RemoveProcessAsync is idempotent on the instance so the extra call is a no-op).
            if (existing is not null)
            {
                await RemoveProcessAsync(key, existing).ConfigureAwait(false);
            }

            var running = await SpawnWithRestartAsync(key, ct).ConfigureAwait(false);
            return running.Endpoint;
        }
        finally
        {
            gate.Release();
        }
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
                // Deterministic failures (cap reached, model not installed, no free port) are policy outcomes, not
                // transient crashes — surface them as-is instead of burning retries on a guaranteed re-failure.
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
        // slow profile read never stalls admission for other keys. No startup capture, no forced --metrics: byte-for-
        // byte the same spawn as the pre-profiling path.
        return SpawnCoreAsync(key,
            (variant, c) => _profileResolver.ResolveAsync(key.ModelName, key.Role, variant, c),
            startupCapture: null,
            ensureMetrics: false,
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
        CancellationToken ct)
    {
        var modelFilePath = await _modelStore.ResolveModelFilePathAsync(key.ModelName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modelFilePath))
        {
            throw NonRetryable("The requested model is not installed.");
        }

        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        var binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);

        // Resolve the launch args (frozen-profile replay or explore-mode auto-fit, or operator-supplied profiling args)
        // for this (model, role, backend) BEFORE taking the admission gate, so a slow profile read never stalls
        // admission for other keys.
        var resolved = await resolveArgs(variant, ct).ConfigureAwait(false);

        var port = await AdmitAndAllocatePortAsync(ct).ConfigureAwait(false);

        ILlamaServerProcessHandle? handle = null;
        try
        {
            var spec = BuildLaunchSpec(key, binary.ServerExecutablePath, modelFilePath, port, variant, resolved);

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

            await WaitForReadyOrExitAsync(handle, spec.BaseAddress, ct).ConfigureAwait(false);

            var endpoint = new LlamaServerEndpoint(key.ModelName, key.Role, spec.BaseAddress);
            var running = new RunningProcess(handle, endpoint, port, _timeProvider.GetUtcNow());
            _processes[key] = running;
            return running;
        }
        catch
        {
            // Launch/readiness failed: tree-kill the half-started child and free its reserved port (under the
            // admission gate, since the reserved-port set backs the cap count) before bubbling up.
            handle?.TreeKill();
            handle?.Dispose();
            await ReleaseReservedPortAsync(port).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Waits for a freshly launched process to pass its readiness probe, racing that wait against the process
    ///     exiting. A child that dies during load (an incompatible model, or a context that will not fit in the
    ///     available memory) is detected the instant it exits and surfaced as a NON-RETRYABLE failure — instead of
    ///     polling <c>/health</c> against a dead endpoint for the full readiness budget and then retrying. A
    ///     crash-on-load is deterministic, so retrying it only multiplies the stall by <c>MaxRestartAttempts</c>.
    /// </summary>
    private async Task WaitForReadyOrExitAsync(ILlamaServerProcessHandle handle, Uri baseAddress, CancellationToken ct)
    {
        // Cancel the losing side the instant the other wins, so neither the /health poll nor the exit-watcher is left
        // running after the race is decided.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var readyTask = _healthProbe.WaitForReadyAsync(baseAddress, ReadinessTimeout, linkedCts.Token);
        var exitTask = WatchForExitAsync(handle, linkedCts.Token);

        var winner = await Task.WhenAny(readyTask, exitTask).ConfigureAwait(false);

        if (winner == exitTask && handle.HasExited)
        {
            // The child exited before it ever became ready: a deterministic load failure. Stop the abandoned /health
            // poll and surface a sanitized, non-retryable error (no file paths) so the caller fails fast.
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await SwallowCancellationAsync(readyTask).ConfigureAwait(false);
            throw NonRetryable(
                "The local model runtime exited while loading the model. The model may be incompatible with this runtime or too large for the available memory.");
        }

        // Readiness settled first: stop the exit-watcher and honor the existing outcome — a genuine timeout (process
        // still alive but slow) stays a retryable "did not become ready in time".
        await linkedCts.CancelAsync().ConfigureAwait(false);
        await SwallowCancellationAsync(exitTask).ConfigureAwait(false);

        if (!await readyTask.ConfigureAwait(false))
        {
            throw new LlamaRuntimeException("The local model runtime did not become ready in time.");
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

            // Spawn exactly one process with the operator-supplied args verbatim (bypass the profile resolver).
            var running = await SpawnCoreAsync(key,
                    (_, _) => Task.FromResult(launchArgs),
                    startupOutput.Enqueue,
                    ensureMetrics: enableMetrics,
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

    /// <summary>Builds the exact, ordered llama-server argument vector for a <c>(model, role)</c> on a port.</summary>
    internal static LlamaServerLaunchSpec BuildLaunchSpec(ProcessKey key,
        string executablePath,
        string modelFilePath,
        int port,
        GpuVariant variant,
        ResolvedLaunchArguments resolved)
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

        // The variant only selects the GPU-enabled llama.cpp BUILD (Cuda/Vulkan); the CPU variant stays a pure CPU run
        // and emits NO gpu/fit args. For a GPU build, placement is no longer forced (the old --n-gpu-layers 999 is gone):
        // llama.cpp's default --fit auto-fit now drives layer/expert placement. Explore mode emits --fit on + --metrics so
        // llama.cpp fits and prints the chosen params for capture; replay mode emits the frozen profile args verbatim
        // (and omits --fit, since any explicit fit-arg disables auto-fit — the two are mutually exclusive per run).
        if (variant != GpuVariant.Cpu)
        {
            AppendGpuPlacementArgs(args, resolved);
        }

        if (key.Role == ModelRole.Chat)
        {
            // Mandatory for tool/function calling — without it llama-server ignores the GGUF tool grammar.
            args.Add("--jinja");
        }
        else
        {
            // /v1/embeddings is exposed only with --embeddings + a non-`none` pooling type.
            args.Add("--embeddings");
            args.Add("--pooling");
            args.Add("mean");
        }

        var workingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath)) ?? Environment.CurrentDirectory;
        return new LlamaServerLaunchSpec(key.ModelName, key.Role, executablePath, args, port, workingDirectory);
    }

    /// <summary>
    ///     Appends the GPU placement args for a non-CPU build: explore-mode auto-fit (<c>--fit on</c> + <c>--metrics</c>)
    ///     or the resolved profile's explicit replay args (<c>-c/-ngl/-ts/-ot</c> + matched <c>-ctk/-ctv</c> with
    ///     <c>--flash-attn on</c>). The two paths are mutually exclusive — replay never sets <c>--fit</c>.
    /// </summary>
    private static void AppendGpuPlacementArgs(List<string> args, ResolvedLaunchArguments resolved)
    {
        if (resolved.ExploreMode)
        {
            // Let llama.cpp auto-fit choose and print placement; --metrics exposes the throughput/cache gauges the
            // benchmark reads. Emitting any explicit fit-arg here would disable auto-fit, so emit none.
            args.Add("--fit");
            args.Add("on");
            args.Add("--metrics");
            return;
        }

        // Replay a frozen/explored profile verbatim. --fit is intentionally absent (an explicit fit-arg disables it).
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

    /// <summary>A live, registered process and its last-used timestamp (drives idle-TTL + LRU eviction).</summary>
    private sealed class RunningProcess(ILlamaServerProcessHandle handle, LlamaServerEndpoint endpoint, int port, DateTimeOffset startedUtc)
    {
        private long _lastUsedTicks = startedUtc.UtcTicks;

        // Seeded to the spawn time so a freshly-ready process is not re-probed until one full interval has passed.
        private long _lastLivenessProbeTicks = startedUtc.UtcTicks;
        private int _consecutiveLivenessFailures;
        private int _profilingPinned;

        public ILlamaServerProcessHandle Handle { get; } = handle;

        public LlamaServerEndpoint Endpoint { get; } = endpoint;

        public int Port { get; } = port;

        public DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

        /// <summary>
        ///     <see langword="true" /> while an operator profiling benchmark owns this process; the idle reaper and the
        ///     cap-admission LRU eviction skip a pinned, non-exited process so it is never torn down mid-measurement.
        /// </summary>
        public bool IsProfilingPinned => Volatile.Read(ref _profilingPinned) != 0;

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
