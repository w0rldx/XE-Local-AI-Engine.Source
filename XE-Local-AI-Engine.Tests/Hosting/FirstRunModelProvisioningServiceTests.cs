namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The desktop-only first-run provisioning service: it auto-installs and selects a small GGUF chat model on a clean
///     desktop launch, no-ops when a model is already present (idempotent), never runs off the desktop flag (off-flag
///     invariant), and degrades to onboarding rather than crashing when the download fails (offline-tolerant).
/// </summary>
public sealed class FirstRunModelProvisioningServiceTests
{
    private const string DefaultGguf = "bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M";

    // A path-shaped fragment planted in a probe exception message, so the sanitization assertion has something concrete
    // to prove was stripped rather than merely checking the text is non-empty.
    private const string ProbeSecretPath = "/home/operator/tools/nvidia-smi";

    [Test]
    public async Task CleanDesktopState_EnsuresBinary_Downloads_AndSelectsTheModel()
    {
        var binaryManager = new RecordingBinaryManager();
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = BuildService(isDesktop: true,
            [],
            binaryManager,
            coordinator,
            settingsStore);

        await RunAsync(service);

        AssertEx.True(binaryManager.EnsureCalled, "the llama.cpp binary must be ensured before download");
        AssertEx.Equal(expected: 1, coordinator.StartCalls.Count);
        AssertEx.Equal("bartowski/Qwen2.5-0.5B-Instruct-GGUF", coordinator.StartCalls[0].RepoId);
        AssertEx.Equal("Q4_K_M", coordinator.StartCalls[0].Quant);
        AssertEx.Equal(GgufRole.Chat, coordinator.StartCalls[0].Role);
        AssertEx.Equal(DefaultGguf, settingsStore.Saved?.DefaultModelName);
    }

    [Test]
    public async Task CleanDesktopState_WhenTheMachineKeyIsMintedWhileTheModelDownloads_KeepsBothTheKeyAndTheSelection()
    {
        // This service loads the settings BEFORE the download and writes the default model AFTER it, so the window
        // between the two is as long as the download — minutes on a first run. The settings record is whole-file, so
        // saving the record loaded up front would roll the node back to its pre-download state, discarding the machine
        // key the boot path mints in exactly that window and orphaning every frozen inference profile.
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings(),
            siblingWriteBeforeTheUpdate: latest => latest with
            {
                MachineKey = "minted-while-the-model-downloaded"
            });
        using var service = BuildService(isDesktop: true,
            [],
            new RecordingBinaryManager(),
            new FakeDownloadCoordinator(GgufDownloadPhase.Completed),
            settingsStore);

        await RunAsync(service);

