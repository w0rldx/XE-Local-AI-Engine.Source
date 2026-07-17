namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;

/// <summary>
///     Shared fakes for the <see cref="ImageServerProcessSupervisor" /> tests: a launcher that records launch specs and
///     hands back controllable in-memory handles (no real <c>sd-server</c>), a readiness probe with toggleable
///     readiness + liveness, a fixed model store / backend selector / binary manager, and an advanceable clock. Mirrors
///     the LlamaServer supervisor test doubles (reduced: no role split, no profiling).
/// </summary>
internal sealed class FakeImageProcessLauncher : IImageServerProcessLauncher
{
    private int _nextPid = 2000;

    public ConcurrentQueue<ImageServerLaunchSpec> Launches { get; } = new();

    public ConcurrentBag<FakeImageProcessHandle> Handles { get; } = new();

    public int LaunchCount => Launches.Count;

    public IImageServerProcessHandle Launch(ImageServerLaunchSpec spec)
    {
        Launches.Enqueue(spec);
#pragma warning disable CA2000 // Ownership of the handle transfers to the supervisor under test, which disposes it on teardown.
        var handle = new FakeImageProcessHandle(Interlocked.Increment(ref _nextPid));
#pragma warning restore CA2000
        Handles.Add(handle);
        return handle;
    }
}

/// <summary>An in-memory image-server handle whose exit + tree-kill are directly controllable by the test.</summary>
internal sealed class FakeImageProcessHandle(int pid) : IImageServerProcessHandle
{
    private int _exited;
    private int _killed;

    public bool WasTreeKilled => Volatile.Read(ref _killed) != 0;

    public int ProcessId { get; } = pid;

    public bool HasExited => Volatile.Read(ref _exited) != 0;

    public void TreeKill()
    {
        Interlocked.Exchange(ref _killed, value: 1);
        Interlocked.Exchange(ref _exited, value: 1);
    }

    public void Dispose()
    {
        // No unmanaged resources in the fake.
    }

    /// <summary>Simulates a daemon crash/exit so the next ensure-running sees a dead process.</summary>
    public void SimulateExit()
    {
        Interlocked.Exchange(ref _exited, value: 1);
    }
}

/// <summary>Readiness probe with controllable readiness + liveness; defaults to immediately-ready + responsive.</summary>
internal sealed class FakeImageReadinessProbe(bool ready = true, bool responsive = true) : IImageServerReadinessProbe
{
    private int _responsiveChecks;

    public bool Ready { get; set; } = ready;

    public bool Responsive { get; set; } = responsive;

    /// <summary>Count of reuse-path liveness probes issued — asserts the hot path did / did not probe.</summary>
    public int ResponsiveChecks => Volatile.Read(ref _responsiveChecks);

    public Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
    {
        return Task.FromResult(Ready);
    }

    public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
    {
        Interlocked.Increment(ref _responsiveChecks);
        return Task.FromResult(Responsive);
    }
}

/// <summary>Backend selector returning a fixed backend; never probes hardware.</summary>
internal sealed class FakeSdBackendSelector(SdGpuBackend backend = SdGpuBackend.Cpu) : ISdGpuBackendSelector
{
    public Task<SdGpuBackend> SelectBackendAsync(CancellationToken ct)
    {
        return Task.FromResult(backend);
    }
}

/// <summary>Binary manager returning a fixed fake sd-server path for whatever backend is requested; never downloads.</summary>
internal sealed class FakeSdBinaryManager(SdGpuBackend resolvedBackend = SdGpuBackend.Cpu) : IStableDiffusionBinaryManager
{
    public Task<SdBinary> EnsureBinaryAsync(SdGpuBackend backend, CancellationToken ct)
    {
        return Task.FromResult(new SdBinary("/fake/bin/sd-server", "master-742-1a13107", resolvedBackend, IsPinnedFallback: true));
    }
}

/// <summary>
///     Image model store fake: resolves a model name to a single-file (SD1.5-shaped) diffusion part so the argument
///     builder produces a valid spec. The download/delete surface is not exercised by the supervisor tests.
/// </summary>
internal sealed class FakeImageModelStore(string? diffusionPath = "/fake/models/sd15.safetensors") : IImageModelStore
{
    public Task<IReadOnlyList<ImageModelPart>?> ResolveModelPartsAsync(string modelName, CancellationToken ct)
    {
        if (diffusionPath is null)
        {
            return Task.FromResult<IReadOnlyList<ImageModelPart>?>(null);
        }

        IReadOnlyList<ImageModelPart> parts =
        [
            new ImageModelPart
            {
                Role = ImageModelPartRole.Diffusion,
                FileName = Path.GetFileName(diffusionPath),
                LocalPath = diffusionPath,
                SizeBytes = 1024
            }
        ];

        return Task.FromResult<IReadOnlyList<ImageModelPart>?>(parts);
    }

    public Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([]);
    }

    public Task<ImageModelHandle> EnsureModelAsync(ImageModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
    {
        throw new NotSupportedException("FakeImageModelStore does not download.");
    }

    public Task DeleteModelAsync(string modelName, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string modelName, CancellationToken ct)
    {
        return Task.FromResult(diffusionPath is not null);
    }
}

/// <summary>
///     Time provider whose <see cref="GetUtcNow" /> is advanceable (drives idle-TTL/LRU + liveness-interval comparisons
///     deterministically) while timer creation falls through to the real provider so <c>Task.Delay</c> still completes.
/// </summary>
internal sealed class AdvanceableClock : TimeProvider
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

/// <summary>Builds an <see cref="ImageServerProcessSupervisor" /> over fakes with sensible test defaults.</summary>
internal static class ImageSupervisorFactory
{
    public static ImageServerProcessSupervisor Create(FakeImageProcessLauncher? launcher = null,
        FakeImageReadinessProbe? readinessProbe = null,
        FakeImageModelStore? modelStore = null,
        StableDiffusionRuntimeOptions? options = null,
        AdvanceableClock? timeProvider = null,
        FakeSdBackendSelector? backendSelector = null,
        FakeSdBinaryManager? binaryManager = null,
        IGpuModelLoadAdmission? loadAdmission = null)
    {
        return new ImageServerProcessSupervisor(modelStore ?? new FakeImageModelStore(),
            backendSelector ?? new FakeSdBackendSelector(),
            binaryManager ?? new FakeSdBinaryManager(),
            launcher ?? new FakeImageProcessLauncher(),
            readinessProbe ?? new FakeImageReadinessProbe(),
            options ?? new StableDiffusionRuntimeOptions
            {
                // A long TTL keeps the background reaper out of the way; tests drive eviction explicitly.
                IdleTimeToLive = TimeSpan.FromHours(1),
                MaxLoadedProcesses = 2
            },
            timeProvider ?? new AdvanceableClock(),
            loadAdmission: loadAdmission);
    }
}
