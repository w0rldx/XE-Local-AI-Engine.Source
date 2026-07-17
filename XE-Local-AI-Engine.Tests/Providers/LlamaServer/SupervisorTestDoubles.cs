namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

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

    /// <summary>
    ///     Canned startup lines a profiling spawn's <see cref="LlamaServerLaunchSpec.StartupCapture" /> sink receives on
    ///     launch — simulates the llama.cpp fit/device banner llama-server prints to stdout/stderr. Empty for normal
    ///     spawns (and harmlessly ignored when the launched spec has no capture sink).
    /// </summary>
    public IReadOnlyList<string> StartupLines { get; set; } = [];

    public ILlamaServerProcessHandle Launch(LlamaServerLaunchSpec spec)
    {
        Launches.Enqueue(spec);

        // Replay the canned startup output through the spec's capture sink, exactly as the production launcher forwards
        // each stdout/stderr line. Only profiling spawns set a sink, so a normal spawn skips this.
        if (spec.StartupCapture is { } capture)
        {
            foreach (var line in StartupLines)
            {
                capture(line);
            }
        }

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
        Interlocked.Exchange(ref _killed, value: 1);
        Interlocked.Exchange(ref _exited, value: 1);
    }

    public void Dispose()
    {
        WasDisposed = true;
    }

    /// <summary>Simulates a process crash/exit so the next ensure-running sees a dead process.</summary>
    public void SimulateExit()
    {
        Interlocked.Exchange(ref _exited, value: 1);
    }
}

/// <summary>Health probe with controllable readiness; defaults to immediately-ready + responsive.</summary>
internal sealed class FakeHealthProbe(bool ready = true, bool responsive = true) : ILlamaServerHealthProbe
{
    public bool Ready { get; set; } = ready;

    public bool Responsive { get; set; } = responsive;

    /// <summary>The effective context window /props reports; null (default) means "unknown" for the effective-ctx read.</summary>
    public int? EffectiveContextTokens { get; set; }

    public Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
    {
        return Task.FromResult(Ready);
    }

    public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
    {
        return Task.FromResult(Responsive);
    }

    public Task<int?> TryReadEffectiveContextTokensAsync(Uri baseAddress, CancellationToken ct)
    {
        return Task.FromResult(EffectiveContextTokens);
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

    public Task<int?> TryReadEffectiveContextTokensAsync(Uri baseAddress, CancellationToken ct)
    {
        return Task.FromResult<int?>(null);
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
        return Task.FromResult(new LlamaBinary("/fake/bin/llama-server", "b9692", variant, IsPinnedFallback: true));
    }

    public Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct)
    {
        return Task.FromResult(new LlamaBinary("/fake/bin/llama-server", tag, variant, IsPinnedFallback: false));
    }

    public Task<InstalledRuntimeState> AdoptCudaSourceBuildAsync(string buildBinDir, string tag, CancellationToken ct)
    {
        return Task.FromResult(new InstalledRuntimeState(tag, "(source-build:cuda)", new string('a', 64), GpuVariant.Cuda, DateTimeOffset.UtcNow, buildBinDir));
    }

    public Task RemoveCudaSourceBuildAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
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
///     Inference-profile resolver returning a fixed <see cref="ResolvedLaunchArguments" /> (default: explore-mode) and
///     recording the resolve calls so a test can assert the supervisor awaited it on the spawn path.
/// </summary>
internal sealed class FakeInferenceProfileResolver(ResolvedLaunchArguments? resolved = null) : IInferenceProfileResolver
{
    private readonly ResolvedLaunchArguments _resolved = resolved ?? ResolvedLaunchArguments.Explore();

    public ConcurrentQueue<(string ModelName, ModelRole Role, GpuVariant Backend)> Calls { get; } = new();