        AssertEx.Equal(DefaultGguf, settingsStore.Current.DefaultModelName);
        AssertEx.Equal("minted-while-the-model-downloaded", settingsStore.Current.MachineKey,
            "the key minted during the download must survive the first-run selection.");
    }

    [Test]
    public async Task CleanDesktopState_WhenTheOperatorSelectsAModelWhileTheDownloadRuns_KeepsTheOperatorsChoice()
    {
        // The skip precondition is checked BEFORE the download, and the download runs for minutes. An operator who
        // installs and picks their own model in that window had it silently reverted to the auto-provisioned one,
        // because the post-download write assigned DefaultModelName unconditionally.
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings(),
            siblingWriteBeforeTheUpdate: latest => latest with
            {
                DefaultModelName = "operator/picked:Q8_0"
            });
        using var service = BuildService(isDesktop: true,
            [],
            new RecordingBinaryManager(),
            new FakeDownloadCoordinator(GgufDownloadPhase.Completed),
            settingsStore);

        await RunAsync(service);

        AssertEx.Equal("operator/picked:Q8_0", settingsStore.Current.DefaultModelName,
            "a selection made during the download must not be reverted to the first-run model.");
    }

    [Test]
    public async Task NotDesktopMode_NoOps_NoDownload_NoSelection()
    {
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = BuildService(isDesktop: false,
            [],
            new RecordingBinaryManager(),
            coordinator,
            settingsStore);

        await RunAsync(service);

        AssertEx.Equal(expected: 0, coordinator.StartCalls.Count);
        AssertEx.Null(settingsStore.Saved, "the off-flag path must not select a model");
    }

    [Test]
    public async Task GgufAlreadyInstalled_NoOps()
    {
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = BuildService(isDesktop: true,
            ["already/installed:Q4_K_M"],
            new RecordingBinaryManager(),
            coordinator,
            settingsStore);

        await RunAsync(service);

        AssertEx.Equal(expected: 0, coordinator.StartCalls.Count);
        AssertEx.Null(settingsStore.Saved);
    }

    [Test]
    public async Task NonDefaultModelAlreadySelected_NoOps()
    {
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            DefaultModelName = "operator/picked:Q8_0"
        });
        using var service = BuildService(isDesktop: true,
            [],
            new RecordingBinaryManager(),
            coordinator,
            settingsStore);

        await RunAsync(service);

        AssertEx.Equal(expected: 0, coordinator.StartCalls.Count);
        AssertEx.Null(settingsStore.Saved);
    }

    [Test]
    public async Task DownloadFails_LeavesOnboarding_DoesNotSelect_AndDoesNotThrow()
    {
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Failed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = BuildService(isDesktop: true,
            [],
            new RecordingBinaryManager(),
            coordinator,
            settingsStore);

        // Must not throw — offline-tolerance keeps startup alive with the empty-picker onboarding fallback.
        await RunAsync(service);

        AssertEx.Equal(expected: 1, coordinator.StartCalls.Count);
        AssertEx.Null(settingsStore.Saved, "a failed download must not select a model");
    }

    [Test]
    public async Task BinaryAcquisitionThrows_IsSwallowed_DoesNotCrash()
    {
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = BuildService(isDesktop: true,
            [],
            new RecordingBinaryManager
            {
                ThrowOnEnsure = true
            },
            coordinator,
            settingsStore);

        await RunAsync(service);

        AssertEx.Equal(expected: 0, coordinator.StartCalls.Count);
        AssertEx.Null(settingsStore.Saved);
    }

    [Test]
    public async Task GpuProbeOverrunsCeiling_FallsBackToCpu_AndStillProvisions()
    {
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        var binaryManager = new RecordingBinaryManager();
        // A selector that hangs until cancelled — it observes the provisioning ceiling (linked CTS) and throws
        // OperationCanceledException, exactly as the real cancellation-linked probe does when its child overruns.
        var hangingSelector = new HangingVariantSelector();
        using var service = BuildService(isDesktop: true,
            [],
            binaryManager,
            coordinator,
            settingsStore,
            hangingSelector,
            TimeSpan.FromMilliseconds(20));

        await RunAsync(service);

        // The ceiling fired, detection fell back to CPU, and provisioning still reached the download + selection.
        AssertEx.True(binaryManager.EnsureCalled, "provisioning must continue to the download after the probe ceiling");
        AssertEx.Equal(GpuVariant.Cpu, binaryManager.LastVariant);
        AssertEx.Equal(expected: 1, coordinator.StartCalls.Count);
        AssertEx.Equal(DefaultGguf, settingsStore.Saved?.DefaultModelName);
    }

    [Test]
    public async Task NotDesktopMode_ReportsNoAcquisitionStatus()
    {
        var acquisitionStatus = new RecordingAcquisitionStatusRegistry();
        using var service = BuildService(isDesktop: false,
            [],
            new RecordingBinaryManager(),
            new FakeDownloadCoordinator(GgufDownloadPhase.Completed),
            new FakeNodeSettingsStore(new StoredNodeSettings()),
            acquisitionStatus: acquisitionStatus);

        await RunAsync(service);

        // Off-flag invariant: a headless / Aspire / CI host must introduce no new work at all before the desktop gate,
        // so the acquisition channel stays silent and its snapshot stays Idle.
        AssertEx.Equal(expected: 0, acquisitionStatus.Updates.Count);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Idle), acquisitionStatus.Current.Phase);
    }

    [Test]
    public async Task CleanDesktopState_ReportsDetectingGpu_BeforeTheProbeRuns()
    {
        var acquisitionStatus = new RecordingAcquisitionStatusRegistry();
        var variantSelector = new FakeVariantSelector(acquisitionStatus);
        using var service = BuildService(isDesktop: true,
            [],
            new RecordingBinaryManager(),
            new FakeDownloadCoordinator(GgufDownloadPhase.Completed),
            new FakeNodeSettingsStore(new StoredNodeSettings()),
            variantSelector,
            acquisitionStatus: acquisitionStatus);

        await RunAsync(service);

        // The probe is the FIRST silent multi-second phase, so the status must already say DetectingGpu by the time the
        // probe is entered — reporting it afterwards would leave exactly the unexplained pause this channel exists for.
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.DetectingGpu), variantSelector.PhaseAtProbe);
        AssertEx.Equal(RuntimeAcquisitionPhase.DetectingGpu, acquisitionStatus.Updates[0].Phase);
    }

    [Test]
    public async Task GpuProbeThrows_ReportsSanitizedFailure_AndStillDoesNotCrash()
    {
        var acquisitionStatus = new RecordingAcquisitionStatusRegistry();
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed);
        using var service = BuildService(isDesktop: true,
            [],
            new RecordingBinaryManager(),
            coordinator,
            new FakeNodeSettingsStore(new StoredNodeSettings()),
            new ThrowingVariantSelector(),
            acquisitionStatus: acquisitionStatus);

        // The exception still propagates to the outer catch, so startup survives; only the reporting is new.
        await RunAsync(service);

        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), acquisitionStatus.Current.Phase);
        AssertEx.Equal(expected: 0, coordinator.StartCalls.Count);

        // Sanitization is asserted, not assumed: an arbitrary exception message can carry an absolute path, so anything
        // that is not a user-safe LlamaRuntimeException must be collapsed rather than surfaced verbatim.
        var sanitized = acquisitionStatus.Current.SanitizedError;
        AssertEx.NotNull(sanitized);
        AssertEx.False(sanitized!.Contains(ProbeSecretPath, StringComparison.Ordinal),
            "an acquisition failure must never surface a raw exception message containing a path");
    }

    [Test]
    public async Task GpuProbeOverrunsCeiling_FallsBackToCpu_AndReportsNoFailure()
    {
        var acquisitionStatus = new RecordingAcquisitionStatusRegistry();
        var binaryManager = new RecordingBinaryManager();
        using var service = BuildService(isDesktop: true,
            [],
            binaryManager,
            new FakeDownloadCoordinator(GgufDownloadPhase.Completed),
            new FakeNodeSettingsStore(new StoredNodeSettings()),
            new HangingVariantSelector(),
            TimeSpan.FromMilliseconds(20),
            acquisitionStatus);

        await RunAsync(service);

        // The ceiling overrun is a FALLBACK to the CPU runtime, not a failure — publishing Failed here would show an
        // error banner for a run that goes on to acquire the CPU runtime and provision normally.
        AssertEx.Equal(GpuVariant.Cpu, binaryManager.LastVariant);
        AssertEx.False(acquisitionStatus.Updates.Any(static update => update.Phase == RuntimeAcquisitionPhase.Failed),
            "the probe-ceiling CPU fallback must not report an acquisition failure");
    }

    [Test]
    public async Task ThrowAfterAcquisitionSucceeds_DoesNotReportFailure_AndLeavesCompleted()
    {
        var acquisitionStatus = new RecordingAcquisitionStatusRegistry();
        // The binary manager owns the Completed report in production; the fake stands in for it so this test can assert
        // what happens to that terminal state when a LATER provisioning step throws.
        var binaryManager = new RecordingBinaryManager
        {
            AcquisitionStatus = acquisitionStatus
        };
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed)
        {
            ThrowOnStart = true
        };
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = BuildService(isDesktop: true,
            [],
            binaryManager,
            coordinator,
            settingsStore,
            acquisitionStatus: acquisitionStatus);

        await RunAsync(service);

        // Guards the scoping of the terminal state: the outer ExecuteAsync catch spans the model download and the
        // settings save too, so publishing Failed from there would overwrite a legitimate Completed with a false runtime
        // failure — and the banner's retry would become a dead button attached to a wrong diagnosis. The model-download
        // failure path has its own channel.
        AssertEx.True(binaryManager.EnsureCalled, "acquisition must have succeeded before the later step threw");
        AssertEx.False(acquisitionStatus.Updates.Any(static update => update.Phase == RuntimeAcquisitionPhase.Failed),
            "a post-acquisition throw must not be reported as a runtime acquisition failure");
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Completed), acquisitionStatus.Current.Phase);
        AssertEx.Null(settingsStore.Saved);
    }

    private static FirstRunModelProvisioningService BuildService(bool isDesktop,
        IReadOnlyList<string> installed,
        RecordingBinaryManager binaryManager,
        FakeDownloadCoordinator coordinator,
        FakeNodeSettingsStore settingsStore,
        IGpuVariantSelector? variantSelector = null,
        TimeSpan? gpuProbeCeiling = null,
        IRuntimeAcquisitionStatusRegistry? acquisitionStatus = null)
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["FirstRunModel:Enabled"] = "true",
                                ["FirstRunModel:RepoId"] = "bartowski/Qwen2.5-0.5B-Instruct-GGUF",
                                ["FirstRunModel:Quant"] = "Q4_K_M",
                                ["Agent:LocalChat:DefaultModel"] = "qwen3:0.6b"
                            })
                            .Build();

        return new FirstRunModelProvisioningService(configuration,
            new FakeGgufModelStore(installed),
            coordinator,
            binaryManager,
            variantSelector ?? new FakeVariantSelector(),
            settingsStore,
            acquisitionStatus ?? new RecordingAcquisitionStatusRegistry(),
            NullLogger<FirstRunModelProvisioningService>.Instance,
            isDesktop,
            TimeSpan.FromMilliseconds(5),
            gpuProbeCeiling ?? TimeSpan.FromSeconds(25));
    }

    private static async Task RunAsync(FirstRunModelProvisioningService service)
    {
        // StartAsync returns as soon as ExecuteAsync first yields (the download poll uses a real timer), so await the
        // background ExecuteTask to drive provisioning to completion before asserting. The caller owns disposal.
        await service.StartAsync(CancellationToken.None);
        if (service.ExecuteTask is not null)
        {
            await service.ExecuteTask;
        }

        await service.StopAsync(CancellationToken.None);
    }

    private sealed class RecordingBinaryManager : ILlamaCppBinaryManager
    {
        public bool EnsureCalled { get; private set; }

        public GpuVariant? LastVariant { get; private set; }

        public bool ThrowOnEnsure { get; init; }

        /// <summary>
        ///     When set, the fake reports Completed on a successful ensure — standing in for the real manager, which owns
        ///     the download/verify/extract phases and their terminal status.
        /// </summary>
        public IRuntimeAcquisitionStatusRegistry? AcquisitionStatus { get; init; }

        public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct)
        {
            EnsureCalled = true;
            LastVariant = variant;
            if (ThrowOnEnsure)
            {
                throw new LlamaRuntimeException("No prebuilt llama.cpp runtime is available.");
            }

            AcquisitionStatus?.Report(new RuntimeAcquisitionUpdate(RuntimeAcquisitionPhase.Completed, variant.ToString(), "b9692"));
            return Task.FromResult(new LlamaBinary("/fake/llama-server", "b9692", variant, IsPinnedFallback: true));
        }

        public Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct)
        {
            return Task.FromResult(new LlamaBinary("/fake/llama-server", tag, variant, IsPinnedFallback: false));
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

    /// <summary>
    ///     A selector that resolves immediately and, when handed the registry, records the phase the acquisition status
    ///     was already showing at the moment the probe was entered — the ordering the DetectingGpu report exists to give.
    /// </summary>
    private sealed class FakeVariantSelector(RecordingAcquisitionStatusRegistry? acquisitionStatus = null) : IGpuVariantSelector
    {
        public string? PhaseAtProbe { get; private set; }

        public Task<GpuVariant> SelectVariantAsync(CancellationToken ct)
        {
            PhaseAtProbe = acquisitionStatus?.Current.Phase;
            return Task.FromResult(GpuVariant.Cpu);
        }
    }

    /// <summary>A selector that fails outright, standing in for a probe that cannot run at all (not a ceiling overrun).</summary>
    private sealed class ThrowingVariantSelector : IGpuVariantSelector
    {
        public Task<GpuVariant> SelectVariantAsync(CancellationToken ct)
        {
            throw new InvalidOperationException($"{ProbeSecretPath} exited with code 127.");
        }
    }

    /// <summary>
    ///     An in-memory <see cref="IRuntimeAcquisitionStatusRegistry" /> that keeps every update in order and stamps the
    ///     same monotonic sequence the real registry does, without its throttle or its publisher.
    /// </summary>
    private sealed class RecordingAcquisitionStatusRegistry : IRuntimeAcquisitionStatusRegistry
    {
        private long _sequence;

        public List<RuntimeAcquisitionUpdate> Updates { get; } = [];

        public RuntimeAcquisitionStatusHubEvent Current { get; private set; } = new(Sequence: 0,
            nameof(RuntimeAcquisitionPhase.Idle),
            Variant: null,
            Tag: null,
            CompletedBytes: null,
            TotalBytes: null,
            StepIndex: 1,
            StepCount: 1,
            SanitizedError: null);

        public void Report(RuntimeAcquisitionUpdate update)
        {
            Updates.Add(update);
            Current = new RuntimeAcquisitionStatusHubEvent(++_sequence,
                update.Phase.ToString(),
                update.Variant,
                update.Tag,
                update.CompletedBytes,
                update.TotalBytes,
                update.StepIndex,
                update.StepCount,
                update.SanitizedError);
        }
    }

    /// <summary>
    ///     A selector that never completes on its own — it waits on the supplied token, mirroring how the real
    ///     cancellation-linked probe blocks until the provisioning ceiling cancels it (then throws).
    /// </summary>
    private sealed class HangingVariantSelector : IGpuVariantSelector
    {
        public async Task<GpuVariant> SelectVariantAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return GpuVariant.Cpu;
        }
    }

    private sealed class FakeGgufModelStore(IReadOnlyList<string> installed) : IGgufModelStore
    {
        public Task<string?> ResolveModelFilePathAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<string?> ResolveProjectorFilePathAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct)
        {
            IReadOnlyList<LocalModelDescriptor> descriptors = installed
                                                              .Select(static name => new LocalModelDescriptor
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
            throw new NotSupportedException();
        }

        public Task<GgufModelHandle> EnsureModelAsync(GgufModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct)
        {
            throw new NotSupportedException();
        }

        public Task DeleteModelAsync(string modelName, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult(false);
        }

        public Task<GgufModelFootprintFacts?> ResolveModelFootprintFactsAsync(string modelName, CancellationToken ct)
        {
            return Task.FromResult<GgufModelFootprintFacts?>(null);
        }
    }

    /// <summary>A coordinator that records Start requests and reports a fixed terminal phase for any model name.</summary>
    private sealed class FakeDownloadCoordinator(GgufDownloadPhase terminalPhase) : IGgufDownloadCoordinator
    {
        public List<GgufModelRequest> StartCalls { get; } = [];

        /// <summary>Simulates a provisioning step failing AFTER the runtime was successfully acquired.</summary>
        public bool ThrowOnStart { get; init; }

        public Task<GgufDownloadTicket> StartAsync(GgufModelRequest request, CancellationToken ct)
        {
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("The model download could not be started.");
            }

            StartCalls.Add(request);
            var modelName = string.IsNullOrWhiteSpace(request.Quant) ? request.RepoId : GgufModelName.Format(request.RepoId, request.Quant);
            return Task.FromResult(new GgufDownloadTicket(modelName, AlreadyInFlight: false));
        }

        public bool Cancel(string modelName)
        {
            return false;
        }

        public GgufDownloadStatus? GetStatus(string modelName)
        {
            return new GgufDownloadStatus(modelName, terminalPhase, CompletedBytes: null, TotalBytes: null, terminalPhase == GgufDownloadPhase.Failed ? "Download failed." : null);
        }

        public IReadOnlyList<GgufDownloadStatus> ListStatuses()
        {
            return [];
        }
    }

}
