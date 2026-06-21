namespace XE_Local_AI_Engine.Providers.LlamaServer;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using XE_Local_AI_Engine.Providers.Abstractions;

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

    /// <summary>Base delay between crash-restart attempts; grows linearly per attempt.</summary>
    private static readonly TimeSpan RestartBackoffStep = TimeSpan.FromMilliseconds(250);

    // Guards the loaded-cap admission decision + port-set mutation so the cap can never be exceeded by a race.
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private readonly HashSet<int> _allocatedPorts = [];
    private readonly ILlamaCppBinaryManager _binaryManager;

    // Single-flight ensure-running gate, one semaphore per (model, role) key.
    private readonly ConcurrentDictionary<ProcessKey, SemaphoreSlim> _ensureGates = new();
    private readonly LlamaServerExternalEndpointOptions _externalEndpoints;
    private readonly ILlamaServerHealthProbe _healthProbe;
    private readonly ILlamaServerProcessLauncher _launcher;
    private readonly IGgufModelStore _modelStore;
    private readonly LlamaServerSupervisorOptions _options;

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
        LlamaServerExternalEndpointOptions? externalEndpoints = null,
        TimeProvider? timeProvider = null)
    {
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _externalEndpoints = externalEndpoints ?? new LlamaServerExternalEndpointOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;

        _reaperLoop = Task.Run(() => ReapIdleLoopAsync(_shutdownCts.Token));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
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

        // Fast path: an already-running, live process is reused without taking the spawn gate.
        if (_processes.TryGetValue(key, out var existing) && !existing.Handle.HasExited)
        {
            existing.MarkUsed(_timeProvider.GetUtcNow());
            return existing.Endpoint;
        }

        // Single-flight: concurrent callers for the same key spawn exactly once.
        var gate = _ensureGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — another caller may have spawned while we waited.
            if (_processes.TryGetValue(key, out existing) && !existing.Handle.HasExited)
            {
                existing.MarkUsed(_timeProvider.GetUtcNow());
                return existing.Endpoint;
            }

            // A crashed/exited process lingering under this key is reaped before respawn.
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

    private async Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthCoreAsync(KeyValuePair<ProcessKey, RunningProcess>[] snapshot,
        CancellationToken ct)
    {
        var healths = new List<LlamaServerProcessHealth>(snapshot.Length);
        foreach (var (key, running) in snapshot)
        {
            if (running.Handle.HasExited)
            {
                healths.Add(new LlamaServerProcessHealth(key.ModelName, key.Role, false, "Process has exited."));
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

    /// <summary>One spawn attempt: admit under the cap, allocate a port, launch, health-probe, register.</summary>
    private async Task<RunningProcess> SpawnOnceAsync(ProcessKey key, CancellationToken ct)
    {
        var modelFilePath = await _modelStore.ResolveModelFilePathAsync(key.ModelName, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(modelFilePath))
        {
            throw NonRetryable("The requested model is not installed.");
        }

        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        var binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);

        var port = await AdmitAndAllocatePortAsync(ct).ConfigureAwait(false);

        ILlamaServerProcessHandle? handle = null;
        try
        {
            var spec = BuildLaunchSpec(key, binary.ServerExecutablePath, modelFilePath, port, variant);
            handle = _launcher.Launch(spec);

            var ready = await _healthProbe
                              .WaitForReadyAsync(spec.BaseAddress, ReadinessTimeout, ct)
                              .ConfigureAwait(false);
            if (!ready)
            {
                throw new LlamaRuntimeException("The local model runtime did not become ready in time.");
            }

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

    // Offload every model layer to the GPU. llama.cpp clamps this to the model's actual layer count, so a value above
    // any real model's layer count means "all layers on the GPU". Only applied for a GPU runtime variant.
    private const string OffloadAllGpuLayers = "999";

    /// <summary>Builds the exact, ordered llama-server argument vector for a <c>(model, role)</c> on a port.</summary>
    internal static LlamaServerLaunchSpec BuildLaunchSpec(ProcessKey key, string executablePath, string modelFilePath, int port, GpuVariant variant)
    {
        var args = new List<string>
        {
            "-m",
            modelFilePath,
            "--host",
            "127.0.0.1", // localhost-only bind
            "--port",
            port.ToString(CultureInfo.InvariantCulture)
        };

        // The variant only selects the GPU-enabled llama.cpp BUILD (Cuda/Vulkan). llama-server's default n-gpu-layers
        // is 0, so without this flag every layer stays in system RAM and inference runs on the CPU even though CUDA is
        // present and reported by llama.cpp — exactly the "model loaded in RAM, CUDA detected" symptom. Passing
        // --n-gpu-layers offloads the layers to the GPU. Omitted for the CPU variant so it stays a pure CPU run.
        if (variant != GpuVariant.Cpu)
        {
            args.Add("--n-gpu-layers");
            args.Add(OffloadAllGpuLayers);
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

        public ILlamaServerProcessHandle Handle { get; } = handle;

        public LlamaServerEndpoint Endpoint { get; } = endpoint;

        public int Port { get; } = port;

        public DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

        public void MarkUsed(DateTimeOffset now)
        {
            Interlocked.Exchange(ref _lastUsedTicks, now.UtcTicks);
        }
    }
}
