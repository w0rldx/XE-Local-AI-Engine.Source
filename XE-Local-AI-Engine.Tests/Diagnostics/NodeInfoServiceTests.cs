namespace XE_Local_AI_Engine.Tests.Diagnostics;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Diagnostics;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Testing.Fakes;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="NodeInfoService" />: a failing or silent provider degrades to a null section plus a warning, the
///     settings carry exactly the reviewed fields, and the report carries no path a runtime record holds.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class NodeInfoServiceTests
{
    private readonly ManualTimeProvider _clock = new(DateTimeOffset.UnixEpoch.AddYears(56));
    private readonly IRuntimeDeviceAudit _deviceAudit = Substitute.For<IRuntimeDeviceAudit>();
    private readonly IGgufModelStore _models = Substitute.For<IGgufModelStore>();
    private readonly IHardwareProfiler _profiler = Substitute.For<IHardwareProfiler>();
    private readonly IInstalledRuntimeStore _llamaStore = Substitute.For<IInstalledRuntimeStore>();
    private readonly ILlamaServerProcessSupervisor _llamaSupervisor = Substitute.For<ILlamaServerProcessSupervisor>();
    private readonly INodeSettingsAdministrationService _settings = Substitute.For<INodeSettingsAdministrationService>();
    private readonly ILiveMemorySampler _sampler = Substitute.For<ILiveMemorySampler>();

    public NodeInfoServiceTests()
    {
        _profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new HardwareProfile
        {
            TotalRamBytes = 64,
            AvailableRamBytes = 32,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 1000
        });
        _deviceAudit.GetAuditAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new RuntimeDeviceAuditState
        {
            InferenceBackend = "cuda",
            GpuExpected = true,
            CpuFallback = false,
            Devices = [new RuntimeAuditDevice { Name = "RTX Test", TotalBytes = 32, FreeBytes = 30 }]
        });
        _sampler.SampleAsync(Arg.Any<CancellationToken>()).Returns(new LiveMemorySample
        {
            TotalRamBytes = 64,
            AvailableRamBytes = 20,
            Gpus = []
        });
        _llamaStore.ReadAsync(Arg.Any<CancellationToken>()).Returns(new InstalledRuntimeState("b9999",
            "asset.zip",
            "sha",
            GpuVariant.Cuda,
            DateTimeOffset.UnixEpoch,
            SourceBuildPath: "/home/jane/llama.cpp/build",
            SourceCommit: "abc1234"));
        _llamaSupervisor.CheckHealthAsync(Arg.Any<CancellationToken>()).Returns([
            new LlamaServerProcessHealth
            {
                ModelName = "qwen2.5-0.5b",
                Role = ModelRole.Chat,
                IsResponsive = true,
                Detail = "ok on http://127.0.0.1:41234 from /home/jane/models/q.gguf",
                IsBusy = true,
                LastUsedUtc = DateTimeOffset.UnixEpoch
            },
            new LlamaServerProcessHealth
            {
                ModelName = "bge-small",
                Role = ModelRole.Embedding,
                IsResponsive = false,
                HasExited = true,
                Detail = "exited"
            }
        ]);
        _models.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns([
            new LocalModelDescriptor
            {
                ModelName = "qwen2.5-0.5b",
                ProviderName = "gguf",
                IsAvailable = true,
                SizeBytes = 400,
                ModifiedAt = null,
                MaxContextTokens = null
            }
        ]);
        _settings.GetTrustedSettingsAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings
        {
            EnableTools = true,
            MachineKey = "0123456789abcdef0123456789abcdef",
            OllamaEndpoint = "http://user:pass@ollama.lan:11434",
            WebSearchSearxngUrl = "http://searx.lan",
            UpdateChannel = "preview"
        });
    }

    [Test]
    public async Task GetAsync_WithHealthyProviders_FillsEverySection()
    {
        var info = await CreateService().GetAsync(isShellOwned: true, CancellationToken.None);

        AssertEx.Empty(info.Warnings);
        AssertEx.Equal(expected: 16, info.CpuCores);
        AssertEx.Equal(expected: 20L, info.AvailableRamBytes, "The live reading wins over the cached profile.");
        AssertEx.Equal("cuda", info.InferenceBackend);
        AssertEx.Equal("RTX Test", AssertEx.NotNull(info.Gpus).Single().Name);
        var runtime = AssertEx.NotNull(info.Runtimes).Single();
        AssertEx.Equal("llama-cpp", runtime.Kind);
        AssertEx.Equal("abc1234", runtime.SourceCommit);
        AssertEx.Equal("qwen2.5-0.5b", AssertEx.NotNull(info.Models).Single().Name);
        var running = AssertEx.NotNull(info.RunningModels);
        AssertEx.Equal(expected: 2, running.Count);
        AssertEx.Equal(new NodeInfoRunningModel
        {
            ModelName = "qwen2.5-0.5b",
            Role = "chat",
            State = "responsive",
            IsBusy = true,
            IsTransient = false,
            LastUsedUtc = DateTimeOffset.UnixEpoch
        }, running[0]);
        AssertEx.Equal("embedding", running[1].Role);
        AssertEx.Equal("exited", running[1].State);
        AssertEx.Equal("preview", info.SelectedChannel);
        AssertEx.Equal("stable", info.DefaultChannel);
        AssertEx.Equal("https://github.com/acme/xe", info.RepositoryUrl);
        AssertEx.True(info.IsShellOwned, "The host fact is passed through.");
        AssertEx.Equal("tester", info.Flavour);
    }

    [Test]
    public async Task GetAsync_WhenAProviderThrows_ReportsANullSectionAndAWarning()
    {
        _profiler.GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("probe broke"));

        var info = await CreateService().GetAsync(isShellOwned: false, CancellationToken.None);

        AssertEx.Null(info.CpuCores);
        AssertEx.Null(info.TotalRamBytes);
        AssertEx.Equal("hardware profile: InvalidOperationException", string.Join(" | ", info.Warnings));
        AssertEx.NotNull(info.Models);
        AssertEx.NotNull(info.Settings);
    }

    [Test]
    public async Task GetAsync_WhenTheLlamaSupervisorThrows_ReportsNoRunningModelsAndAWarning()
    {
        _llamaSupervisor.CheckHealthAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("probe failed"));

        var info = await CreateService().GetAsync(isShellOwned: false, CancellationToken.None);

        AssertEx.Null(info.RunningModels);
        AssertEx.Equal("running models: HttpRequestException", string.Join(" | ", info.Warnings));
        AssertEx.NotNull(info.Models);
    }

    [Test]
    public async Task GetAsync_WhenAProviderNeverAnswers_GivesUpAtTheBudget()
    {
        var never = new TaskCompletionSource<RuntimeDeviceAuditState>();
        _deviceAudit.GetAuditAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(never.Task);

        var pending = CreateService().GetAsync(isShellOwned: false, CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(pending, "The capture must wait for the audit until the budget expires.");
        _clock.Advance(NodeInfoService.CaptureBudget);
        var info = await pending;

        AssertEx.Null(info.InferenceBackend);
        AssertEx.Equal("device audit: did not answer in time", string.Join(" | ", info.Warnings));
        AssertEx.Equal(expected: 16, info.CpuCores, "The sections that answered are kept.");
    }

    [Test]
    public async Task GetAsync_SettingsCarryTheReviewedFieldsOnly()
    {
        var info = await CreateService().GetAsync(isShellOwned: false, CancellationToken.None);
        var settings = AssertEx.NotNull(info.Settings);

        AssertEx.Equal("true", settings["enableTools"]);
        AssertEx.Equal("preview", settings["updateChannel"]);
        AssertEx.False(settings.ContainsKey("machineKey"), "The machine key is never reported.");
        AssertEx.False(settings.ContainsKey("ollamaEndpoint"), "Operator URLs are never reported.");
        AssertEx.False(settings.ContainsKey("webSearchSearxngUrl"), "Operator URLs are never reported.");
        AssertEx.Equal(NodeInfoService.IncludedSettings.Count, settings.Count);
        var all = string.Join('|', settings.Values);
        AssertEx.False(all.Contains("pass@", StringComparison.Ordinal), all);
        AssertEx.False(all.Contains("0123456789abcdef", StringComparison.Ordinal), all);
    }

    [Test]
    public void SettingsReview_CoversEveryStoredField_ExactlyOnce()
    {
        var fields = typeof(StoredNodeSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                               .Select(static property => property.Name)
                                               .Where(static name => name != "EqualityContract")
                                               .ToHashSet(StringComparer.Ordinal);

        var unreviewed = fields.Except(NodeInfoService.IncludedSettings).Except(NodeInfoService.ExcludedSettings).ToArray();
        AssertEx.Empty(unreviewed, "A new StoredNodeSettings field must be added to IncludedSettings or ExcludedSettings: " + string.Join(", ", unreviewed));
        AssertEx.Empty(NodeInfoService.IncludedSettings.Intersect(NodeInfoService.ExcludedSettings).ToArray());
        AssertEx.Empty(NodeInfoService.IncludedSettings.Concat(NodeInfoService.ExcludedSettings).Except(fields).ToArray());
    }

    private NodeInfoService CreateService()
    {
        var sdStore = Substitute.For<IStableDiffusionInstalledRuntimeStore>();
        sdStore.ReadAsync(Arg.Any<CancellationToken>()).Returns((StableDiffusionInstalledRuntimeState?)null);
        var whisperStore = Substitute.For<IWhisperInstalledRuntimeStore>();
        whisperStore.ReadAsync(Arg.Any<CancellationToken>()).Returns((WhisperInstalledRuntimeState?)null);
        var residents = new RuntimeResidentsService(Substitute.For<IImageServerSupervisor>(),
            new ImageRuntimeActivityGate(),
            Substitute.For<IWhisperServerSupervisor>(),
            new WhisperRuntimeActivityGate(),
            Options.Create(new TranscriptionOptions { Enabled = false }));
        return new NodeInfoService(_profiler,
            _deviceAudit,
            _sampler,
            _llamaStore,
            _llamaSupervisor,
            sdStore,
            whisperStore,
            residents,
            _models,
            _settings,
            Options.Create(new AppUpdateChannelOptions
            {
                Channel = "tester",
                GitHubRepositoryUrl = "https://github.com/acme/xe"
            }),
            new NodeLaunchContext { IsLocalMode = false },
            _clock,
            new NodeLogLevelSwitch(new ConfigurationBuilder().Build()),
            new RecordingLogger<NodeInfoService>());
    }
}
