namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Shared fakes for the <see cref="LlamaServerProcessSupervisor" /> tests: a process launcher that records the
///     exact launch specs and hands back controllable in-memory process handles (no real <c>llama-server</c>), a
///     deterministic health probe, a fixed binary manager / variant selector, and a time provider whose clock can be
///     advanced while delays still use real (tiny) timers.
/// </summary>
internal sealed class FakeProcessLauncher : ILlamaServerProcessLauncher
{
    private readonly Func<LlamaServerLaunchSpec, FakeProcessHandle>? _onLaunch;
    private int _nextPid = 1000;

    public FakeProcessLauncher(Func<LlamaServerLaunchSpec, FakeProcessHandle>? onLaunch = null)
    {
        _onLaunch = onLaunch;
    }

    public ConcurrentQueue<LlamaServerLaunchSpec> Launches { get; } = new();

    public ConcurrentBag<FakeProcessHandle> Handles { get; } = new();

    public int LaunchCount => Launches.Count;

    public ILlamaServerProcessHandle Launch(LlamaServerLaunchSpec spec)
    {
        Launches.Enqueue(spec);
#pragma warning disable CA2000 // Ownership of the handle transfers to the supervisor under test, which disposes it on teardown.
        var handle = _onLaunch?.Invoke(spec) ?? new FakeProcessHandle(Interlocked.Increment(ref _nextPid));
#pragma warning restore CA2000
        Handles.Add(handle);
        return handle;
    }
}

/// <summary>An in-memory process handle whose exit + tree-kill are directly controllable by the test.</summary>
internal sealed class FakeProcessHandle(int pid) : ILlamaServerProcessHandle
{
    private int _exited;
    private int _killed;

    public bool WasTreeKilled => Volatile.Read(ref _killed) != 0;

    public bool WasDisposed { get; private set; }

    public int ProcessId { get; } = pid;

    public bool HasExited => Volatile.Read(ref _exited) != 0;

    public void TreeKill()
    {
        Interlocked.Exchange(ref _killed, 1);
        Interlocked.Exchange(ref _exited, 1);
    }

    public void Dispose()
    {
        WasDisposed = true;
    }

    /// <summary>Simulates a process crash/exit so the next ensure-running sees a dead process.</summary>
    public void SimulateExit()
    {
        Interlocked.Exchange(ref _exited, 1);
    }
}

/// <summary>Health probe with controllable readiness; defaults to immediately-ready + responsive.</summary>
internal sealed class FakeHealthProbe(bool ready = true, bool responsive = true) : ILlamaServerHealthProbe
{
    public bool Ready { get; set; } = ready;

    public bool Responsive { get; set; } = responsive;

    public Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
    {
        return Task.FromResult(Ready);
    }

    public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
    {
        return Task.FromResult(Responsive);
    }
}

/// <summary>
///     Health probe whose readiness wait blocks on a gate the test releases, so multiple distinct-model spawns can be
///     held in-flight simultaneously to exercise the concurrent-cap race.
/// </summary>
internal sealed class GatedHealthProbe : ILlamaServerHealthProbe
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _waiting;

    /// <summary>Number of spawns currently parked in the readiness wait.</summary>
    public int Waiting => Volatile.Read(ref _waiting);

    public async Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }
    }

    public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
    {
        return Task.FromResult(true);
    }

    /// <summary>Releases every parked (and future) readiness wait.</summary>
    public void Release()
    {
        _release.TrySetResult();
    }
}

/// <summary>Binary manager returning a fixed fake server path for whatever variant is requested; never downloads.</summary>
internal sealed class FakeBinaryManager : ILlamaCppBinaryManager
{
    public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct)
    {
        return Task.FromResult(new LlamaBinary("/fake/bin/llama-server", "b9692", variant, true));
    }
}

/// <summary>Variant selector returning a fixed variant; never probes hardware.</summary>
internal sealed class FakeVariantSelector(GpuVariant variant = GpuVariant.Cpu) : IGpuVariantSelector
{
    public Task<GpuVariant> SelectVariantAsync(CancellationToken ct)
    {
        return Task.FromResult(variant);
    }
}

/// <summary>
///     GGUF store fake: resolves a model name to a fixed path (null means "not installed") and reports an optional
///     fixed installed-model list. The download/delete surface is not exercised by the supervisor/provider tests, so
///     <see cref="EnsureModelAsync" /> throws and delete/exists are trivial.
/// </summary>
internal sealed class FakeModelStore(
    string? fixedPath = "/fake/models/model.gguf",
    IReadOnlyList<string>? installedModelNames = null) : IGgufModelStore
{
    public Task<string?> ResolveModelFilePathAsync(string modelName, CancellationToken ct)
    {
        return Task.FromResult(fixedPath);
    }

    public Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct)
    {
        IReadOnlyList<LocalModelDescriptor> descriptors = (installedModelNames ?? [])
                                                          .Select(name => new LocalModelDescriptor
                                                          {
                                                              ModelName = name,
                                                              ProviderName = LlamaServerProviderConstants.ProviderName,
                                                              IsAvailable = true,
                                                              SizeBytes = null,
                                                              ModifiedAt = null,
                                                              MaxContextTokens = null
                                                          })
                                                          .ToList();

        return Task.FromResult(descriptors);
    }

    public Task<GgufModelHandle> EnsureModelAsync(GgufModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        throw new NotSupportedException("FakeModelStore does not download.");
    }

    public Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string modelName, CancellationToken ct)
    {
        return Task.FromResult(fixedPath is not null);
    }
}

/// <summary>
///     Time provider whose <see cref="GetUtcNow" /> is advanceable (drives idle-TTL/LRU comparisons deterministically)
///     while timer creation falls through to the real provider so <c>Task.Delay</c> still completes.
/// </summary>
internal sealed class AdvanceableTimeProvider : TimeProvider
{
    private long _utcTicks = DateTimeOffset.UtcNow.UtcTicks;

    public override DateTimeOffset GetUtcNow()
    {
        return new DateTimeOffset(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
    }

    public void Advance(TimeSpan delta)
    {
        Interlocked.Add(ref _utcTicks, delta.Ticks);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        return System.CreateTimer(callback, state, dueTime, period);
    }
}
