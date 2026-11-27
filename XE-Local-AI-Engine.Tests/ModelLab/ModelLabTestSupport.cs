namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Training.BaseArtifacts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The Model Lab job host on the node's real DI and its scripted doubles, shared by the Model Lab test classes. Only the process,
///     the runtime's install state, the card and the GGUF header are scripted.
/// </summary>
internal static class ModelLabTestSupport
{
    internal const string Gguf = "org/Some-GGUF:Q4_K_M";
    internal const long Card = 32_607L * 1024 * 1024;
    internal const long WeightBytes = 1_746_942_600;
    internal const string Prompt = "the secret prompt under test";

    internal static readonly string Handshake = $"{{\"event\":\"handshake\",\"contractVersion\":{ModelLabTraceSpec.ContractVersion}}}";

    internal static readonly string[] CompleteRun =
    [
        Handshake,
        """{"event":"phase","phase":"tracing"}""",
        """{"event":"progress","step":1,"totalSteps":128,"vramBytes":1000}""",
        """{"event":"done","cancelled":false}"""
    ];

    internal static void WrapStore(IServiceCollection services, Func<IModelLabExperimentStore, IModelLabExperimentStore> wrap)
    {
        services.RemoveAll<IModelLabExperimentStore>();
        services.AddScoped(provider => wrap(ActivatorUtilities.CreateInstance<ModelLabExperimentStore>(provider)));
    }

    /// <summary>The real store, failing only where the test says, so a commit that happened really happened.</summary>
    internal sealed class FaultingStore : IModelLabExperimentStore
    {
        private readonly IModelLabExperimentStore _inner;
        private Guid _created;

        public FaultingStore(IModelLabExperimentStore inner)
        {
            _inner = inner;
        }

        public Action? BeforeCreate { get; init; }

        public Func<Guid, Task>? BeforeTrim { get; init; }

        public Action? AfterCreate { get; init; }

        public Exception? TrimFailure { get; init; }

        public Exception? CompleteFailure { get; init; }

        public Action? BeforeProgress { get; init; }

        public Action<ModelLabExperimentCompletion>? OnComplete { get; init; }

        public async Task CreateRunningAsync(ModelLabExperimentCreate command, CancellationToken cancellationToken = default)
        {
            BeforeCreate?.Invoke();
            await _inner.CreateRunningAsync(command, cancellationToken);
            _created = command.Id;
            AfterCreate?.Invoke();
        }

        public async Task<IReadOnlyList<Guid>> TrimToNewestAsync(int keep, CancellationToken cancellationToken = default)
        {
            if (BeforeTrim is not null)
            {
                await BeforeTrim(_created);
            }

            return TrimFailure is null ? await _inner.TrimToNewestAsync(keep, cancellationToken) : throw TrimFailure;
        }

        public Task<ModelLabExperimentRecord?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.GetAsync(id, cancellationToken);

        public Task<ModelLabExperimentUpdateOutcome> SetNotesAsync(Guid id, string? notes, CancellationToken cancellationToken = default) =>
            _inner.SetNotesAsync(id, notes, cancellationToken);

        public Task<ModelLabExperimentUpdateOutcome> SetPinnedAsync(Guid id, bool pinned, int maxPinned, CancellationToken cancellationToken = default) =>
            _inner.SetPinnedAsync(id, pinned, maxPinned, cancellationToken);

        public Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.ExistsAsync(id, cancellationToken);

        public Task<IReadOnlyList<ModelLabExperimentRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            _inner.ListAsync(cancellationToken);

        public Task SetLaunchReceiptAsync(Guid id, ReadOnlyMemory<byte>? launchReceiptJson, CancellationToken cancellationToken = default) =>
            _inner.SetLaunchReceiptAsync(id, launchReceiptJson, cancellationToken);

        public Task SetProgressAsync(Guid id, ReadOnlyMemory<byte> resultJson, ModelLabBlobReference? blob, CancellationToken cancellationToken = default)
        {
            BeforeProgress?.Invoke();
            return _inner.SetProgressAsync(id, resultJson, blob, cancellationToken);
        }

        public Task CompleteAsync(Guid id, ModelLabExperimentCompletion completion, CancellationToken cancellationToken = default)
        {
            OnComplete?.Invoke(completion);
            return CompleteFailure is null ? _inner.CompleteAsync(id, completion, cancellationToken) : throw CompleteFailure;
        }

