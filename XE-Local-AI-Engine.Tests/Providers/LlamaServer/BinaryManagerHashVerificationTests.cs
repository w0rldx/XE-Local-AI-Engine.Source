namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Runtime.InteropServices;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Binary hash verification: a SHA256 mismatch is re-downloaded exactly once and then surfaced as a sanitized
///     failure; a cached binary is reused offline with no download. All HTTP is faked — no network.
/// </summary>
public sealed class BinaryManagerHashVerificationTests
{
    [Test]
    public async Task EnsureBinary_CorruptDownload_RetriesOnce_ThenSurfacesSanitizedFailure()
    {
        using var cache = new TempCacheDir();
        // The handler always returns bytes that cannot match the pinned SHA256 → forced corruption on every attempt.
        using var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("not-the-pinned-archive"u8.ToArray())
        });
        using var http = new HttpClient(handler, false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64);

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));

        // Re-download exactly once: two total attempts.
        AssertEx.Equal(2, handler.CallCount);
        // Sanitized surface — no internal absolute path leaks into the user-facing message.
        AssertEx.False(exception.Message.Contains(cache.Path, StringComparison.Ordinal));
        AssertEx.False(exception.Message.Contains(Path.GetTempPath(), StringComparison.Ordinal));
        AssertEx.Contains(exception.Message, "integrity", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task EnsureBinary_CachedBinaryPresent_ReusedOffline_NoDownload()
    {
        using var cache = new TempCacheDir();
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        var serverPath = Path.Combine(cache.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cpu", pin.ServerRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        await File.WriteAllTextAsync(serverPath, "fake-llama-server");

        using var handler = new CountingHandler(() =>
            throw new InvalidOperationException("Offline reuse must not hit the network."));
        using var http = new HttpClient(handler, false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(0, handler.CallCount);
        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.True(binary.IsPinnedFallback);
    }

    [Test]
    public async Task EnsureBinary_CachedBinaryInUpstreamTopLevelLayout_ResolvedByTreeSearch_NoDownload()
    {
        // Regression: the pinned b9692 Linux archive extracts llama-server to a top-level llama-{tag}/ folder,
        // NOT the pin's build/bin/ relative path. The manager must locate the executable by searching the extracted
        // tree, or acquisition silently fails with "did not contain the expected server executable".
        using var cache = new TempCacheDir();
        var variantDir = Path.Combine(cache.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cpu");
        var actualServerPath = Path.Combine(variantDir, $"llama-{LlamaCppReleasePins.PinnedTag}", "llama-server");
        Directory.CreateDirectory(Path.GetDirectoryName(actualServerPath)!);
        await File.WriteAllTextAsync(actualServerPath, "fake-llama-server");

        using var handler = new CountingHandler(() =>
            throw new InvalidOperationException("Offline reuse must not hit the network."));
        using var http = new HttpClient(handler, false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(0, handler.CallCount);
        AssertEx.Equal(actualServerPath, binary.ServerExecutablePath);
        AssertEx.True(binary.IsPinnedFallback);
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
                    Directory.Delete(Path, true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
