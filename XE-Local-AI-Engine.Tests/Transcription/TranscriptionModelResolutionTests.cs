namespace XE_Local_AI_Engine.Tests.Transcription;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Capture;
using XE_Local_AI_Engine.Client.Services.Transcription.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     With no model pinned, the effective model is never an uninstalled recommendation while another model is installed:
///     a CUDA build flipping the recommendation to the full turbo must not strand every new session on a missing file.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class TranscriptionModelResolutionTests : IDisposable
{
    private const long Gigabyte = 1024L * 1024L * 1024L;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-whisper-resolve-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task ResolveEffectiveModelId_WhenTheRecommendationIsNotInstalled_ReturnsTheInstalledModel()
    {
        var service = NewService(installed: ["small"], pinned: null);

        AssertEx.Equal("large-v3-turbo", (await service.GetRecommendedModelAsync(CancellationToken.None)).Id);
        AssertEx.Equal("small", await service.ResolveEffectiveModelIdAsync(CancellationToken.None));
        AssertEx.Equal("small", (await service.GetRuntimeAsync(CancellationToken.None)).EffectiveModelId);
    }

    [Test]
    public async Task ResolveEffectiveModelId_PrefersTheLargestInstalledModelNotAboveTheRecommendation()
    {
        var service = NewService(installed: ["tiny", "small", "large-v3-turbo-q5_0"], pinned: null);

        AssertEx.Equal("large-v3-turbo-q5_0", await service.ResolveEffectiveModelIdAsync(CancellationToken.None));
    }

    [Test]
    public async Task ResolveEffectiveModelId_WhenNothingIsInstalled_ReturnsTheRecommendation()
    {
        var service = NewService(installed: [], pinned: null);

        AssertEx.Equal("large-v3-turbo", await service.ResolveEffectiveModelIdAsync(CancellationToken.None));
    }

    [Test]
    public async Task ResolveEffectiveModelId_WhenAModelIsPinned_ReturnsThePinEvenIfNotInstalled()
    {
        var service = NewService(installed: ["small"], pinned: "base");

        AssertEx.Equal("base", await service.ResolveEffectiveModelIdAsync(CancellationToken.None));
    }

    private TranscriptionRuntimeService NewService(IReadOnlyList<string> installed, string? pinned)
    {
        var pathResolver = new WhisperModelPathResolver(new FakeNodeDataDirectory(_root));
        foreach (var id in installed)
        {
            var path = pathResolver.FilePathFor(AssertEx.NotNull(WhisperModelCatalog.Find(id)));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0]);
        }

        var settings = Substitute.For<INodeSettingsStore>();
        _ = settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings
        {
            TranscriptionSelectedModelId = pinned
        });

        var backendSelector = Substitute.For<IWhisperBackendSelector>();
        _ = backendSelector.SelectBackendAsync(Arg.Any<CancellationToken>()).Returns(WhisperBackend.Cuda);

        var deviceAudit = Substitute.For<IRuntimeDeviceAudit>();
        _ = deviceAudit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(new HardwareProfile
        {
            TotalRamBytes = 47 * Gigabyte,
            AvailableRamBytes = 32 * Gigabyte,
            VramBytes = 32 * Gigabyte,
            AvailableVramBytes = 28 * Gigabyte,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 200 * Gigabyte
        });

        return new TranscriptionRuntimeService(Substitute.For<IWhisperServerSupervisor>(),
            Substitute.For<IWhisperRuntimeActivityGate>(),
            Substitute.For<IWhisperInstalledRuntimeStore>(),
            backendSelector,
            Substitute.For<IWhisperModelDownloadCoordinator>(),
            pathResolver,
            settings,
            deviceAudit,
            Substitute.For<IProcessAudioCaptureSource>(),
            Options.Create(new TranscriptionOptions()));
    }
}
