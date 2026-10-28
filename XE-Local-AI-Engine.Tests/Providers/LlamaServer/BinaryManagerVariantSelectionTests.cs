namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     GPU-variant selection (NVIDIA→CUDA where a CUDA prebuilt is pinned, else Vulkan; AMD/Intel→Vulkan; none→CPU) plus
///     pinned-vs-upgrade asset resolution. No network; the vendor probe is faked.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BinaryManagerVariantSelectionTests
{
    [Test]
    public async Task SelectVariant_WindowsNvidia_PicksCuda()
    {
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.Nvidia), OSPlatform.Windows, Architecture.X64);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Cuda, variant);
    }

    [Test]
    public async Task SelectVariant_LinuxX64Nvidia_PicksCuda()
    {
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.Nvidia), OSPlatform.Linux, Architecture.X64);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Cuda, variant);
    }

    [Test]
    [Arguments("Linux")]
    [Arguments("Windows")]
    public async Task SelectVariant_Arm64Nvidia_FallsBackToVulkan_NoPrebuiltCuda(string os)
    {
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.Nvidia), (os == "Linux" ? OSPlatform.Linux : OSPlatform.Windows), Architecture.Arm64);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Vulkan, variant);
    }

    [Test]
    public async Task SelectVariant_AmdGpu_PicksVulkan()
    {
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.Amd), OSPlatform.Windows, Architecture.X64);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Vulkan, variant);
    }

    [Test]
    public async Task SelectVariant_IntelGpu_PicksVulkan()
    {
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.Intel), OSPlatform.Linux, Architecture.X64);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Vulkan, variant);
    }

    [Test]
    public async Task SelectVariant_NoGpu_PicksCpu()
    {
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.None), OSPlatform.Linux, Architecture.X64);

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
        // No Linux arm64 CUDA prebuilt is pinned; requesting CUDA there resolves to the CPU floor, never null.
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Cuda);

        AssertEx.NotNull(pin);
        AssertEx.Contains(pin!.AssetName, "ubuntu");
        AssertEx.False(pin.AssetName.Contains("cuda", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public void ResolveExact_LinuxArm64Cuda_ReturnsNull_NoCpuFloorFallback()
    {
        // Unlike Resolve(), the exact resolve never substitutes the CPU floor — no Linux arm64 CUDA prebuilt is pinned,
        // so it returns null and the binary manager fails loud rather than serving a CPU archive stamped as CUDA.
        var pin = LlamaCppReleasePins.TryResolveExact(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Cuda);

        AssertEx.Null(pin);
    }

    [Test]
    public void ResolveExact_LinuxX64Cuda_ReturnsGenuineCudaPin()
    {
        var pin = LlamaCppReleasePins.TryResolveExact(OSPlatform.Linux, Architecture.X64, GpuVariant.Cuda);

        AssertEx.NotNull(pin);
        AssertEx.Contains(pin!.AssetName, "ubuntu-cuda");
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
    public async Task EnsureBinary_LinuxArm64CudaNoPrebuilt_Throws_NeverServesCpuArchiveAsCuda()
    {
        // A Cuda request on Linux/arm64 (no CUDA prebuilt or source build) fails loud even with a "cuda" cached binary:
        // EnsureBinary throws BEFORE any cache lookup, never returning the CPU archive as Variant=Cuda for GPU flags.
        using var cache = new TempCacheDir();
        var cpuPin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.Arm64, GpuVariant.Cpu)!;
        // Seed a CPU server under the CUDA variant dir — the exact path a lax cache hit would serve as Cuda.
        SeedCachedServer(cache.Path, LlamaCppReleasePins.PinnedTag, "cuda", cpuPin.ServerRelativePath);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.Arm64, TimeProvider.System);

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
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64, TimeProvider.System);

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
        var manager = new LlamaCppBinaryManager(http, cache.Path, "b9999", OSPlatform.Linux, Architecture.X64, TimeProvider.System);

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

    private sealed class FakeVendorProbe : IGpuVendorProbe
    {
        private readonly DetectedGpuVendor _vendor;

        public FakeVendorProbe(DetectedGpuVendor vendor)
        {
            _vendor = vendor;
        }

        public Task<DetectedGpuVendor> DetectVendorAsync(CancellationToken ct)
        {
            return Task.FromResult(_vendor);
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
