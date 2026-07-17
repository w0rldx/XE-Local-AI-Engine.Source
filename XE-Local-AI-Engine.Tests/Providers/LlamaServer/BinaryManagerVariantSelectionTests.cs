namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     GPU-variant selection: the OS-aware selection rule
///     (NVIDIA→CUDA on Windows / →Vulkan on Linux, AMD/Intel→Vulkan, none→CPU) plus pinned-vs-upgrade asset
///     resolution. No network and no real GPU probe — the vendor probe is faked.
/// </summary>
public sealed class BinaryManagerVariantSelectionTests
{
    [Test]
    public async Task SelectVariant_WindowsNvidia_PicksCuda()
    {
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.Nvidia), isWindows: true);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Cuda, variant);
    }

    [Test]
    public async Task SelectVariant_LinuxNvidia_FallsBackToVulkan_NoPrebuiltCuda()
    {
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.Nvidia), isWindows: false);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Vulkan, variant);
    }

    [Test]
    public async Task SelectVariant_AmdGpu_PicksVulkan()
    {
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.Amd), isWindows: true);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Vulkan, variant);
    }

    [Test]
    public async Task SelectVariant_IntelGpu_PicksVulkan()
    {
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.Intel), isWindows: false);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Vulkan, variant);
    }

    [Test]
    public async Task SelectVariant_NoGpu_PicksCpu()
    {
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.None), isWindows: false);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Cpu, variant);
    }

    [Test]
    public void ResolvePin_WindowsNvidiaCuda_PicksWinCudaAsset()
    {
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda);

        AssertEx.NotNull(pin);
        AssertEx.Contains(pin!.AssetName, "win-cuda");
        AssertEx.Equal(expected: 64, pin.Sha256.Length);
    }

    [Test]
    public void ResolvePin_LinuxNoGpuPrebuilt_FallsBackToCpuFloor()
    {
        // Linux has no CUDA prebuilt; requesting CUDA on Linux resolves to the CPU floor, never null.
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cuda);

        AssertEx.NotNull(pin);
        AssertEx.Contains(pin!.AssetName, "ubuntu");
        AssertEx.False(pin.AssetName.Contains("cuda", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void ResolveExact_LinuxCuda_ReturnsNull_NoCpuFloorFallback()
    {
        // GPTAUD-09b: unlike Resolve(), the exact resolve never substitutes the CPU floor — Linux has no CUDA prebuilt,
        // so it returns null and the binary manager fails loud rather than serving a CPU archive stamped as CUDA.
        var pin = LlamaCppReleasePins.TryResolveExact(OSPlatform.Linux, Architecture.X64, GpuVariant.Cuda);

        AssertEx.Null(pin);
    }

    [Test]
    public void ResolveExact_WindowsCuda_ReturnsGenuineCudaPin()
    {
        var pin = LlamaCppReleasePins.TryResolveExact(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda);

        AssertEx.NotNull(pin);
        AssertEx.Contains(pin!.AssetName, "win-cuda");
    }

    [Test]
    public void ResolveExact_LinuxCpu_ReturnsCpuPin()
    {
        // The CPU variant's exact pin IS the CPU floor, so a CPU request still resolves.
        var pin = LlamaCppReleasePins.TryResolveExact(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu);

        AssertEx.NotNull(pin);
        AssertEx.Contains(pin!.AssetName, "ubuntu");
    }

    [Test]
    public async Task EnsureBinary_LinuxCudaNoPrebuilt_Throws_NeverServesCpuArchiveAsCuda()
    {
        // GPTAUD-09b: a Cuda request on Linux/x64 (no genuine CUDA prebuilt, no managed source build) must fail loud, even
        // when a binary is cached under the requested "cuda" variant dir — before the fix EnsureBinary resolved the CPU
        // floor pin and returned that cached CPU archive as a LlamaBinary with Variant=Cuda, which the supervisor would
        // then launch with GPU placement flags. The exact-resolve now throws BEFORE any cache/download lookup.
        using var cache = new TempCacheDir();
        var cpuPin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        // Seed a CPU server under the CUDA variant dir — the exact path a pre-fix cache hit would have served as Cuda.
        SeedCachedServer(cache.Path, LlamaCppReleasePins.PinnedTag, "cuda", cpuPin.ServerRelativePath);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64);

        var ex = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None));

        AssertEx.Contains(ex.Message, "prebuilt", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task EnsureBinary_PinnedTag_MarksBinaryAsPinnedFallback()
    {
        using var cache = new TempCacheDir();
        // Pre-seed a cached binary so EnsureBinary reuses it offline (handler would throw if a download were attempted).
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        SeedCachedServer(cache.Path, LlamaCppReleasePins.PinnedTag, "cpu", pin.ServerRelativePath);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.True(binary.IsPinnedFallback);
        AssertEx.Equal(LlamaCppReleasePins.PinnedTag, binary.Version);
    }

    [Test]
    public async Task EnsureBinary_UpgradeTag_KeepsPinnedFallbackCacheIntact()
    {
        using var cache = new TempCacheDir();
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;

        // Both the pinned fallback and an upgrade tag are cached side by side under distinct tag directories.
        var pinnedServer = SeedCachedServer(cache.Path, LlamaCppReleasePins.PinnedTag, "cpu", pin.ServerRelativePath);
        SeedCachedServer(cache.Path, "b9999", "cpu", pin.ServerRelativePath);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, "b9999", OSPlatform.Linux, Architecture.X64);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.False(binary.IsPinnedFallback);
        AssertEx.Equal("b9999", binary.Version);
        // The pinned fallback binary must NOT be deleted by resolving an upgrade.
        AssertEx.True(File.Exists(pinnedServer));
    }

    private static string SeedCachedServer(string cacheRoot, string tag, string variantSlug, string serverRelativePath)
    {
        var serverPath = Path.Combine(cacheRoot, "llama.cpp", tag, variantSlug, serverRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        File.WriteAllText(serverPath, "fake-llama-server");
        return serverPath;
    }

    private sealed class FakeVendorProbe(DetectedGpuVendor vendor) : IGpuVendorProbe
    {
        public Task<DetectedGpuVendor> DetectVendorAsync(CancellationToken ct)
        {
            return Task.FromResult(vendor);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("No network call expected: a cached binary should have been reused.");
        }
    }

    private sealed class TempCacheDir : IDisposable
    {
        public TempCacheDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-llama-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
