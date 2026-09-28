namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Minimal <see cref="ILlamaServerProcessSupervisor" /> mapping <c>(modelName, role)</c> to the endpoint of a
///     server the eval launched itself, so the REAL <c>LlamaServerRerankerClient</c> and
///     <c>LlamaServerLocalModelProvider</c> run unmodified. Only what those two call is implemented
///     (<see cref="EnsureRunningAsync" />, <see cref="TryAcquireInferenceLease" />); the rest throws.
/// </summary>
/// <remarks>
///     A key registered with <see cref="RegisterLazy" /> spawns on its first ensure (or <see cref="WarmLazyAsync" />), detached
///     under the eval's lifetime token, and reports <see cref="LlamaServerLeaseAcquisition.NotRunning" /> until its server is
///     up, as the real supervisor does. Leases are counted per key so the eval can wait out a call the client abandoned.
///     The supervisor owns lazily spawned servers and kills them on dispose.
/// </remarks>
internal sealed class LiveEndpointSupervisor : ILlamaServerProcessSupervisor, IAsyncDisposable
{
    private readonly ConcurrentDictionary<(string ModelName, ModelRole Role), Uri> _endpoints = new();
    private readonly ConcurrentDictionary<(string ModelName, ModelRole Role), Lazy<Task<LiveLlamaServer>>> _lazy = new();
    private readonly ConcurrentDictionary<(string ModelName, ModelRole Role), LeaseCounter> _leases = new();

    /// <summary>Routes <paramref name="modelName" /> in <paramref name="role" /> to <paramref name="baseAddress" /> (a <c>.../v1</c> base).</summary>
    public void Register(string modelName, ModelRole role, Uri baseAddress) =>
        _endpoints[(modelName, role)] = baseAddress;

    /// <summary>Routes the key to a server <paramref name="spawn" /> starts on the first ensure, detached under <paramref name="lifetime" />.</summary>
    public void RegisterLazy(string modelName, ModelRole role, Func<CancellationToken, Task<LiveLlamaServer>> spawn, CancellationToken lifetime) =>
        _lazy[(modelName, role)] = new Lazy<Task<LiveLlamaServer>>(() => spawn(lifetime));

    /// <summary>True once something started the lazy key's spawn (an ensure or <see cref="WarmLazyAsync" />).</summary>
    public bool LazyStarted(string modelName, ModelRole role) =>
        _lazy.TryGetValue((modelName, role), out var lazy) && lazy.IsValueCreated;

    /// <summary>Starts the lazy key's spawn, as the product's pre-warmer would, and waits for it (see <see cref="AwaitLazyAsync" />).</summary>
    public Task<(LiveLlamaServer? Server, string? Failure)> WarmLazyAsync(string modelName, ModelRole role)
    {
        if (_lazy.TryGetValue((modelName, role), out var lazy))
        {
            _ = lazy.Value;
        }

        return AwaitLazyAsync(modelName, role);
    }

    /// <summary>Completes once no lease on the key is outstanding, i.e. every call the client abandoned has settled.</summary>
    public Task WaitForLeasesReleasedAsync(string modelName, ModelRole role, CancellationToken ct) =>
        _leases.GetOrAdd((modelName, role), static _ => new LeaseCounter()).WhenReleased().WaitAsync(ct);

    private LiveLlamaServer? SpawnedServer(string modelName, ModelRole role) =>
        _lazy.TryGetValue((modelName, role), out var lazy) && lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully ? lazy.Value.Result : null;

