namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Collections.Concurrent;
using System.Net;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;

/// <summary>
///     Shared process-boundary fakes for the <see cref="WhisperServerProcessSupervisor" /> tests: a launcher that
///     records launch specs and hands back controllable in-memory handles (no real <c>whisper-server</c>), a readiness
///     probe with toggleable readiness and liveness driven by gates the test owns, a fixed backend selector and binary
///     manager, and a recording GPU-load admission.
/// </summary>
/// <remarks>
///     Nothing here is copied out of the stable-diffusion.cpp doubles file — that file is read-only for this slice.
///     The deterministic clock is the repo's own <see cref="XE_Local_AI_Engine.Tests.Testing.ManualTimeProvider" />,
///     which is the only double in this repository that also overrides timer creation; the supervisor's reaper loop is
///     a delay over its injected provider, so a now-only fake would leave it waiting in wall-clock time and turn the
///     test into a sleep in disguise.
/// </remarks>
internal sealed class FakeWhisperProcessLauncher : IWhisperServerProcessLauncher
{
    private int _nextPid = 3000;

    public ConcurrentQueue<WhisperServerLaunchSpec> Launches { get; } = new();

    public ConcurrentBag<FakeWhisperProcessHandle> Handles { get; } = new();

    public int LaunchCount => Launches.Count;

    public IWhisperServerProcessHandle Launch(WhisperServerLaunchSpec spec)
    {
        Launches.Enqueue(spec);
#pragma warning disable CA2000 // Ownership transfers to the supervisor under test, which disposes it on teardown.
        var handle = new FakeWhisperProcessHandle(Interlocked.Increment(ref _nextPid));
#pragma warning restore CA2000
        Handles.Add(handle);
        return handle;
    }
}

/// <summary>An in-memory handle whose exit and tree-kill are directly controllable by the test.</summary>
internal sealed class FakeWhisperProcessHandle : IWhisperServerProcessHandle
{
    private int _exited;
    private int _killed;

    public FakeWhisperProcessHandle(int pid)
    {
        ProcessId = pid;
    }

    public bool WasTreeKilled => Volatile.Read(ref _killed) != 0;

    public int ProcessId { get; }

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

    /// <summary>Simulates a crash or exit, so the next ensure sees a dead process.</summary>
    public void SimulateExit() =>
        Interlocked.Exchange(ref _exited, value: 1);
}

/// <summary>
///     Readiness probe with controllable readiness and liveness. Readiness can also be parked on a gate the test
///     completes, which is how "the port opens after a while" is expressed without a single sleep.
/// </summary>
internal sealed class FakeWhisperReadinessProbe : IWhisperServerReadinessProbe
{
    private int _responsiveChecks;
    private int _readinessWaits;

    public FakeWhisperReadinessProbe(bool ready = true, bool responsive = true)
    {
        Ready = ready;
        Responsive = responsive;
    }

    public bool Ready { get; set; }

    public bool Responsive { get; set; }

    /// <summary>When set, readiness parks on this gate and resolves to whatever it completes with.</summary>
    public TaskCompletionSource<bool>? ReadinessGate { get; set; }

    /// <summary>Signalled once readiness has actually been awaited, so a test never races the code under test.</summary>
    public TaskCompletionSource ReadinessReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Count of reuse-path liveness probes issued — asserts the hot path did or did not probe.</summary>
    public int ResponsiveChecks => Volatile.Read(ref _responsiveChecks);

    /// <summary>Count of readiness waits — asserts a respawn or a model switch actually re-gated on readiness.</summary>
    public int ReadinessWaits => Volatile.Read(ref _readinessWaits);

    public async Task<bool> WaitForReadyAsync(Uri baseAddress, TimeSpan readinessTimeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _readinessWaits);
        ReadinessReached.TrySetResult();

        if (ReadinessGate is { } gate)
        {
            return await gate.Task.WaitAsync(ct);
        }

        return Ready;
    }

    public Task<bool> CheckResponsiveAsync(Uri baseAddress, CancellationToken ct)
    {
        Interlocked.Increment(ref _responsiveChecks);
        return Task.FromResult(Responsive);
    }
}

/// <summary>Backend selector returning a fixed backend; never probes hardware.</summary>
internal sealed class FakeWhisperBackendSelector : IWhisperBackendSelector
{
    private readonly WhisperBackend _backend;

    public FakeWhisperBackendSelector(WhisperBackend backend = WhisperBackend.Cpu)
    {
        _backend = backend;
    }

    public Task<WhisperBackend> SelectBackendAsync(CancellationToken ct) =>
        Task.FromResult(_backend);
}

/// <summary>Binary manager returning a fixed fake server path for whatever backend is requested; never downloads.</summary>
internal sealed class FakeWhisperBinaryManager : IWhisperCppBinaryManager
{
    private readonly WhisperBackend _resolvedBackend;
    private readonly bool _isPinnedFallback;
    private readonly string _version;

    public FakeWhisperBinaryManager(
        WhisperBackend resolvedBackend = WhisperBackend.Cpu,
        bool isPinnedFallback = true,
        string version = "b5130")
    {
        _resolvedBackend = resolvedBackend;
        _isPinnedFallback = isPinnedFallback;
        _version = version;
    }

    public Task<WhisperBinary> EnsureBinaryAsync(WhisperBackend backend, CancellationToken ct) =>
        Task.FromResult(new WhisperBinary("/fake/bin/whisper-server", _version, _resolvedBackend, _isPinnedFallback));
}

