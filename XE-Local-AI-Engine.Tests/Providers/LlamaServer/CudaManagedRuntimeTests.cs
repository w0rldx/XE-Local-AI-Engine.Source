namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Managed source-built CUDA runtime: the <see cref="LlamaCppBinaryManager" /> serve-time short-circuit
///     (path-chain + SHA re-validation), the <see cref="RecordResolvedRuntimeAsync" /> source-build guard, the
///     <see cref="GpuVariantSelector" /> cached-signal rule, and the additive <see cref="InstalledRuntimeState" /> field.
///     POSIX-only (the validation spawns a real stub executable + uses Unix file modes).
/// </summary>
public sealed class CudaManagedRuntimeTests
{
    private const string GpuStub = "#!/bin/sh\ncase \"$1\" in\n  --version) echo 'version: test'; exit 0 ;;\n  --list-devices) echo 'Available devices:'; echo '  CUDA0: Test GPU (24000 MiB, 23000 MiB free)'; exit 0 ;;\n  *) exit 0 ;;\nesac\n";

    [Test]
    public async Task EnsureBinary_ManagedCuda_ServesBuiltBinaryNoDownload()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var (binDir, serverPath, sha) = SeedSourceBuild(dir.Path, GpuStub);
        using var store = new InstalledRuntimeStore(dir.Path);
        await store.WriteAsync(SourceBuildState(binDir, sha), CancellationToken.None);
        var signal = new CudaManagedBuildSignal();
        signal.MarkAvailable();

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, store, overrideOptions: null, signal);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.Equal(GpuVariant.Cuda, binary.Variant);
        AssertEx.Equal(LlamaCppReleasePins.PinnedTag, binary.Version);
        AssertEx.False(binary.IsPinnedFallback);
    }

    [Test]
    public async Task EnsureBinary_ManagedCuda_ShaMismatch_DiscardsAndFailsLoud()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var (binDir, serverPath, _) = SeedSourceBuild(dir.Path, GpuStub);
        using var store = new InstalledRuntimeStore(dir.Path);
        // Record a deliberately wrong SHA so the serve-time recompare fails.
        await store.WriteAsync(SourceBuildState(binDir, new string('a', 64)), CancellationToken.None);
        _ = serverPath;
        var signal = new CudaManagedBuildSignal();
        signal.MarkAvailable();

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, store, overrideOptions: null, signal);

        // No prebuilt Linux CUDA asset exists → after discarding the bad record the Cuda request fails loudly (no silent CPU).
        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None));

        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
        AssertEx.False(signal.IsAvailable);
    }

    [Test]
    public async Task EnsureBinary_ManagedRecordMissingOnDisk_GracefulFallbackNoSilentCpu()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var missingBin = Path.Combine(dir.Path, "llama.cpp", "source-cuda", LlamaCppReleasePins.PinnedTag, "build", "bin");
        using var store = new InstalledRuntimeStore(dir.Path);
        await store.WriteAsync(SourceBuildState(missingBin, new string('b', 64)), CancellationToken.None);
        var signal = new CudaManagedBuildSignal();
        signal.MarkAvailable();

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, store, overrideOptions: null, signal);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None));

        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
        AssertEx.False(signal.IsAvailable);
    }

    [Test]
    public async Task EnsureBinary_ManagedCuda_WorldWritableAncestor_Discarded()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var (binDir, _, sha) = SeedSourceBuild(dir.Path, GpuStub);
        using var store = new InstalledRuntimeStore(dir.Path);
        await store.WriteAsync(SourceBuildState(binDir, sha), CancellationToken.None);
        var signal = new CudaManagedBuildSignal();
        signal.MarkAvailable();

        // Make an ancestor world-writable → the full path-chain check rejects the build.
        var sourceCuda = Path.Combine(dir.Path, "llama.cpp", "source-cuda");
        MakeWorldWritable(sourceCuda);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, store, overrideOptions: null, signal);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
        AssertEx.False(signal.IsAvailable);
    }

    [Test]
    public async Task EnsureBinary_Vulkan_DoesNotClobberManagedCudaRecord()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        var (binDir, _, sha) = SeedSourceBuild(dir.Path, GpuStub);
        using var store = new InstalledRuntimeStore(dir.Path);
        await store.WriteAsync(SourceBuildState(binDir, sha), CancellationToken.None);

        // Pre-place a cached Vulkan binary for the recorded tag so the Vulkan ensure resolves from cache (no download),
        // reaching RecordResolvedRuntimeAsync — which must NOT overwrite the source-build record.
        var vulkanBin = Path.Combine(dir.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "vulkan", "build", "bin");
        Directory.CreateDirectory(vulkanBin);
        WriteExecutableStub(vulkanBin, GpuStub);

        var signal = new CudaManagedBuildSignal();
        signal.MarkAvailable();

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, store, overrideOptions: null, signal);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Vulkan, CancellationToken.None);
        AssertEx.Equal(GpuVariant.Vulkan, binary.Variant);

        var after = await store.ReadAsync(CancellationToken.None);
        AssertEx.NotNull(after);
        AssertEx.Equal(GpuVariant.Cuda, after!.Variant);
        AssertEx.Equal(binDir, after.SourceBuildPath);
    }

    [Test]
    public async Task Selector_WhenManagedCudaSignalSet_ReturnsCuda()
    {
        var signal = new CudaManagedBuildSignal();
        signal.MarkAvailable();
        var selector = new GpuVariantSelector(new StubVendorProbe(DetectedGpuVendor.Nvidia), isWindows: false, overrideOptions: null, signal);

        AssertEx.Equal(GpuVariant.Cuda, await selector.SelectVariantAsync(CancellationToken.None));
    }

    [Test]
    public async Task Selector_WhenNoManagedBuild_UnchangedRule()
    {
        var selector = new GpuVariantSelector(new StubVendorProbe(DetectedGpuVendor.Nvidia), isWindows: false, overrideOptions: null, new CudaManagedBuildSignal());

        // Linux NVIDIA with no managed build → Vulkan (unchanged rule).
        AssertEx.Equal(GpuVariant.Vulkan, await selector.SelectVariantAsync(CancellationToken.None));
    }

    [Test]
    public async Task RemoveCudaSourceBuild_RefusesPathOutsideSourceCuda()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TempDir();
        using var store = new InstalledRuntimeStore(dir.Path);

        // A recorded build path OUTSIDE {cacheRoot}/llama.cpp/source-cuda/ must never trigger a delete of the source-cuda tree.
        var outsidePath = Path.Combine(dir.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cuda", "build", "bin");
        await store.WriteAsync(SourceBuildState(outsidePath, new string('e', 64)), CancellationToken.None);

        // Seed the real source-cuda tree with a sentinel so we can prove it survives the remove.
        var sourceCuda = Path.Combine(dir.Path, "llama.cpp", "source-cuda");
        Directory.CreateDirectory(sourceCuda);
        var sentinel = Path.Combine(sourceCuda, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep", CancellationToken.None);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, store, overrideOptions: null, new CudaManagedBuildSignal());

        await manager.RemoveCudaSourceBuildAsync(CancellationToken.None);

        AssertEx.True(File.Exists(sentinel), "Remove must not delete the source-cuda tree when the recorded path is outside it.");
    }

    [Test]
    public async Task InstalledRuntimeState_RoundTrips_SourceBuildPath_OldFileLoadsNull()
    {
        using var dir = new TempDir();
        using var store = new InstalledRuntimeStore(dir.Path);

        // Additive field round-trips.
        await store.WriteAsync(SourceBuildState("/some/bin", new string('c', 64)), CancellationToken.None);
        var read = await store.ReadAsync(CancellationToken.None);
        AssertEx.NotNull(read);
        AssertEx.Equal("/some/bin", read!.SourceBuildPath);

        // An old-format record (no SourceBuildPath) loads with null.
        var legacy = new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag, "asset.tar.gz", new string('d', 64), GpuVariant.Cpu, DateTimeOffset.UtcNow);
        await store.WriteAsync(legacy, CancellationToken.None);
        var readLegacy = await store.ReadAsync(CancellationToken.None);
        AssertEx.NotNull(readLegacy);
        AssertEx.Null(readLegacy!.SourceBuildPath);
    }

    private static InstalledRuntimeState SourceBuildState(string binDir, string sha)
    {
        return new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag,
            "(source-build:cuda)",
            sha,
            GpuVariant.Cuda,
            DateTimeOffset.UtcNow,
            binDir);
    }

    [UnsupportedOSPlatform("windows")]
    private static (string BinDir, string ServerPath, string Sha) SeedSourceBuild(string cacheRoot, string script)
    {
        var binDir = Path.Combine(cacheRoot, "llama.cpp", "source-cuda", LlamaCppReleasePins.PinnedTag, "build", "bin");
        Directory.CreateDirectory(binDir);
        var serverPath = WriteExecutableStub(binDir, script);
        using var stream = File.OpenRead(serverPath);
        var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        return (binDir, serverPath, sha);
    }

    [UnsupportedOSPlatform("windows")]
    private static string WriteExecutableStub(string dir, string script)
    {
        var path = Path.Combine(dir, "llama-server");
        File.WriteAllText(path, script);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    [UnsupportedOSPlatform("windows")]
    private static void MakeWorldWritable(string path)
    {
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.OtherWrite);
    }

    private sealed class StubVendorProbe(DetectedGpuVendor vendor) : IGpuVendorProbe
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
            throw new InvalidOperationException("No network call expected: a managed CUDA serve must never download.");
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-cuda-managed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
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