        public Task<ModelLabExperimentDeleteOutcome> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(id, cancellationToken);

        public Task<IReadOnlyList<ModelLabLaunchReceipt>> ListLaunchReceiptsAsync(CancellationToken cancellationToken = default) =>
            _inner.ListLaunchReceiptsAsync(cancellationToken);

        public Task<IReadOnlyList<Guid>> RecoverOnStartupAsync(CancellationToken cancellationToken = default) =>
            _inner.RecoverOnStartupAsync(cancellationToken);
    }

    /// <summary>The real checkpoint store, whose row delete waits until the test lets it go, or fails.</summary>
    internal sealed class StallingArtifactStore : ITrainingBaseArtifactStore
    {
        private readonly ITrainingBaseArtifactStore _inner;
        private readonly Stall _stall;

        private StallingArtifactStore(ITrainingBaseArtifactStore inner, Stall stall)
        {
            _inner = inner;
            _stall = stall;
        }

        public static void Install(IServiceCollection services, Stall stall)
        {
            services.RemoveAll<ITrainingBaseArtifactStore>();
            services.AddScoped<ITrainingBaseArtifactStore>(provider => new StallingArtifactStore(ActivatorUtilities.CreateInstance<TrainingBaseArtifactStore>(provider), stall));
        }

        public async Task<bool> DeleteAsync(Guid artifactId, long expectedVersion, CancellationToken cancellationToken = default)
        {
            _stall.Entered.TrySetResult();
            await _stall.MayProceed.Task.WaitAsync(TestBudgets.Contended, cancellationToken);
            return _stall.Failure is null ? await _inner.DeleteAsync(artifactId, expectedVersion, cancellationToken) : throw _stall.Failure;
        }

        public Task<TrainingBaseArtifactRecord> StartDownloadAsync(string repoId, string revision, CancellationToken cancellationToken = default) =>
            _inner.StartDownloadAsync(repoId, revision, cancellationToken);

        public Task<TrainingBaseArtifactRecord?> GetAsync(Guid artifactId, CancellationToken cancellationToken = default) =>
            _inner.GetAsync(artifactId, cancellationToken);

        public Task<IReadOnlyList<TrainingBaseArtifactRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            _inner.ListAsync(cancellationToken);

        public Task<TrainingBaseArtifactRecord> MarkReadyAsync(Guid artifactId,
            long expectedVersion,
            byte[] filesJson,
            long totalBytes,
            byte[]? licenseJson,
            CancellationToken cancellationToken = default) =>
            _inner.MarkReadyAsync(artifactId, expectedVersion, filesJson, totalBytes, licenseJson, cancellationToken);

        public Task<TrainingBaseArtifactRecord> MarkFailedAsync(Guid artifactId, long expectedVersion, string errorMessage, CancellationToken cancellationToken = default) =>
            _inner.MarkFailedAsync(artifactId, expectedVersion, errorMessage, cancellationToken);

        public Task<TrainingBaseArtifactRecord> SetRevisionAsync(Guid artifactId, long expectedVersion, string revision, CancellationToken cancellationToken = default) =>
            _inner.SetRevisionAsync(artifactId, expectedVersion, revision, cancellationToken);

        public Task<int> RecoverOnStartupAsync(CancellationToken cancellationToken = default) =>
            _inner.RecoverOnStartupAsync(cancellationToken);

