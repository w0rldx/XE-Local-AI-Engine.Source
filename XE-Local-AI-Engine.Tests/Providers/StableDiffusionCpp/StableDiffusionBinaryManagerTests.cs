namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Net;
using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Configuration;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class StableDiffusionBinaryManagerTests
{
    [Test]
    public async Task EnsureBinary_CorruptDownload_RetriesOnce_ThenSurfacesSanitizedFailure()
    {
        using var cache = new TempCacheDir();
        // The handler always returns bytes that cannot match the pinned SHA256 → forced corruption on every attempt.
        using var handler = new CountingHandler(static () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("not-the-pinned-archive"u8.ToArray())
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new StableDiffusionCppBinaryManager(http, cache.Path, StableDiffusionReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64);

        var exception = await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => manager.EnsureBinaryAsync(SdGpuBackend.Cpu, CancellationToken.None));

        // Re-download exactly once: two total attempts.
        AssertEx.Equal(expected: 2, handler.CallCount);
        // Sanitized surface — no internal absolute path leaks into the user-facing message.
        AssertEx.False(exception.Message.Contains(cache.Path, StringComparison.Ordinal));
        AssertEx.False(exception.Message.Contains(Path.GetTempPath(), StringComparison.Ordinal));
        AssertEx.Contains(exception.Message, "integrity", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task EnsureBinary_CachedBinaryPresent_ReusedOffline_NoDownload()
    {
        using var cache = new TempCacheDir();
        var pin = StableDiffusionReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, SdGpuBackend.Cpu)!;
        var serverPath = Path.Combine(cache.Path, "stable-diffusion.cpp", StableDiffusionReleasePins.PinnedTag, "cpu", pin.ServerRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        await File.WriteAllTextAsync(serverPath, "fake-sd-server");

        using var handler = new CountingHandler(static () =>
            throw new InvalidOperationException("Offline reuse must not hit the network."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new StableDiffusionCppBinaryManager(http, cache.Path, StableDiffusionReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64);

        var binary = await manager.EnsureBinaryAsync(SdGpuBackend.Cpu, CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.True(binary.IsPinnedFallback);
        AssertEx.Equal(SdGpuBackend.Cpu, binary.Backend);
    }

    [Test]
    public async Task EnsureBinary_BringYourOwnOverride_ServesConfiguredBinary_NoDownload()
    {
        using var cache = new TempCacheDir();
        var byoPath = Path.Combine(cache.Path, "byo-sd-server");
        await File.WriteAllTextAsync(byoPath, "operator-built-sd-server");

        var overrideOptions = new StableDiffusionServerRuntimeOverrideOptions
        {
            ServerPath = byoPath,
            Backend = SdGpuBackend.Cuda
        };

        using var handler = new CountingHandler(static () =>
            throw new InvalidOperationException("An active override must not hit the network."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new StableDiffusionCppBinaryManager(http, cache.Path, StableDiffusionReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64, overrideOptions);

        // Even when the caller requests CPU, an active override serves its OWN backend and path.
        var binary = await manager.EnsureBinaryAsync(SdGpuBackend.Cpu, CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(byoPath, binary.ServerExecutablePath);
        AssertEx.Equal(SdGpuBackend.Cuda, binary.Backend);
        AssertEx.False(binary.IsPinnedFallback);
    }

    [Test]
    public async Task EnsureBinary_BrokenOverride_ThrowsSanitized_NoFallThroughToAcquisition()
    {
        using var cache = new TempCacheDir();
        var overrideOptions = new StableDiffusionServerRuntimeOverrideOptions
        {
            ServerPath = Path.Combine(cache.Path, "does-not-exist-sd-server"),
            Backend = SdGpuBackend.Cuda
        };

        using var handler = new CountingHandler(static () =>
            throw new InvalidOperationException("A broken override must not fall through to a download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new StableDiffusionCppBinaryManager(http, cache.Path, StableDiffusionReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64, overrideOptions);

        await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => manager.EnsureBinaryAsync(SdGpuBackend.Cuda, CancellationToken.None));
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    private sealed class CountingHandler(Func<HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder());
        }
    }

    private sealed class TempCacheDir : IDisposable
    {
        public TempCacheDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-sdcpp-test-" + Guid.NewGuid().ToString("N"));
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
