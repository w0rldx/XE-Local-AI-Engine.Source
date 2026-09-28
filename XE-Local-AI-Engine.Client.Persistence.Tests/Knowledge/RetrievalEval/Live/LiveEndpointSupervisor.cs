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
///     A key registered with <see cref="RegisterLazy" /> spawns on its first ensure, as production does: the spawn runs
///     DETACHED under the eval's lifetime token and each caller only awaits it with its own token
///     (<c>LlamaServerProcessSupervisor.AwaitDetachedSpawnAsync</c>), so a search budget that fires mid-spawn abandons the
///     wait, not the spawn. The supervisor owns lazily spawned servers and kills them on dispose.
/// </remarks>
internal sealed class LiveEndpointSupervisor : ILlamaServerProcessSupervisor, IAsyncDisposable
{
    private static readonly NoOpLease SharedLease = new();

    private readonly ConcurrentDictionary<(string ModelName, ModelRole Role), Uri> _endpoints = new();
    private readonly ConcurrentDictionary<(string ModelName, ModelRole Role), Lazy<Task<LiveLlamaServer>>> _lazy = new();

    /// <summary>Routes <paramref name="modelName" /> in <paramref name="role" /> to <paramref name="baseAddress" /> (a <c>.../v1</c> base).</summary>
    public void Register(string modelName, ModelRole role, Uri baseAddress) =>
        _endpoints[(modelName, role)] = baseAddress;

    /// <summary>Routes the key to a server <paramref name="spawn" /> starts on the first ensure, detached under <paramref name="lifetime" />.</summary>
    public void RegisterLazy(string modelName, ModelRole role, Func<CancellationToken, Task<LiveLlamaServer>> spawn, CancellationToken lifetime) =>
        _lazy[(modelName, role)] = new Lazy<Task<LiveLlamaServer>>(() => spawn(lifetime));

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

    public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role) =>
        _endpoints.ContainsKey((modelName, role)) || SpawnedServer(modelName, role) is not null
            ? LlamaServerLeaseAcquisition.Granted(SharedLease)
            : LlamaServerLeaseAcquisition.NotRunning;

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

    private sealed class NoOpLease : ILlamaServerInferenceLease
    {
        public bool WasEjected => false;

        public void Dispose()
        {
        }
    }
}
