namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The operator variant pin: <see cref="InstalledRuntimeState.OperatorSelected" /> survives the store, an explicit
///     acquire writes it and an implicit ensure never does or clobbers it, the selector and the startup seed honour it
///     below the override, and <see cref="LlamaCppBinaryManager.ClearVariantPinAsync" /> restores the vendor rule.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class OperatorVariantPinTests
{
    [Test]
    public async Task Store_RecordWrittenBeforeThePinExisted_ReadsAsNotOperatorSelected()
    {
        using var dir = new TempDirectory("xe-variant-pin");
        await File.WriteAllTextAsync(dir.FilePath("installed-runtime.json"),
            """{"tag":"b1","asset":"llama-b1-bin-ubuntu-vulkan-x64.tar.gz","sha256":"aa","variant":2,"installedAtUtc":"2026-01-01T00:00:00+00:00"}""");
        using var store = new InstalledRuntimeStore(dir.Path);

        var read = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));

        AssertEx.Equal(GpuVariant.Vulkan, read.Variant);
        AssertEx.False(read.OperatorSelected);
    }

    [Test]
    public async Task Store_PinnedRecord_RoundTripsTheFlag()
    {
        using var dir = new TempDirectory("xe-variant-pin");
        using var store = new InstalledRuntimeStore(dir.Path);

        await store.WriteAsync(PrebuiltRecord(GpuVariant.Vulkan, operatorSelected: true), CancellationToken.None);

        AssertEx.True(AssertEx.NotNull(await store.ReadAsync(CancellationToken.None)).OperatorSelected);
    }

    [Test]
    [Arguments(true, GpuVariant.Vulkan)]
    [Arguments(false, GpuVariant.Cuda)]
    public async Task Selector_OnLinuxNvidia_HonoursOnlyAnOperatorPinnedPrebuilt(bool operatorSelected, GpuVariant expected)
    {
        var store = StoreReturning(PrebuiltRecord(GpuVariant.Vulkan, operatorSelected));
        var selector = new GpuVariantSelector(new FixedVendorProbe(DetectedGpuVendor.Nvidia), OSPlatform.Linux, Architecture.X64, overrideOptions: null,
            new CudaManagedBuildSignal(), store);

        AssertEx.Equal(expected, await selector.SelectVariantAsync(CancellationToken.None));
    }

    [Test]
    public async Task Selector_WithAnActiveOverride_IgnoresThePin()
    {
        var store = StoreReturning(PrebuiltRecord(GpuVariant.Vulkan, operatorSelected: true));
        var overrideOptions = new LlamaServerRuntimeOverrideOptions
        {
            ServerPath = "/opt/llama/llama-server",
            Variant = GpuVariant.Cpu
        };
        var selector = new GpuVariantSelector(new FixedVendorProbe(DetectedGpuVendor.Nvidia), OSPlatform.Linux, Architecture.X64, overrideOptions,
            new CudaManagedBuildSignal(), store);

        AssertEx.Equal(GpuVariant.Cpu, await selector.SelectVariantAsync(CancellationToken.None));
    }

    [Test]
    public void RecordedPinnedVariant_ForASourceBuild_KeysOffItsBinaryNotTheFlag()
    {
        using var dir = new TempDirectory("xe-variant-pin");
        var missingBuild = PrebuiltRecord(GpuVariant.Cuda, operatorSelected: true) with
        {
            SourceBuildPath = dir.FilePath("absent-bin")
        };

        // A source build stays authoritative on its own terms: a recorded build whose binary is gone pins nothing, flag or not.
        AssertEx.Null(CudaBuildStartupService.RecordedPinnedVariant(missingBuild));
        AssertEx.Equal(GpuVariant.Vulkan, CudaBuildStartupService.RecordedPinnedVariant(PrebuiltRecord(GpuVariant.Vulkan, operatorSelected: true)));
        AssertEx.Null(CudaBuildStartupService.RecordedPinnedVariant(PrebuiltRecord(GpuVariant.Vulkan, operatorSelected: false)));
    }

    [Test]
    public async Task StartupService_SeedsTheSignalFromAPinnedPrebuilt()
    {
        var signal = new CudaManagedBuildSignal();
        var service = new CudaBuildStartupService(Substitute.For<ILlamaCppSourceBuildService>(),
            StoreReturning(PrebuiltRecord(GpuVariant.Vulkan, operatorSelected: true)),
            signal,
            NullLogger<CudaBuildStartupService>.Instance);

        await service.StartAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Vulkan, signal.ActiveVariant);
    }

    [Test]
    public async Task ExplicitAcquire_OverAnImplicitRecord_PinsTheVariantAndSeedsTheSignal()
    {
        using var cache = new TempDirectory("xe-variant-pin");
        SeedCachedPrebuilt(cache.Path, GpuVariant.Vulkan);
        using var store = new InstalledRuntimeStore(cache.Path);
        await store.WriteAsync(PrebuiltRecord(GpuVariant.Cpu, operatorSelected: false), CancellationToken.None);
        var signal = new CudaManagedBuildSignal();
        using var handler = new NoNetworkHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = CreateManager(http, cache.Path, store, signal);

        await manager.AcquireBinaryAsync(GpuVariant.Vulkan, CancellationToken.None);

        var record = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(GpuVariant.Vulkan, record.Variant);
        AssertEx.True(record.OperatorSelected);
        AssertEx.Equal(GpuVariant.Vulkan, signal.ActiveVariant);
    }

    [Test]
    public async Task ExplicitAcquire_OfTheRecordedVariant_FlipsTheFlagAndKeepsTheRecord()
    {
        using var cache = new TempDirectory("xe-variant-pin");
        SeedCachedPrebuilt(cache.Path, GpuVariant.Vulkan);
        using var store = new InstalledRuntimeStore(cache.Path);
        var implicitRecord = PrebuiltRecord(GpuVariant.Vulkan, operatorSelected: false);
        await store.WriteAsync(implicitRecord, CancellationToken.None);
        using var handler = new NoNetworkHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = CreateManager(http, cache.Path, store, new CudaManagedBuildSignal());

        await manager.AcquireBinaryAsync(GpuVariant.Vulkan, CancellationToken.None);

        AssertEx.Equal(implicitRecord with
        {
            OperatorSelected = true
        }, await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task UnpinnedAcquireAndImplicitEnsure_RecordNoPin()
    {
        using var cache = new TempDirectory("xe-variant-pin");
        SeedCachedPrebuilt(cache.Path, GpuVariant.Vulkan);
        using var store = new InstalledRuntimeStore(cache.Path);
        var signal = new CudaManagedBuildSignal();
        using var handler = new NoNetworkHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = CreateManager(http, cache.Path, store, signal);

        await manager.AcquireBinaryAsync(GpuVariant.Vulkan, pinVariant: false, CancellationToken.None);
        AssertEx.False(AssertEx.NotNull(await store.ReadAsync(CancellationToken.None)).OperatorSelected);

        await store.DeleteAsync(CancellationToken.None);
        await manager.EnsureBinaryAsync(GpuVariant.Vulkan, CancellationToken.None);
        AssertEx.False(AssertEx.NotNull(await store.ReadAsync(CancellationToken.None)).OperatorSelected);
        AssertEx.Null(signal.ActiveVariant);
    }

    [Test]
    [Arguments(GpuVariant.Vulkan)]
    [Arguments(GpuVariant.Cpu)]
    public async Task ImplicitEnsure_NeverRewritesAPinnedRecord(GpuVariant ensured)
    {
        using var cache = new TempDirectory("xe-variant-pin");
        SeedCachedPrebuilt(cache.Path, GpuVariant.Vulkan);
        SeedCachedPrebuilt(cache.Path, GpuVariant.Cpu);
        using var store = new InstalledRuntimeStore(cache.Path);
        var pinned = PrebuiltRecord(GpuVariant.Vulkan, operatorSelected: true);
        await store.WriteAsync(pinned, CancellationToken.None);
        using var handler = new NoNetworkHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = CreateManager(http, cache.Path, store, new CudaManagedBuildSignal());

        // The pinned variant's own re-serve keeps the pin, and an implicit ensure of another variant must not replace it.
        await manager.EnsureBinaryAsync(ensured, CancellationToken.None);

        AssertEx.Equal(pinned, await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task ClearVariantPin_RewritesTheRecordUnpinnedAndClearsTheSignal()
    {
        using var cache = new TempDirectory("xe-variant-pin");
        using var store = new InstalledRuntimeStore(cache.Path);
        var pinned = PrebuiltRecord(GpuVariant.Vulkan, operatorSelected: true);
        await store.WriteAsync(pinned, CancellationToken.None);
        var signal = new CudaManagedBuildSignal();
        signal.SetActive(GpuVariant.Vulkan);
        var before = signal.Version;
        using var handler = new NoNetworkHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = CreateManager(http, cache.Path, store, signal);

        await manager.ClearVariantPinAsync(CancellationToken.None);

        AssertEx.Equal(pinned with
        {
            OperatorSelected = false
        }, await store.ReadAsync(CancellationToken.None));
        AssertEx.Null(signal.ActiveVariant);
        AssertEx.True(signal.Version > before, "the cleared pin must invalidate memos keyed to the signal stamp");
    }

    [Test]
    public async Task ClearVariantPin_DropsAProcessOnlyPinAndLeavesTheUnpinnedRecord()
    {
        using var cache = new TempDirectory("xe-variant-pin");
        using var store = new InstalledRuntimeStore(cache.Path);
        var otherVariant = PrebuiltRecord(GpuVariant.Cpu, operatorSelected: false);
        await store.WriteAsync(otherVariant, CancellationToken.None);
        var signal = new CudaManagedBuildSignal();
        signal.SetActive(GpuVariant.Vulkan);
        using var handler = new NoNetworkHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = CreateManager(http, cache.Path, store, signal);

        await manager.ClearVariantPinAsync(CancellationToken.None);

        AssertEx.Null(signal.ActiveVariant);
        AssertEx.Equal(otherVariant, await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task ClearVariantPin_LeavesARecordedSourceBuildAlone()
    {
        using var cache = new TempDirectory("xe-variant-pin");
        using var store = new InstalledRuntimeStore(cache.Path);
        var sourceBuild = PrebuiltRecord(GpuVariant.Cuda, operatorSelected: false) with
        {
            SourceBuildPath = cache.FilePath("bin")
        };
        await store.WriteAsync(sourceBuild, CancellationToken.None);
        var signal = new CudaManagedBuildSignal();
        signal.SetActive(GpuVariant.Cuda);
        var before = signal.Version;
        using var handler = new NoNetworkHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = CreateManager(http, cache.Path, store, signal);

        await manager.ClearVariantPinAsync(CancellationToken.None);

        AssertEx.Equal(sourceBuild, await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(GpuVariant.Cuda, signal.ActiveVariant);
        AssertEx.Equal(before, signal.Version);
    }

    // The handler refuses every request: each test serves a binary already on disk.
    private static LlamaCppBinaryManager CreateManager(HttpClient http, string cacheRoot, IInstalledRuntimeStore store, ICudaManagedBuildSignal signal)
    {
        return new LlamaCppBinaryManager(http, cacheRoot, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, TimeProvider.System, catalog: null, store, overrideOptions: null, signal);
    }

    private static void SeedCachedPrebuilt(string cacheRoot, GpuVariant variant)
    {
        var pin = AssertEx.NotNull(LlamaCppReleasePins.TryResolveExact(OSPlatform.Linux, Architecture.X64, variant));
        var slug = variant == GpuVariant.Vulkan ? "vulkan" : "cpu";
        var serverPath = Path.Combine(cacheRoot, "llama.cpp", LlamaCppReleasePins.PinnedTag, slug,
            pin.ServerRelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        File.WriteAllText(serverPath, "cached-binary");
    }

    private static InstalledRuntimeState PrebuiltRecord(GpuVariant variant, bool operatorSelected)
    {
        var pin = AssertEx.NotNull(LlamaCppReleasePins.TryResolveExact(OSPlatform.Linux, Architecture.X64, variant));
        return new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag, pin.AssetName, pin.Sha256, variant,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), OperatorSelected: operatorSelected);
    }

    private static IInstalledRuntimeStore StoreReturning(InstalledRuntimeState state)
    {
        var store = Substitute.For<IInstalledRuntimeStore>();
        store.ReadAsync(Arg.Any<CancellationToken>()).Returns(state);
        return store;
    }

    private sealed class FixedVendorProbe : IGpuVendorProbe
    {
        private readonly DetectedGpuVendor _vendor;

        public FixedVendorProbe(DetectedGpuVendor vendor)
        {
            _vendor = vendor;
        }

        public Task<DetectedGpuVendor> DetectVendorAsync(CancellationToken ct) =>
            Task.FromResult(_vendor);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A cached serve must not download.");
    }
}