    public Task<ResolvedLaunchArguments> ResolveAsync(string modelName, ModelRole role, GpuVariant backend, CancellationToken ct)
    {
        Calls.Enqueue((modelName, role, backend));
        return Task.FromResult(_resolved);
    }
}

/// <summary>
///     Supervisor fake whose <see cref="CheckHealthAsync" /> returns a configurable health list so the runtime-status
///     running-count surface and the pre-update 409 safety gate can be exercised deterministically without spawning any
///     real <c>llama-server</c>. The ensure/evict surface is unused by those tests and is a no-op.
/// </summary>
internal sealed class FakeProcessSupervisor(params LlamaServerProcessHealth[] running) : ILlamaServerProcessSupervisor
{
    private readonly IReadOnlyList<LlamaServerProcessHealth> _running = running ?? [];

    /// <summary>
    ///     Endpoint <see cref="EnsureRunningAsync" /> hands out; <see langword="null" /> (default) keeps the legacy
    ///     not-supported behavior for tests that never exercise the ensure path.
    /// </summary>
    public Uri? EnsureEndpoint { get; set; }

    /// <summary>
    ///     The acquisition <see cref="TryAcquireInferenceLease" /> returns. Defaults to
    ///     <see cref="LlamaServerLeaseAcquisition.NotRunning" /> (no lease, not evicting).
    /// </summary>
    public LlamaServerLeaseAcquisition LeaseAcquisition { get; set; } = LlamaServerLeaseAcquisition.NotRunning;

    public Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        if (EnsureEndpoint is { } endpoint)
        {
            return Task.FromResult(new LlamaServerEndpoint(modelName, role, endpoint));
        }

        throw new NotSupportedException("FakeProcessSupervisor does not ensure-run.");
    }

    public Task EvictAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct)
    {
        return Task.FromResult(LlamaServerEjectOutcome.NotRunning);
    }

    public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role)
    {
        return LeaseAcquisition;
    }

    public Task<T> RunExclusiveProfilingAsync<T>(string modelName,
        ModelRole role,
        ResolvedLaunchArguments launchArgs,
        bool enableMetrics,
        Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        throw new NotSupportedException("FakeProcessSupervisor does not run profiling.");
    }

    public Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct)
    {
        return Task.FromResult(_running);
    }

    public int CountRunningProcesses()
    {
        return _running.Count;
    }

    public LlamaServerRuntimeInfo? GetRuntimeInfo(string modelName, ModelRole role)
    {
        return null;
    }

    /// <summary>One responsive chat process health entry — a convenience for "a model is running" gate tests.</summary>
    public static LlamaServerProcessHealth RunningChat(string modelName = "demo-model")
    {
        return new LlamaServerProcessHealth(modelName, ModelRole.Chat, IsResponsive: true, Detail: "running");
    }
}

/// <summary>
///     In-memory <see cref="ILlamaServerLaunchFallbackStore" /> for tests: records disabled optimized backends without
///     touching disk. Exposes the recorded set so a test can assert the one-shot KV-quant fallback was persisted.
/// </summary>
internal sealed class FakeLaunchFallbackStore : ILlamaServerLaunchFallbackStore
{
    private readonly HashSet<GpuVariant> _disabled = [];

    public IReadOnlyCollection<GpuVariant> Disabled => _disabled;

    public Task<bool> IsOptimizedConfigDisabledAsync(GpuVariant variant, CancellationToken ct)
    {
        return Task.FromResult(_disabled.Contains(variant));
    }

    public Task DisableOptimizedConfigAsync(GpuVariant variant, CancellationToken ct)
    {
        _disabled.Add(variant);
        return Task.CompletedTask;
    }

    /// <summary>Seeds a backend as already-disabled so a spawn skips the optimized config from the start.</summary>
    public void Disable(GpuVariant variant)
    {
        _disabled.Add(variant);
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

    public Task<string> ResolveModelNameAsync(GgufModelRequest request, CancellationToken ct)
    {
        throw new NotSupportedException("FakeModelStore does not resolve model names.");
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

    public Task<GgufModelFootprintFacts?> ResolveModelFootprintFactsAsync(string modelName, CancellationToken ct)
    {
        return Task.FromResult<GgufModelFootprintFacts?>(null);
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
