namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using NSubstitute;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ILlamaCppBinaryManager.TryGetInstalledBinaryAsync" />: the READ-ONLY resolve behind the device
///     diagnostics. It answers with a binary only when one is already on disk, and otherwise reports "none" — it must
///     never download, never create the cache tree, and never write <c>installed-runtime.json</c>, because it runs from
///     a plain GET the SPA fires on every authenticated page. The recovery direction matters as much as the refusal: the
///     moment something else installs a runtime, the very next lookup must see it (no negative is memoized here).
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BinaryManagerInstalledLookupTests
{
    [Test]
    public async Task TryGetInstalledBinary_NothingOnDisk_ReturnsNull_WithoutNetworkOrCacheWrites()
    {
        using var cache = new TempCacheDir();
        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var store = Substitute.For<IInstalledRuntimeStore>();
        store.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<InstalledRuntimeState?>(null));
        var manager = Manager(http, cache.Path, store);

        var binary = await manager.TryGetInstalledBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Null(binary);
        AssertEx.Equal(expected: 0, handler.CallCount);

        // The cache root is untouched: no {cacheRoot}/llama.cpp/... tree was materialized by merely asking.
        AssertEx.Equal(expected: 0, Directory.GetFileSystemEntries(cache.Path).Length);

        // And the record is read, never written — a diagnostics GET must not mutate installed-runtime.json.
        await store.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }

    [Test]
    public async Task TryGetInstalledBinary_CachedBinaryPresent_ResolvesIt_WithoutNetwork()
    {
        using var cache = new TempCacheDir();
        var serverPath = WritePinnedCpuServer(cache.Path);
        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = Manager(http, cache.Path, store: null);

        var binary = AssertEx.NotNull(await manager.TryGetInstalledBinaryAsync(GpuVariant.Cpu, CancellationToken.None));

        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.Equal(LlamaCppReleasePins.PinnedTag, binary.Version);
        AssertEx.True(binary.IsPinnedFallback);
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task TryGetInstalledBinary_AfterTheRuntimeIsInstalled_SeesItOnTheVeryNextCall()
    {
        // The install-after-unknown sequence, at the layer that decides it: the first lookup finds nothing (which is
        // what makes the device audit report "unknown"), and the lookup made after an install — by provisioning, the
        // ensure/select endpoint, or a source-build adoption — resolves the new binary. Nothing caches the "none".
        using var cache = new TempCacheDir();
        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = Manager(http, cache.Path, store: null);

        AssertEx.Null(await manager.TryGetInstalledBinaryAsync(GpuVariant.Cpu, CancellationToken.None));

        var serverPath = WritePinnedCpuServer(cache.Path);

        var binary = AssertEx.NotNull(await manager.TryGetInstalledBinaryAsync(GpuVariant.Cpu, CancellationToken.None));
        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task TryGetInstalledBinary_NoPrebuiltForTheHostVariant_ReturnsNull_NotAThrow()
    {
        // Linux has no prebuilt CUDA asset upstream, where an ensure raises its sanitized "no prebuilt" refusal. The
        // lookup must simply report "none installed" instead, so a page-load diagnostic degrades rather than erroring.
        using var cache = new TempCacheDir();
        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = Manager(http, cache.Path, store: null);

        AssertEx.Null(await manager.TryGetInstalledBinaryAsync(GpuVariant.Cuda, CancellationToken.None));
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    private static LlamaCppBinaryManager Manager(HttpClient http, string cacheRoot, IInstalledRuntimeStore? store)
    {
        // No catalog: the lookup must not consult the live release API either (that is a network call, and it can only
        // ever pick a tag to ACQUIRE — it cannot make a binary appear on disk).
        return new LlamaCppBinaryManager(http,
            cacheRoot,
            LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux,
            Architecture.X64,
            TimeProvider.System,
            catalog: null,
            store);
    }

    private static string WritePinnedCpuServer(string cacheRoot)
    {
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        var serverPath = Path.Combine(cacheRoot,
            "llama.cpp",
            LlamaCppReleasePins.PinnedTag,
            "cpu",
            pin.ServerRelativePath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        File.WriteAllText(serverPath, "fake-llama-server");
        return serverPath;
    }

    /// <summary>Any outbound request at all fails the test — "never downloads" is the claim under test.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException($"The installed-binary lookup must never touch the network (requested {request.RequestUri}).");
        }
    }

    private sealed class TempCacheDir : IDisposable
    {
        public TempCacheDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-llama-lookup-" + Guid.NewGuid().ToString("N"));
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
