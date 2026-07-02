namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
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
    private readonly IImageModelStore _modelStore;
    private readonly StableDiffusionRuntimeOptions _options;

    // One running daemon per model key.
    private readonly ConcurrentDictionary<string, RunningServer> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly IImageServerReadinessProbe _readinessProbe;
    private readonly Task _reaperLoop;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    /// <summary>Creates the supervisor over its collaborators. The idle reaper loop starts immediately.</summary>
    internal ImageServerProcessSupervisor(IImageModelStore modelStore,
        ISdGpuBackendSelector backendSelector,
        IStableDiffusionBinaryManager binaryManager,
        IImageServerProcessLauncher launcher,
        IImageServerReadinessProbe readinessProbe,
        StableDiffusionRuntimeOptions options,
        TimeProvider? timeProvider = null)
    {
        _modelStore = modelStore ?? throw new ArgumentNullException(nameof(modelStore));
        _backendSelector = backendSelector ?? throw new ArgumentNullException(nameof(backendSelector));
        _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _readinessProbe = readinessProbe ?? throw new ArgumentNullException(nameof(readinessProbe));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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
    public async Task<ImageServerEndpoint> EnsureRunningAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Fast path: an already-running, live daemon is reused without taking the spawn gate.
        if (_processes.TryGetValue(modelName, out var existing) && !existing.Handle.HasExited)
        {
            existing.MarkUsed(_timeProvider.GetUtcNow());
            return existing.Endpoint;
        }

        return await SpawnUnderGateAsync(modelName, evictFirst: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ImageServerEndpoint> RestartAsync(string modelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

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

    private async Task<ImageServerEndpoint> SpawnUnderGateAsync(string modelName, bool evictFirst, CancellationToken ct)
    {
        var gate = _ensureGates.GetOrAdd(modelName, static _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate — another caller may have spawned while we waited (ensure path only).
            if (!evictFirst && _processes.TryGetValue(modelName, out var existing) && !existing.Handle.HasExited)
            {
                existing.MarkUsed(_timeProvider.GetUtcNow());
                return existing.Endpoint;
            }

            // A stale/exited (or, on restart, the outgoing) daemon under this key is reaped before respawn.
            if (_processes.TryGetValue(modelName, out var stale))
            {
                await RemoveProcessAsync(modelName, stale).ConfigureAwait(false);
            }

            var running = await SpawnOnceAsync(modelName, ct).ConfigureAwait(false);
            return running.Endpoint;
        }
        finally
        {
            gate.Release();
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

        var port = await AllocatePortAsync(ct).ConfigureAwait(false);

        IImageServerProcessHandle? handle = null;
        try
        {
            // The binary's OWN backend drives the launch flags — a bring-your-own override may serve a different backend
            // than the host probe selected (§ Lane A override contract).
            var spec = ImageServerArgumentBuilder.Build(modelName,
                binary.ServerExecutablePath,
                parts,
                binary.Backend,
                port,
                _options,
                Environment.ProcessorCount);

            handle = _launcher.Launch(spec);

            await WaitForReadyOrExitAsync(handle, spec.BaseAddress, ct).ConfigureAwait(false);

            var endpoint = new ImageServerEndpoint(modelName, spec.BaseAddress);
            var running = new RunningServer(handle, endpoint, port, _timeProvider.GetUtcNow());
            _processes[modelName] = running;
            return running;
        }
        catch
        {
            handle?.TreeKill();
            handle?.Dispose();
            await ReleaseReservedPortAsync(port).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Waits for the freshly launched daemon to answer <c>/sdcpp/v1/capabilities</c>, racing that against the process
    ///     exiting. sd-server binds its socket only after a successful model load, so an exit-before-ready is a
    ///     deterministic load failure (§4A): surface it immediately instead of polling a dead endpoint for the full budget.
    /// </summary>
    private async Task WaitForReadyOrExitAsync(IImageServerProcessHandle handle, Uri baseAddress, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var readyTask = _readinessProbe.WaitForReadyAsync(baseAddress, _options.ReadinessTimeout, linkedCts.Token);
        var exitTask = WatchForExitAsync(handle, linkedCts.Token);

        var winner = await Task.WhenAny(readyTask, exitTask).ConfigureAwait(false);

        if (winner == exitTask && handle.HasExited)
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            await SwallowCancellationAsync(readyTask).ConfigureAwait(false);
            throw new StableDiffusionRuntimeException(
                "The image runtime exited while loading the model. The model may be incompatible with this runtime or too large for the available memory.");
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
            if (running.Handle.HasExited || now - running.LastUsedUtc >= _options.IdleTimeToLive)
            {
                await RemoveProcessAsync(key, running).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> AllocatePortAsync(CancellationToken ct)
    {
        await _admissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            PruneExitedProcesses();
            return AllocatePort();
        }
        finally
        {
            _admissionGate.Release();
        }
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

    private async Task RemoveProcessAsync(string key, RunningServer running)
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

    private void TeardownProcess(string key, RunningServer running)
    {
        if (!_processes.TryRemove(new KeyValuePair<string, RunningServer>(key, running)))
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
    private sealed class RunningServer(IImageServerProcessHandle handle, ImageServerEndpoint endpoint, int port, DateTimeOffset startedUtc)
    {
        private long _lastUsedTicks = startedUtc.UtcTicks;

        public IImageServerProcessHandle Handle { get; } = handle;

        public ImageServerEndpoint Endpoint { get; } = endpoint;

        public int Port { get; } = port;

        public DateTimeOffset LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), TimeSpan.Zero);

        public void MarkUsed(DateTimeOffset now)
        {
            Interlocked.Exchange(ref _lastUsedTicks, now.UtcTicks);
        }
    }
}
