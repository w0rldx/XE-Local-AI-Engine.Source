namespace XE_Local_AI_Engine.Tests.Hosting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.BackgroundServices;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The desktop-only first-run provisioning service: it auto-installs and selects a small GGUF chat model on a clean
///     desktop launch, no-ops when a model is already present (idempotent), never runs off the desktop flag (off-flag
///     invariant), and degrades to onboarding rather than crashing when the download fails (offline-tolerant).
/// </summary>
public sealed class FirstRunModelProvisioningServiceTests
{
    private const string DefaultGguf = "bartowski/Qwen2.5-0.5B-Instruct-GGUF:Q4_K_M";

    [Test]
    public async Task CleanDesktopState_EnsuresBinary_Downloads_AndSelectsTheModel()
    {
        var binaryManager = new RecordingBinaryManager();
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = BuildService(isDesktop: true,
            installed: [],
            binaryManager: binaryManager,
            coordinator: coordinator,
            settingsStore: settingsStore);

        await RunAsync(service);

        AssertEx.True(binaryManager.EnsureCalled, "the llama.cpp binary must be ensured before download");
        AssertEx.Equal(1, coordinator.StartCalls.Count);
        AssertEx.Equal("bartowski/Qwen2.5-0.5B-Instruct-GGUF", coordinator.StartCalls[0].RepoId);
        AssertEx.Equal("Q4_K_M", coordinator.StartCalls[0].Quant);
        AssertEx.Equal(GgufRole.Chat, coordinator.StartCalls[0].Role);
        AssertEx.Equal(DefaultGguf, settingsStore.Saved?.DefaultModelName);
    }

    [Test]
    public async Task NotDesktopMode_NoOps_NoDownload_NoSelection()
    {
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = BuildService(isDesktop: false,
            installed: [],
            binaryManager: new RecordingBinaryManager(),
            coordinator: coordinator,
            settingsStore: settingsStore);

        await RunAsync(service);

        AssertEx.Equal(0, coordinator.StartCalls.Count);
        AssertEx.Null(settingsStore.Saved, "the off-flag path must not select a model");
    }

    [Test]
    public async Task GgufAlreadyInstalled_NoOps()
    {
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = BuildService(isDesktop: true,
            installed: ["already/installed:Q4_K_M"],
            binaryManager: new RecordingBinaryManager(),
            coordinator: coordinator,
            settingsStore: settingsStore);

        await RunAsync(service);

        AssertEx.Equal(0, coordinator.StartCalls.Count);
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
            installed: [],
            binaryManager: new RecordingBinaryManager(),
            coordinator: coordinator,
            settingsStore: settingsStore);

        await RunAsync(service);

        AssertEx.Equal(0, coordinator.StartCalls.Count);
        AssertEx.Null(settingsStore.Saved);
    }

    [Test]
    public async Task DownloadFails_LeavesOnboarding_DoesNotSelect_AndDoesNotThrow()
    {
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Failed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = BuildService(isDesktop: true,
            installed: [],
            binaryManager: new RecordingBinaryManager(),
            coordinator: coordinator,
            settingsStore: settingsStore);

        // Must not throw — offline-tolerance keeps startup alive with the empty-picker onboarding fallback.
        await RunAsync(service);

        AssertEx.Equal(1, coordinator.StartCalls.Count);
        AssertEx.Null(settingsStore.Saved, "a failed download must not select a model");
    }

    [Test]
    public async Task BinaryAcquisitionThrows_IsSwallowed_DoesNotCrash()
    {
        var coordinator = new FakeDownloadCoordinator(GgufDownloadPhase.Completed);
        var settingsStore = new FakeNodeSettingsStore(new StoredNodeSettings());
        using var service = BuildService(isDesktop: true,
            installed: [],
            binaryManager: new RecordingBinaryManager
            {
                ThrowOnEnsure = true
            },
            coordinator: coordinator,
            settingsStore: settingsStore);

        await RunAsync(service);

        AssertEx.Equal(0, coordinator.StartCalls.Count);
        AssertEx.Null(settingsStore.Saved);
    }

    private static FirstRunModelProvisioningService BuildService(bool isDesktop,
        IReadOnlyList<string> installed,
        RecordingBinaryManager binaryManager,
        FakeDownloadCoordinator coordinator,
        FakeNodeSettingsStore settingsStore)
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
            new FakeVariantSelector(),
            settingsStore,
            NullLogger<FirstRunModelProvisioningService>.Instance,
            isDesktop,
            TimeSpan.FromMilliseconds(5));
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

        public bool ThrowOnEnsure { get; init; }

        public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct)
        {
            EnsureCalled = true;
            if (ThrowOnEnsure)
            {
                throw new LlamaRuntimeException("No prebuilt llama.cpp runtime is available.");
            }

            return Task.FromResult(new LlamaBinary("/fake/llama-server", "b9692", variant, true));
        }
    }

    private sealed class FakeVariantSelector : IGpuVariantSelector
    {
        public Task<GpuVariant> SelectVariantAsync(CancellationToken ct) => Task.FromResult(GpuVariant.Cpu);
    }

    private sealed class FakeGgufModelStore(IReadOnlyList<string> installed) : IGgufModelStore
    {
        public Task<string?> ResolveModelFilePathAsync(string modelName, CancellationToken ct) => Task.FromResult<string?>(null);

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

        public Task<string> ResolveModelNameAsync(GgufModelRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<GgufModelHandle> EnsureModelAsync(GgufModelRequest request, IProgress<PullProgress>? progress, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteModelAsync(string modelName, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string modelName, CancellationToken ct) => Task.FromResult(false);
    }

    /// <summary>A coordinator that records Start requests and reports a fixed terminal phase for any model name.</summary>
    private sealed class FakeDownloadCoordinator(GgufDownloadPhase terminalPhase) : IGgufDownloadCoordinator
    {
        public List<GgufModelRequest> StartCalls { get; } = [];

        public Task<GgufDownloadTicket> StartAsync(GgufModelRequest request, CancellationToken ct)
        {
            StartCalls.Add(request);
            var modelName = string.IsNullOrWhiteSpace(request.Quant) ? request.RepoId : GgufModelName.Format(request.RepoId, request.Quant);
            return Task.FromResult(new GgufDownloadTicket(modelName, false));
        }

        public bool Cancel(string modelName) => false;

        public GgufDownloadStatus? GetStatus(string modelName) =>
            new(modelName, terminalPhase, null, null, terminalPhase == GgufDownloadPhase.Failed ? "Download failed." : null);
    }

    private sealed class FakeNodeSettingsStore(StoredNodeSettings initial) : INodeSettingsStore
    {
        private StoredNodeSettings _current = initial;

        public StoredNodeSettings? Saved { get; private set; }

        public Task<StoredNodeSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_current);

        public Task SaveAsync(StoredNodeSettings settings, CancellationToken cancellationToken = default)
        {
            _current = settings;
            Saved = settings;
            return Task.CompletedTask;
        }
    }
}