/// <summary>
///     Recording GPU-load admission: counts acquisitions and releases and records their order, so a test can assert
///     both that the gate was taken and that the ticket was released exactly once on every outcome.
/// </summary>
internal sealed class RecordingGpuLoadAdmission : IGpuModelLoadAdmission
{
    private int _acquired;
    private int _released;

    public int AcquireCount => Volatile.Read(ref _acquired);

    public int ReleaseCount => Volatile.Read(ref _released);

    /// <summary>True while a ticket is outstanding — asserts the gate is not held past readiness or failure.</summary>
    public bool IsHeld => AcquireCount > ReleaseCount;

    public Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _acquired);
        return Task.FromResult<IDisposable>(new Ticket(this));
    }

    private sealed class Ticket : IDisposable
    {
        private readonly RecordingGpuLoadAdmission _owner;
        private int _disposed;

        public Ticket(RecordingGpuLoadAdmission owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, value: 1) == 0)
            {
                Interlocked.Increment(ref _owner._released);
            }
        }
    }
}

/// <summary>An admission gate that must never be touched; taking it fails the test that wired it.</summary>
internal sealed class ForbiddenGpuLoadAdmission : IGpuModelLoadAdmission
{
    public Task<IDisposable> AcquireAsync(CancellationToken ct) =>
        throw new InvalidOperationException("A CPU backend must never acquire the GPU load-admission gate.");
}

/// <summary>Scripted HTTP for the supervisor's in-place model-switch route.</summary>
internal sealed class ScriptedWhisperHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    private int _callCount;

    public ScriptedWhisperHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    public int CallCount => Volatile.Read(ref _callCount);

    public string? LastPath { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastPath = request.RequestUri?.AbsolutePath;
        Interlocked.Increment(ref _callCount);
        return Task.FromResult(_responder(request));
    }
}

/// <summary>Builds a supervisor over fakes with test defaults, and keeps every collaborator reachable afterwards.</summary>
internal sealed class WhisperSupervisorHarness : IAsyncDisposable
{
    private readonly HttpClient _httpClient;

    public WhisperSupervisorHarness(FakeWhisperProcessLauncher? launcher = null,
        FakeWhisperReadinessProbe? readinessProbe = null,
        WhisperRuntimeOptions? options = null,
        TimeProvider? timeProvider = null,
        FakeWhisperBackendSelector? backendSelector = null,
        FakeWhisperBinaryManager? binaryManager = null,
        IGpuModelLoadAdmission? loadAdmission = null,
        ScriptedWhisperHttpHandler? httpHandler = null,
        string? modelsDirectory = null)
    {
        Launcher = launcher ?? new FakeWhisperProcessLauncher();
        ReadinessProbe = readinessProbe ?? new FakeWhisperReadinessProbe();
        BackendSelector = backendSelector ?? new FakeWhisperBackendSelector();
        BinaryManager = binaryManager ?? new FakeWhisperBinaryManager();
        HttpHandler = httpHandler ?? new ScriptedWhisperHttpHandler(static _ => new HttpResponseMessage(HttpStatusCode.OK));
        ActivityGate = new WhisperRuntimeActivityGate();
        ModelsDirectory = modelsDirectory ?? CreateModelsDirectory();

        Options = options ?? new WhisperRuntimeOptions
        {
            // A long TTL keeps the background reaper out of the way; tests that want eviction drive the clock.
            IdleTimeToLive = TimeSpan.FromHours(1)
        };
        Options.ModelsDirectory = ModelsDirectory;

        _httpClient = new HttpClient(HttpHandler, disposeHandler: false);

        Supervisor = new WhisperServerProcessSupervisor(BackendSelector,
            BinaryManager,
            Launcher,
            ReadinessProbe,
            _httpClient,
            Options,
            timeProvider ?? TimeProvider.System,
            logger: null,
            loadAdmission,
            ActivityGate);
    }

    public WhisperServerProcessSupervisor Supervisor { get; }

    public FakeWhisperProcessLauncher Launcher { get; }

    public FakeWhisperReadinessProbe ReadinessProbe { get; }

    public FakeWhisperBackendSelector BackendSelector { get; }

    public FakeWhisperBinaryManager BinaryManager { get; }

    public ScriptedWhisperHttpHandler HttpHandler { get; }

    public WhisperRuntimeActivityGate ActivityGate { get; }

    public WhisperRuntimeOptions Options { get; }

    public string ModelsDirectory { get; }

    public async ValueTask DisposeAsync()
    {
        await Supervisor.DisposeAsync();
        _httpClient.Dispose();
        HttpHandler.Dispose();

        try
        {
            if (Directory.Exists(ModelsDirectory))
            {
                Directory.Delete(ModelsDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    /// <summary>
    ///     Materializes an empty weight file for every catalogue row, because the supervisor refuses to spawn for a
    ///     model it cannot find on disk — which is the honest behaviour, and means the fakes need a real directory.
    /// </summary>
    private static string CreateModelsDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "xe-whisper-models-" + Guid.NewGuid().ToString("N"));
        foreach (var entry in WhisperModelCatalog.Models)
        {
            var path = Path.Combine(root, WhisperModelCatalog.RelativeFilePath(entry));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fake-weights");
        }

        return root;
    }
}