    /// <summary>
    ///     Waits for a lazy key's spawn, once started: its server, or the failure message when the spawn failed (both null
    ///     when it never started). A failed spawn is invisible to the caller, which the reranker client degrades on.
    /// </summary>
    public async Task<(LiveLlamaServer? Server, string? Failure)> AwaitLazyAsync(string modelName, ModelRole role)
    {
        if (!_lazy.TryGetValue((modelName, role), out var lazy) || !lazy.IsValueCreated)
        {
            return (null, null);
        }

        try
        {
            return (await lazy.Value, null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or HttpRequestException or IOException)
        {
            return (null, exception.Message);
        }
    }

    /// <summary>Unregisters a lazy key and kills its server, waiting out a spawn still in flight.</summary>
    public async Task ReleaseLazyAsync(string modelName, ModelRole role)
    {
        if (!_lazy.TryRemove((modelName, role), out var lazy) || !lazy.IsValueCreated)
        {
            return;
        }

        try
        {
            await (await lazy.Value).DisposeAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or HttpRequestException or IOException)
        {
            // The spawn itself failed and already tore its process down; nothing is left to kill.
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (modelName, role) in _lazy.Keys)
        {
            await ReleaseLazyAsync(modelName, role);
        }
    }

    public async Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        Uri? baseAddress;
        if (_lazy.TryGetValue((modelName, role), out var lazy))
        {
            try
            {
                baseAddress = (await lazy.Value.WaitAsync(ct)).BaseAddress;
            }
            catch (InvalidOperationException exception)
            {
                // A failed spawn surfaces as the product's runtime failure, which the reranker client degrades on.
                throw new LlamaRuntimeException($"The live {role} server for '{modelName}' failed to start.", exception);
            }
        }
        else if (!_endpoints.TryGetValue((modelName, role), out baseAddress))
        {
            // An unmapped key is the product's "model not installed" shape, which the reranker client degrades on.
            throw new LlamaRuntimeException($"No live server is registered for {role} model '{modelName}'.");
        }

        return new LlamaServerEndpoint
        {
            ModelName = modelName,
            Role = role,
            BaseAddress = baseAddress
        };
    }

    public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role)
    {
        var baseAddress = _endpoints.TryGetValue((modelName, role), out var registered) ? registered : SpawnedServer(modelName, role)?.BaseAddress;
        if (baseAddress is null)
        {
            return LlamaServerLeaseAcquisition.NotRunning;
        }

#pragma warning disable CA2000 // Ownership of the lease transfers to the caller inside the returned acquisition, as in the product supervisor.
        return LlamaServerLeaseAcquisition.Granted(_leases.GetOrAdd((modelName, role), static _ => new LeaseCounter()).Acquire(), new LlamaServerEndpoint
        {
            ModelName = modelName,
            Role = role,
            BaseAddress = baseAddress
        });
#pragma warning restore CA2000
    }

    public Task EvictAsync(string modelName, ModelRole role, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<T> RunExclusiveProfilingAsync<T>(string modelName,
        ModelRole role,
        ResolvedLaunchArguments launchArgs,
        bool enableMetrics,
        Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body,
        CancellationToken ct,
        Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>? captureVramBeforeSpawn = null) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct) =>
        throw new NotSupportedException();

    public int CountRunningProcesses() =>
        throw new NotSupportedException();

    public LlamaServerRuntimeInfo? GetRuntimeInfo(string modelName, ModelRole role) =>
        throw new NotSupportedException();

    private sealed class LeaseCounter
    {
        private readonly Lock _gate = new();
        private int _active;
        private TaskCompletionSource _released = CompletedSource();

        public ILlamaServerInferenceLease Acquire()
        {
            lock (_gate)
            {
                if (_active++ == 0)
                {
                    _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }

            return new Lease(this);
        }

        public Task WhenReleased()
        {
            lock (_gate)
            {
                return _released.Task;
            }
        }

        private void Release()
        {
            lock (_gate)
            {
                if (--_active == 0)
                {
                    _released.SetResult();
                }
            }
        }

        private static TaskCompletionSource CompletedSource()
        {
            var source = new TaskCompletionSource();
            source.SetResult();
            return source;
        }

        private sealed class Lease : ILlamaServerInferenceLease
        {
            private LeaseCounter? _owner;

            public Lease(LeaseCounter owner) =>
                _owner = owner;

            public bool WasEjected => false;

            public void Dispose() =>
                Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