        /// <summary>Where a row delete stops, and what it does once let go.</summary>
        internal sealed class Stall
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource MayProceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Exception? Failure { get; init; }
        }
    }

    /// <summary>The real gate, with a step just before it is taken (the last moment a delete can pass the guard) and one as an exclusive hold goes.</summary>
    internal sealed class GateWithHook : IGpuWorkGate
    {
        private readonly IGpuWorkGate _inner;
        private int _exclusiveAttempts;

        public GateWithHook(IGpuWorkGate inner)
        {
            _inner = inner;
        }

        public Func<Task>? BeforeBegin { get; set; }

        /// <summary>Runs just before an exclusive hold is given back, while the gate still shows it.</summary>
        public Action? BeforeExclusiveRelease { get; set; }

        public int ExclusiveAttempts => Volatile.Read(ref _exclusiveAttempts);

        public GpuWorkKind? ExclusiveKind => _inner.ExclusiveKind;

        public IReadOnlyList<GpuWorkKind> Holders => _inner.Holders;

        public static void Install(IServiceCollection services)
        {
            services.RemoveAll<IGpuWorkGate>();
            services.AddSingleton<IGpuWorkGate>(static provider => new GateWithHook(ActivatorUtilities.CreateInstance<GpuWorkGate>(provider)));
        }

        public bool TryBeginExclusive(GpuWorkKind kind, [NotNullWhen(true)] out IDisposable? handle)
        {
            Interlocked.Increment(ref _exclusiveAttempts);
            BeforeBegin?.Invoke().GetAwaiter().GetResult();
            if (!_inner.TryBeginExclusive(kind, out var inner))
            {
                handle = null;
                return false;
            }

            handle = new Hold(inner, this);
            return true;
        }

        public bool TryBeginShared(GpuWorkKind kind, [NotNullWhen(true)] out IDisposable? handle) =>
            _inner.TryBeginShared(kind, out handle);

        private sealed class Hold : IDisposable
        {
            private readonly IDisposable _inner;
            private readonly GateWithHook _gate;
            private int _disposed;

            public Hold(IDisposable inner, GateWithHook gate)
            {
                _inner = inner;
                _gate = gate;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _gate.BeforeExclusiveRelease?.Invoke();
                    _inner.Dispose();
                }
            }
        }
    }

    /// <summary>OLMoE's shape at the test GGUF's embedding width: 16 blocks of 16 heads, no <c>layer_types</c> unless <paramref name="extra" /> adds one.</summary>
    internal static string OlmoeConfig(string extra) =>
        $$"""{"model_type":"olmoe","hidden_size":1024,"num_hidden_layers":16,"num_attention_heads":16{{extra}}}""";

    /// <summary>A real test host, with one artifact Ready and an installed GGUF that pairs with it.</summary>
    internal sealed class LabHost : IAsyncDisposable
    {
        public const string Config =
            """{"model_type":"qwen3_5","text_config":{"hidden_size":1024,"num_hidden_layers":24,"num_attention_heads":8,"layer_types":["linear_attention","linear_attention","linear_attention","full_attention","linear_attention","linear_attention","linear_attention","full_attention","linear_attention","linear_attention","linear_attention","full_attention","linear_attention","linear_attention","linear_attention","full_attention","linear_attention","linear_attention","linear_attention","full_attention","linear_attention","linear_attention","linear_attention","full_attention"]}}""";

        private readonly TestServerWebAppFactory _factory;
        private readonly Guid _artifactId;

        private LabHost(TestServerWebAppFactory factory, Guid artifactId, ITrainingRuntimeService runtime, IBaseCheckpointStore checkpoints)
        {
            _factory = factory;
            _artifactId = artifactId;
            Runtime = runtime;
            Checkpoints = checkpoints;
        }

        public ITrainingRuntimeService Runtime { get; }

        public IBaseCheckpointStore Checkpoints { get; }

        public TestServerWebAppFactory Factory => _factory;

        public IServiceProvider Services => _factory.Services;

        public ModelLabExperimentService Service => Services.GetRequiredService<ModelLabExperimentService>();

        public ModelLabExperimentReader Reader => Services.GetRequiredService<ModelLabExperimentReader>();

        public IGpuWorkGate Gate => Services.GetRequiredService<IGpuWorkGate>();

        public IPendingFootprintLedger Ledger => Services.GetRequiredService<IPendingFootprintLedger>();

        public ILlamaServerProcessSupervisor Supervisor => Services.GetRequiredService<ILlamaServerProcessSupervisor>();

        public string DataRoot => Services.GetRequiredService<INodeDataDirectory>().Root;

        public string WorkDirectory(Guid id) =>
            Path.Combine(ModelLabScriptRunner.WorkRoot(Services.GetRequiredService<INodeDataDirectory>()), id.ToString("N"));

        /// <param name="warmIdleSeconds">The node's warm time; 0, the default here, makes every job one-shot as before warming existed.</param>
        public static async Task<LabHost> StartAsync(ITrainingProcessSpawner process,
            ModelLabJobOptions? options = null,
            ILlamaServerProcessSupervisor? supervisor = null,
            long availableVramBytes = Card,
            Action<IServiceCollection>? configure = null,
            string config = Config,
            int ggufBlockCount = 24,
            int warmIdleSeconds = 0,
            string ggufArchitecture = "qwen35")
        {
            var interpreter = Path.Combine(Path.GetTempPath(), "python-" + Guid.NewGuid().ToString("N"));
            var runtime = Substitute.For<ITrainingRuntimeService>();
            runtime.ResolveInterpreterPath().Returns(interpreter);
            runtime.GetStatus().Returns(new TrainingRuntimeStatus
            {
                Phase = TrainingRuntimePhase.Ready,
                IsRunning = false,
                Terminal = true,
                LogLines = [],
                LogStartSequence = 0,
                SanitizedError = null,
                Installed = null,
                StartedAtUtc = null,
                CompletedAtUtc = null
            });

            var audit = Substitute.For<IRuntimeDeviceAudit>();
            audit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new HardwareProfile
            {
                TotalRamBytes = long.MaxValue / 4,
                AvailableRamBytes = long.MaxValue / 4,
                VramBytes = Card,
                AvailableVramBytes = availableVramBytes,
                VramKnown = true,
                GpuVendor = GpuVendor.Nvidia,
                GpuAccelAvailable = true,
                CpuCores = 8,
                FreeDiskBytes = long.MaxValue / 4
            });

            var ggufStore = Substitute.For<IGgufModelStore>();
            ggufStore.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
            ggufStore.ResolveModelFilePathAsync(Gguf, Arg.Any<CancellationToken>()).Returns("/models/some.gguf");
            var ggufReader = Substitute.For<IGgufMetadataReader>();
            ggufReader.ReadTensorsAsync("/models/some.gguf", Arg.Any<CancellationToken>()).Returns(new GgufTensorInspection
            {
                Failure = GgufTensorReadFailure.None,
                Version = 3,
                Architecture = ggufArchitecture,
                ModelName = null,
                ParamCount = null,
                FileType = null,
                BlockCount = ggufBlockCount,
                NextnPredictLayers = null,
                Alignment = null,
                AttentionHeadCount = null,
                AttentionHeadCountKv = null,
                EmbeddingLength = 1024,
                ContextLength = null,
                ExpertCount = null,
                ChatTemplate = null,
                SplitCount = null,
                TensorCount = 0,
                Tensors = [],
                HeaderBytes = 0,
                FileSizeBytes = 0
            });

            var checkpoints = Substitute.For<IBaseCheckpointStore>();
            // The download preflight asks the real disk for checkpoint size plus headroom; a busy temp volume must not decide a test.
            var freeSpace = Substitute.For<IFreeSpaceProbe>();
            freeSpace.GetAvailableFreeBytes(Arg.Any<string>()).Returns(long.MaxValue);
#pragma warning disable CA2000 // LabHost owns it.
            var factory = new TestServerWebAppFactory
            {
                ConfigureAdditionalTestServices = services =>
                {
                    services.RemoveAll<ITrainingProcessSpawner>();
                    services.AddSingleton<ITrainingProcessSpawner>(process);
                    services.RemoveAll<ITrainingRuntimeService>();
                    services.AddSingleton(runtime);
                    services.RemoveAll<IRuntimeDeviceAudit>();
                    services.AddSingleton(audit);
                    services.RemoveAll<IGgufModelStore>();
                    services.AddSingleton(ggufStore);
                    services.RemoveAll<IGgufMetadataReader>();
                    services.AddSingleton(ggufReader);
                    services.RemoveAll<IBaseCheckpointStore>();
                    services.AddSingleton(checkpoints);
                    services.RemoveAll<IFreeSpaceProbe>();
                    services.AddSingleton(freeSpace);
                    if (options is not null)
                    {
                        services.RemoveAll<IOptions<ModelLabJobOptions>>();
                        services.AddSingleton(Options.Create(options));
                    }

                    if (supervisor is not null)
                    {
                        services.RemoveAll<ILlamaServerProcessSupervisor>();
                        services.AddSingleton(supervisor);
                    }

                    configure?.Invoke(services);
                }
            };
#pragma warning restore CA2000
            _ = factory.Server;

            _ = await factory.Services.GetRequiredService<INodeSettingsStore>().UpdateAsync(settings => settings with
            {
                ModelLabWarmIdleSeconds = warmIdleSeconds
            });
            var artifactId = await SeedReadyArtifactAsync(factory.Services, config);
            return new LabHost(factory, artifactId, runtime, checkpoints);
        }

        public ModelLabExperimentRequest Request() =>
            new()
            {
                BaseArtifactId = _artifactId,
                InstalledModelName = Gguf,
                Prompt = Prompt,
                LicenseConfirmed = true
            };

        public HttpRequestMessage Authorized(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, path);
            _factory.AddNodeBearerToken(request);
            request.Headers.Add("Origin", "http://localhost");
            return request;
        }

        public async Task SeedRunningAsync(Guid id)
        {
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().CreateRunningAsync(new ModelLabExperimentCreate
            {
                Id = id,
                RepoId = "Qwen/Qwen3.5-0.8B",
                Revision = "rev",
                BaseArtifactId = _artifactId,
                InstalledModelName = Gguf,
                Prompt = "seed",
                LicenseConfirmationJson = new byte[]
                {
                    1
                }
            });
        }

        public async Task SeedFinishedAsync(Guid id)
        {
            await SeedRunningAsync(id);
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().CompleteAsync(id, new ModelLabExperimentCompletion
            {
                Status = ModelLabExperimentStatus.Failed,
                ErrorMessage = "seed"
            });
        }

        public async Task<Guid> SeedDownloadingArtifactAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            var record = await scope.ServiceProvider.GetRequiredService<ITrainingBaseArtifactStore>().StartDownloadAsync("Qwen/Qwen3.5-9B", "rev");
            return record.Id;
        }

        /// <summary>The test factory runs no hosted service, so the stop the host would make is made here: an idle worker's lease would hold up teardown for its warm time.</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Service.StopAsync(CancellationToken.None);
            }
            finally
            {
                await _factory.DisposeAsync();
            }
        }

        /// <summary>A second Ready checkpoint: the same model at another revision, so it pairs with the same GGUF.</summary>
        public Task<Guid> SeedOtherReadyArtifactAsync() =>
            SeedReadyArtifactAsync(Services, Config, "0000000000000000000000000000000000000002");

        private static async Task<Guid> SeedReadyArtifactAsync(IServiceProvider services, string config, string revision = "2fc06364715b967f1860aea9cf38778875588b17")
        {
            await using var scope = services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ITrainingBaseArtifactStore>();
            var record = await store.StartDownloadAsync("Qwen/Qwen3.5-0.8B", revision);
            var directory = Path.Combine(services.GetRequiredService<INodeDataDirectory>().Root, "training", "base", record.Id.ToString());
            Directory.CreateDirectory(directory);
            var configPath = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(configPath, config);
            var files = JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new BaseArtifactFileView("Config", "config.json", configPath, 100, null),
                new BaseArtifactFileView("Weights", "model.safetensors", Path.Combine(directory, "model.safetensors"), WeightBytes, null)
            });
            var license = JsonSerializer.SerializeToUtf8Bytes(new BaseArtifactLicenseView("Qwen/Qwen3.5-0.8B", "apache-2.0", IsGated: false, DateTimeOffset.UnixEpoch));
            _ = await store.MarkReadyAsync(record.Id, record.Version, files, WeightBytes + 100, license);
            return record.Id;
        }
    }

    /// <summary>A scripted <c>modellab.py</c>: it stays running until the test finishes it, and writes the files the job file names.</summary>
    /// <remarks>
    ///     Spawned with <c>--serve</c> it speaks this build's contract on its own: the handshake at spawn, every protocol line stamped with the spawn's
    ///     token and the job's id, a <c>ready</c> after a terminal frame, a cancelled <c>done</c> on a cancel and an exit on an evict.
    /// </remarks>
    internal sealed class ScriptedProcess : ITrainingProcessSpawner, ITrainingProcessHandle
    {
        private static readonly string[] JobEvents = ["phase", "progress", "heartbeat", "done", "error"];

        private readonly TaskCompletionSource _spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly Channel<RunCommand> _runs = Channel.CreateUnbounded<RunCommand>();
        private volatile RunCommand? _current;

        public string TraceContent { get; init; } = "{\"footer\":true,\"truncated\":false,\"records\":0,\"dropped\":0,\"payloadBytes\":0,\"cap\":262144}\n";

        /// <summary>Written when the job names an attention file; null leaves it unwritten.</summary>
        public string? AttentionContent { get; init; } = "{\"footer\":true,\"truncated\":false,\"records\":0,\"dropped\":0,\"payloadBytes\":0,\"cap\":262144}\n";

        public string TraceResult { get; init; } = """{"promptTokens":3,"tokens":[{"id":9,"text":"hi"}],"text":"hi","seconds":0.5,"peakVramBytes":10,"blockCount":24}""";

        public byte[] VectorContent { get; init; } = "the-steering-vector-bytes"u8.ToArray();

        public byte[] RandomVectorContent { get; init; } = "the-random-vector-bytes"u8.ToArray();

        public string DerivedResult { get; init; } =
            """{"seconds":1.5,"peakVramBytes":10,"blockCount":24,"steering":{"layer":15,"layerKind":"F","referenceNorm":3.9,"differenceNorm":0.8,"vectorNorm":1.95,"randomSeed":1234,"loadSeconds":2.5}}""";

        /// <summary>Ignores a SIGTERM, a cancel and an evict: only a kill ends it.</summary>
        public bool IgnoresStop { get; init; }

        /// <summary>False for a process a kill does not end either, whose exit is never seen.</summary>
        public bool ExitsOnKill { get; init; } = true;

        /// <summary>Set: an evict exits only once this completes, so a test can catch the session while it is evicting.</summary>
        public TaskCompletionSource? HoldExitOnEvict { get; init; }

        public volatile bool EvictRequested;

        /// <summary>The handshake a serving spawn says first; null for the stamped contract-6 one.</summary>
        public string? ServeHandshake { get; init; }

        /// <summary>A clean job ends without its <c>ready</c>, as a worker stuck releasing memory would.</summary>
        public bool WithholdsReady { get; init; }

        /// <summary>A clean job's process exits right after its <c>ready</c> line, before any evict.</summary>
        public bool ExitsAfterReady { get; init; }

        /// <summary>Runs as the <c>run</c> command arrives, once the job is current, so a test can say lines straight after it.</summary>
        public Action<ScriptedProcess>? OnRun { get; init; }

        /// <summary>Throws from the spawn, as a spawner that cannot start the process does.</summary>
        public bool FailsToSpawn { get; init; }

        /// <summary>
        ///     An exit leaves the output open, as a descendant holding the pipe would; <see cref="WaitForExitAsync" /> then waits for
        ///     that pipe too, as <c>Process.WaitForExitAsync</c> does, while <see cref="WaitForGroupExitAsync" /> does not.
        /// </summary>
        public bool OutputOutlivesExit { get; init; }

        /// <summary>
        ///     After the exit the output never runs dry, as a descendant writing without pause would: the reader keeps yielding lines
        ///     whatever its token says, as <c>ChannelReader.ReadAllAsync</c> does while its buffer holds lines.
        /// </summary>
        public bool OutputFloodsAfterExit { get; init; }

        /// <summary>The reader of the output stopped: the stream ended, or its reader was cancelled.</summary>
        public volatile bool OutputReadEnded;

        /// <summary>The script has a job: the spawn, or under <c>--serve</c> the first <c>run</c> command.</summary>
        public Task Spawned => _spawned.Task;

        public TrainingSpawnRequest? Request { get; private set; }

        public bool IsServing => Request?.Arguments.Contains("--serve") == true;

        /// <summary>The current job's id as its frames carry it, or null before the first <c>run</c>.</summary>
        public string? JobId => _current?.JobId;

        public volatile bool StopRequested;

        public volatile bool Killed;

        public TrainingLaunchReceipt Receipt { get; init; } = new()
        {
            Pid = 4242,
            Pgid = 4242,
            ExecutablePath = "/venv/bin/python",
            StartTicks = 1,
            RunToken = "token"
        };

        public ConcurrentQueue<string> WrittenLines { get; } = new();

        public bool HasExited => _exit.Task.IsCompleted;

        public ITrainingProcessHandle Spawn(TrainingSpawnRequest request)
        {
            if (Request is not null)
            {
                throw new InvalidOperationException("A scripted process is spawned once; use a ScriptedSpawner for more.");
            }

            Request = request;
            if (FailsToSpawn)
            {
                throw new InvalidOperationException("The scripted spawn failed.");
            }

            if (IsServing)
            {
                Say(ServeHandshake ?? $"{{\"event\":\"handshake\",\"contractVersion\":{ModelLabTraceSpec.ContractVersion},\"serve\":true}}");
            }
            else
            {
                _spawned.TrySetResult();
            }

            return this;
        }

        /// <summary>The current job's file: the last <c>run</c> command's, or the one-shot spawn's.</summary>
        public string? JobFilePath => _current?.Config ?? (IsServing ? null : Request?.Arguments[2]);

        public string ReadJobFile() =>
            File.ReadAllText(JobFilePath!);

        /// <summary>Says a line as it is; under <c>--serve</c> a protocol frame is stamped with the token, and a job frame with the current job.</summary>
        public void Say(string line) =>
            _output.Writer.TryWrite(Stamp(line, _current?.JobId));

        /// <summary>Says a line exactly as given, with no stamp.</summary>
        public void SayRaw(string line) =>
            _output.Writer.TryWrite(line);

        public void CloseOutput() =>
            _output.Writer.TryComplete();

        /// <summary>Waits for the next job, writes the result files as the script would, then says the lines; see <see cref="PlayJobAsync" />.</summary>
        public void Finish(IReadOnlyList<string> lines, int exitCode) =>
            _ = PlayJobAsync(lines, exitCode);

        /// <summary>
        ///     Under <c>--serve</c>: the handshake was said at spawn, so handshake lines are skipped; a clean job (exit 0 after a terminal frame)
        ///     ends with <c>ready</c> and the process stays up, anything else ends the process with <paramref name="exitCode" />.
        /// </summary>
        private async Task PlayJobAsync(IReadOnlyList<string> lines, int exitCode)
        {
            string config;
            string? jobId = null;
            if (IsServingOrPending())
            {
                var run = await _runs.Reader.ReadAsync();
                config = run.Config;
                jobId = run.JobId;
            }
            else
            {
                await Spawned;
                config = Request!.Arguments[2];
            }

            using (var job = JsonDocument.Parse(await File.ReadAllTextAsync(config)))
            {
                if (job.RootElement.TryGetProperty("vectorPath", out var vectorPath))
                {
                    await File.WriteAllBytesAsync(vectorPath.GetString()!, VectorContent);
                    await File.WriteAllBytesAsync(job.RootElement.GetProperty("randomVectorPath").GetString()!, RandomVectorContent);
                    await File.WriteAllTextAsync(job.RootElement.GetProperty("resultPath").GetString()!, DerivedResult);
                }
                else
                {
                    await File.WriteAllTextAsync(job.RootElement.GetProperty("tracePath").GetString()!, TraceContent);
                    await File.WriteAllTextAsync(job.RootElement.GetProperty("resultPath").GetString()!, TraceResult);
                    if (AttentionContent is not null && job.RootElement.TryGetProperty("attentionPath", out var attentionPath))
                    {
                        await File.WriteAllTextAsync(attentionPath.GetString()!, AttentionContent);
                    }
                }
            }

            if (!IsServing)
            {
                foreach (var line in lines)
                {
                    SayRaw(line);
                }

                End(exitCode);
                return;
            }

            var terminal = false;
            foreach (var line in lines)
            {
                var name = EventOf(line);
                if (name == "handshake")
                {
                    continue;
                }

                terminal |= name is "done" or "error";
                SayRaw(Stamp(line, jobId));
            }

            if (exitCode == 0 && terminal)
            {
                if (!WithholdsReady)
                {
                    SayRaw(Stamp("""{"event":"ready","vramBytes":1000,"reservedBytes":2000}""", jobId));
                }

                if (ExitsAfterReady)
                {
                    End(exitCode: 0);
                }
            }
            else
            {
                End(exitCode);
            }
        }

        /// <summary>Waits for the <c>run</c> command of job number <paramref name="ordinal" />, counted from 1.</summary>
        public Task WaitForRunAsync(int ordinal) =>
            AssertEx.EventuallyAsync(() => WrittenLines.Count(static line => line.Contains("\"op\":\"run\"", StringComparison.Ordinal)) >= ordinal,
                TestBudgets.Contended, $"Run command {ordinal} reached the script.");

        public IAsyncEnumerable<string> ReadOutputAsync(CancellationToken cancellationToken) =>
            OutputFloodsAfterExit ? FloodAfterExitAsync(cancellationToken) : ReadAsync(cancellationToken);

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            var exitCode = await _exit.Task.WaitAsync(cancellationToken);
            if (OutputOutlivesExit)
            {
                await _output.Reader.Completion.WaitAsync(cancellationToken);
            }

            return exitCode;
        }

        public async Task<int?> WaitForGroupExitAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _exit.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        public void RequestStop()
        {
            StopRequested = true;
            if (!IgnoresStop)
            {
                End(exitCode: 3);
            }
        }

        public void KillGroup()
        {
            Killed = true;
            if (ExitsOnKill)
            {
                End(exitCode: 137);
            }
        }

        public void Dispose()
        {
            if (ExitsOnKill)
            {
                End(exitCode: 137);
            }
        }

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            WrittenLines.Enqueue(line);
            using var command = JsonDocument.Parse(line);
            var root = command.RootElement;
            switch (root.GetProperty("op").GetString())
            {
                case "run":
                    var run = new RunCommand
                    {
                        JobId = root.GetProperty("jobId").GetString()!,
                        Config = root.GetProperty("config").GetString()!
                    };
                    _current = run;
                    _runs.Writer.TryWrite(run);
                    _spawned.TrySetResult();
                    OnRun?.Invoke(this);
                    break;
                case "cancel":
                    StopRequested = true;
                    if (!IgnoresStop && root.GetProperty("jobId").GetString() == _current?.JobId)
                    {
                        Say("""{"event":"done","cancelled":true}""");
                        Say("""{"event":"ready","vramBytes":1000,"reservedBytes":2000}""");
                    }

                    break;
                case "evict":
                    EvictRequested = true;
                    if (HoldExitOnEvict is { } hold)
                    {
                        _ = hold.Task.ContinueWith(_ => End(exitCode: 0), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    }
                    else if (!IgnoresStop)
                    {
                        End(exitCode: 0);
                    }

                    break;
            }

            return Task.CompletedTask;
        }

        private bool IsServingOrPending() =>
            Request is null || IsServing;

        private string Stamp(string line, string? jobId)
        {
            if (!IsServing || EventOf(line) is not { } name || JsonNode.Parse(line) is not JsonObject frame)
            {
                return line;
            }

            if (!frame.ContainsKey("token"))
            {
                frame["token"] = Request!.RunToken;
            }

            if (jobId is not null && JobEvents.Contains(name) && !frame.ContainsKey("jobId"))
            {
                frame["jobId"] = jobId;
            }

            return frame.ToJsonString();
        }

        private static string? EventOf(string line)
        {
            if (!line.StartsWith('{'))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.TryGetProperty("event", out var name) ? name.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private void End(int exitCode)
        {
            if (!OutputOutlivesExit)
            {
                _output.Writer.TryComplete();
            }

            _exit.TrySetResult(exitCode);
        }

        private async IAsyncEnumerable<string> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var line in _output.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return line;
                }
            }
            finally
            {
                OutputReadEnded = true;
            }
        }

        private async IAsyncEnumerable<string> FloodAfterExitAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                while (!_exit.Task.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_output.Reader.TryRead(out var line))
                    {
                        yield return line;
                        continue;
                    }

                    _ = await Task.WhenAny(_output.Reader.WaitToReadAsync(cancellationToken).AsTask(), _exit.Task);
                }

                // Never dry and never checking the token: only the consumer, or a kill, stops it.
                while (!Killed)
                {
                    yield return "stray output from a descendant";
                    await Task.Yield();
                }
            }
            finally
            {
                OutputReadEnded = true;
            }
        }

        private sealed class RunCommand
        {
            public required string JobId { get; init; }

            public required string Config { get; init; }
        }
    }

    /// <summary>Hands out one scripted process per spawn, in order, so a test can follow an eviction and the next cold start.</summary>
    internal sealed class ScriptedSpawner : ITrainingProcessSpawner
    {
        private readonly ScriptedProcess[] _processes;
        private int _spawned;

        public ScriptedSpawner(params ScriptedProcess[] processes)
        {
            _processes = processes;
        }

        public int SpawnCount => Volatile.Read(ref _spawned);

        public ITrainingProcessHandle Spawn(TrainingSpawnRequest request)
        {
            var next = Interlocked.Increment(ref _spawned) - 1;
            return next < _processes.Length
                ? _processes[next].Spawn(request)
                : throw new InvalidOperationException($"Spawn {next + 1} has no scripted process.");
        }
    }
}
