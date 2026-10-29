namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     On an Offline node with no runtime, a launch must not download one; it fails with a reason the chat bubble
///     shows. An explicit operator install still downloads, and an installed runtime is still served. All HTTP is faked.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ImplicitRuntimeAcquisitionPolicyTests
{
    private const string Refusal = "No runtime, and the policy refused to fetch one.";

    [Test]
    public async Task EnsureBinary_WhenThePolicyRefusesAndNothingIsInstalled_ThrowsTheReasonWithoutDownloading()
    {
        using var cache = new TempDirectory();
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = CreateManager(http, cache.Path, new RefusingPolicy());

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeNotInstalledException>(() => manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));

        AssertEx.Equal(Refusal, exception.Message);
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task AcquireBinary_WhenThePolicyRefuses_StillDownloadsForTheOperator()
    {
        using var cache = new TempDirectory();
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = CreateManager(http, cache.Path, new RefusingPolicy());

        // The fake archive fails verification; reaching the network at all is what the explicit install must do.
        _ = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.AcquireBinaryAsync(GpuVariant.Cpu, CancellationToken.None));

        AssertEx.True(handler.CallCount > 0, "an explicit install must download despite the policy");
    }

    [Test]
    public async Task EnsureBinary_WhenThePolicyRefusesButTheRuntimeIsCached_ServesIt()
    {
        using var cache = new TempDirectory();
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        var serverPath = Path.Combine(cache.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cpu", pin.ServerRelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        await File.WriteAllTextAsync(serverPath, "fake-llama-server");
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = CreateManager(http, cache.Path, new RefusingPolicy());

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    /// <summary>
    ///     A pin bump must not strand an Offline node: the installed older tag is still on disk and is served, and the
    ///     live catalog, which would pick the new tag and then find nothing, is never asked.
    /// </summary>
    [Test]
    public async Task EnsureBinary_WhenThePolicyRefusesAndAnOlderInstalledTagIsOnDisk_ServesItWithoutTheCatalog()
    {
        using var cache = new TempDirectory();
        const string olderTag = "b1";
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        var serverPath = Path.Combine(cache.Path, "llama.cpp", olderTag, "cpu", pin.ServerRelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        await File.WriteAllTextAsync(serverPath, "fake-llama-server");
        var store = Substitute.For<IInstalledRuntimeStore>();
        store.ReadAsync(Arg.Any<CancellationToken>())
             .Returns(new InstalledRuntimeState(olderTag, pin.AssetName, new string('a', 64), GpuVariant.Cpu, DateTimeOffset.UnixEpoch));
        var catalog = Substitute.For<ILlamaCppReleaseCatalog>();
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64, TimeProvider.System,
            catalog, store, implicitAcquisitionPolicy: new RefusingPolicy());

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.Equal(olderTag, binary.Version);
        AssertEx.Equal(expected: 0, handler.CallCount);
        await catalog.DidNotReceiveWithAnyArgs().ResolveAssetAsync(default!, default, default, default, default);
    }

    /// <summary>
    ///     A cached Windows-CUDA server missing its companion DLLs is served as it is when the policy refuses: the repair download is not attempted, and
    ///     the banner says why the runtime runs without CUDA.
    /// </summary>
    [Test]
    public async Task EnsureBinary_WhenThePolicyRefusesAndTheCachedCudaCompanionSetIsIncomplete_ServesItWithoutTheRepairDownload()
    {
        using var cache = new TempDirectory();
        var serverPath = await WriteCachedWindowsCudaServerAsync(cache.Path);
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RuntimeAcquisitionStatusRegistry(new NullRuntimeAcquisitionEventPublisher(), NullLogger<RuntimeAcquisitionStatusRegistry>.Instance, TimeProvider.System);
        var manager = CreateWindowsManager(http, cache.Path, registry);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), registry.Current.Phase);
        var reason = AssertEx.NotNull(registry.Current.SanitizedError);
        AssertEx.True(reason.StartsWith(CudaRepairFailurePrefix, StringComparison.Ordinal), reason);
        AssertEx.Contains(reason, "external access profile does not download them automatically", StringComparison.Ordinal);
    }

    [Test]
    public async Task AcquireBinary_WhenThePolicyRefusesAndTheCachedCudaCompanionSetIsIncomplete_StillAttemptsTheRepairForTheOperator()
    {
        using var cache = new TempDirectory();
        var serverPath = await WriteCachedWindowsCudaServerAsync(cache.Path);
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RuntimeAcquisitionStatusRegistry(new NullRuntimeAcquisitionEventPublisher(), NullLogger<RuntimeAcquisitionStatusRegistry>.Instance, TimeProvider.System);
        var manager = CreateWindowsManager(http, cache.Path, registry);

        // The fake companion archive fails verification, so the repair fails and the cached server is served; reaching the network is what is pinned.
        var binary = await manager.AcquireBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.True(handler.CallCount > 0, "an explicit install must attempt the companion repair despite the policy");
        var reason = AssertEx.NotNull(registry.Current.SanitizedError);
        AssertEx.False(reason.Contains("external access profile", StringComparison.Ordinal), reason);
    }

    /// <summary>The refusal holds until the operator installs a runtime, so the supervisor surfaces it once instead of retrying with backoff.</summary>
    [Test]
    public async Task Supervisor_WhenTheRuntimeIsNotInstalled_DoesNotRetryTheSpawn()
    {
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns<Task<LlamaBinary>>(_ => throw new LlamaRuntimeNotInstalledException(Refusal));
        await using var supervisor = SupervisorFactory.Create(binaryManager: binaryManager,
            options: new LlamaServerSupervisorOptions
            {
                MaxRestartAttempts = 3,
                IdleTimeToLive = TimeSpan.FromHours(1)
            });

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeNotInstalledException>(() =>
            supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None));

        AssertEx.Equal(Refusal, exception.Message);
        await binaryManager.Received(1).EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments(StoredNodeSettings.ExternalAccessProfileOffline, true)]
    [Arguments(StoredNodeSettings.ExternalAccessProfileRecommended, false)]
    [Arguments(StoredNodeSettings.ExternalAccessProfileCustom, false)]
    public async Task OfflineProfilePolicy_RefusesOnlyUnderOffline(string profile, bool refuses)
    {
        var settings = Substitute.For<INodeRuntimeSettings>();
        settings.GetExternalAccessProfileAsync(Arg.Any<CancellationToken>()).Returns(profile);

        var refusal = await new OfflineProfileLlamaCppAcquisitionPolicy(settings).GetRefusalAsync(CancellationToken.None);

        AssertEx.Equal(refuses, refusal is not null);
        AssertEx.Equal(refuses ? OfflineProfileLlamaCppAcquisitionPolicy.OfflineRefusalMessage : string.Empty, refusal ?? string.Empty);
    }

    [Test]
    public void Classifier_SurfacesTheRefusalVerbatimEvenWhenWrapped()
    {
        var wrapped = new InvalidOperationException("launch failed", new LlamaRuntimeNotInstalledException(OfflineProfileLlamaCppAcquisitionPolicy.OfflineRefusalMessage));

        var classification = InvocationFailureClassifier.MapFailure(wrapped);

        AssertEx.Equal(FailureCategory.ModelLoadFailed, classification.Category);
        AssertEx.Equal(OfflineProfileLlamaCppAcquisitionPolicy.OfflineRefusalMessage, classification.Message);
    }

    private static LlamaCppBinaryManager CreateManager(HttpClient http, string cacheRoot, ILlamaCppImplicitAcquisitionPolicy policy) =>
        new(http, cacheRoot, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64, TimeProvider.System, implicitAcquisitionPolicy: policy);

    private const string CudaRepairFailurePrefix = "The CUDA runtime libraries of the installed llama.cpp runtime are incomplete and could not be repaired";

    private static LlamaCppBinaryManager CreateWindowsManager(HttpClient http, string cacheRoot, RuntimeAcquisitionStatusRegistry registry) =>
        new(http, cacheRoot, LlamaCppReleasePins.PinnedTag, OSPlatform.Windows, Architecture.X64, TimeProvider.System, acquisitionStatus: registry,
            implicitAcquisitionPolicy: new RefusingPolicy());

    /// <summary>A published pinned Windows-CUDA server with ggml-cuda but none of the companion DLLs.</summary>
    private static async Task<string> WriteCachedWindowsCudaServerAsync(string cacheRoot)
    {
        var pin = AssertEx.NotNull(LlamaCppReleasePins.ResolveExactOrNull(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda));
        var serverPath = Path.Combine(cacheRoot, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cuda",
            pin.ServerRelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
        var serverDir = Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!).FullName;
        await File.WriteAllTextAsync(serverPath, "fake-llama-server");
        await File.WriteAllTextAsync(Path.Combine(serverDir, "ggml-cuda.dll"), "fake-ggml-cuda");
        return serverPath;
    }

    private sealed class RefusingPolicy : ILlamaCppImplicitAcquisitionPolicy
    {
        public Task<string?> GetRefusalAsync(CancellationToken ct) =>
            Task.FromResult<string?>(Refusal);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("not-the-pinned-archive"u8.ToArray())
            });
        }
    }
}
