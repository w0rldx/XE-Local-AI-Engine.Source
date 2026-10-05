namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>A Windows-CUDA acquisition publishes its variant dir only once the cudart companion DLLs are inside it.</summary>
/// <remarks>
///     The defect this pins: the build was moved into place first and cudart flattened in afterwards, so a device probe in
///     that window ran a CUDA build that could load no CUDA backend, got an empty GPU list and cached it for the whole
///     session. The HTTP fake records, at the moment the cudart archive is requested, whether the variant dir is
///     already visible. All HTTP is faked — no network.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class BinaryManagerCudartAtomicPublishTests
{
    private const string Tag = "b9799";

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task InstallTag_WindowsCuda_PublishesTheBuildOnlyTogetherWithItsCudartDlls()
    {
        // POSIX only: the post-install smoke test spawns the extracted llama-server.exe, a shell stub with an exec bit.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: true);
        var manager = fixture.Manager();

        var binary = await manager.InstallTagAsync(Tag, fixture.Pin.AssetName, Sha256Hex(fixture.MainArchive), fixture.MainArchive.LongLength, GpuVariant.Cuda,
            CancellationToken.None);

        AssertEx.Equal(expected: 1, fixture.VariantDirVisibleAtCudartRequest.Count);
        AssertEx.False(fixture.VariantDirVisibleAtCudartRequest[0], "The variant dir must not be published before its cudart DLLs are in place.");
        AssertEx.True(binary.ServerExecutablePath.StartsWith(fixture.VariantDir, StringComparison.Ordinal), "The served binary lives in the published variant dir.");
        AssertEx.True(File.Exists(Path.Combine(Path.GetDirectoryName(binary.ServerExecutablePath)!, "cudart64_12.dll")),
            "The cudart DLL sits next to the served server.");
        AssertEx.Empty(fixture.StagingLeftovers());
    }

    [Test]
    public async Task InstallTag_WindowsCuda_CudartFailure_NeverPublishesAHalfCudaDir()
    {
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: false);
        var manager = fixture.Manager();

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync(Tag,
            fixture.Pin.AssetName,
            Sha256Hex(fixture.MainArchive),
            fixture.MainArchive.LongLength,
            GpuVariant.Cuda,
            CancellationToken.None));

        // Retried once (two cudart requests), and the variant dir was invisible during both and does not exist after.
        AssertEx.Equal(expected: 2, fixture.VariantDirVisibleAtCudartRequest.Count);
        AssertEx.False(fixture.VariantDirVisibleAtCudartRequest.Any(visible => visible), "The variant dir must not be published before its cudart DLLs are in place.");
        AssertEx.False(Directory.Exists(fixture.VariantDir), "A cudart failure must leave no variant dir behind.");
        AssertEx.Empty(fixture.StagingLeftovers());
    }

    [Test]
    public async Task EnsureBinary_FirstRunWindowsCuda_PublishesTheBuildOnlyTogetherWithItsCudartDlls()
    {
        // The pinned first-run path (no catalog, no record): no smoke test spawns here, so this runs on every OS.
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: true, LlamaCppReleasePins.PinnedTag);

        var binary = await fixture.PinnedManager().EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(expected: 1, fixture.VariantDirVisibleAtCudartRequest.Count);
        AssertEx.False(fixture.VariantDirVisibleAtCudartRequest[0], "The variant dir must not be published before its cudart DLLs are in place.");
        AssertEx.True(binary.ServerExecutablePath.StartsWith(fixture.VariantDir, StringComparison.Ordinal), "The served binary lives in the published variant dir.");
        AssertEx.True(File.Exists(Path.Combine(Path.GetDirectoryName(binary.ServerExecutablePath)!, "cudart64_12.dll")),
            "The cudart DLL sits next to the served server.");
        AssertEx.Empty(fixture.StagingLeftovers());
    }

    [Test]
    public async Task EnsureBinary_FirstRunWindowsCuda_CudartFailure_NeverPublishesAHalfCudaDir()
    {
        using var cache = new TempDir();
        using var fixture = new Fixture(cache.Path, cudartServed: false, LlamaCppReleasePins.PinnedTag);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => fixture.PinnedManager().EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None));

        AssertEx.Equal(expected: 2, fixture.VariantDirVisibleAtCudartRequest.Count);
        AssertEx.False(fixture.VariantDirVisibleAtCudartRequest.Any(visible => visible), "The variant dir must not be published before its cudart DLLs are in place.");
        AssertEx.False(Directory.Exists(fixture.VariantDir), "A cudart failure must leave no variant dir behind.");
        AssertEx.Empty(fixture.StagingLeftovers());
    }

    [Test]
    public async Task FlattenDlls_ATopUpThatFailsMidway_NeverLeavesTheCudartMarkerBehind()
    {
        // The cached-dir top-up flattens into a PUBLISHED dir, where the cudart64_* marker reads as "installed" and "paired". A recursive enumeration yields
        // a dir's own files before its subdirs', so an enumeration-order copy lands the root-level marker before the nested DLL fails, on any filesystem.
        using var cache = new TempDir();
        var source = Directory.CreateDirectory(Path.Combine(cache.Path, "extracted", "bin")).FullName;
        await File.WriteAllTextAsync(Path.Combine(cache.Path, "extracted", "cudart64_12.dll"), "fake-cuda-runtime");
        await File.WriteAllTextAsync(Path.Combine(source, "nvrtc64_120_0.dll"), "fake-nvrtc");
        var serverDir = Directory.CreateDirectory(Path.Combine(cache.Path, "server")).FullName;
        Directory.CreateDirectory(Path.Combine(serverDir, "nvrtc64_120_0.dll"));

        AssertEx.Throws<SystemException>(() => LlamaCppBinaryManager.FlattenDllsInto(Path.Combine(cache.Path, "extracted"), serverDir));

        AssertEx.Empty(Directory.EnumerateFiles(serverDir, "cudart64_*.dll"));
        AssertEx.Empty(Directory.EnumerateFiles(serverDir, "*.partial"));
    }

    [Test]
    public async Task PublishStagedVariant_OverAnExistingVariantDir_ReplacesItAndLeavesNoSiblingBehind()
    {
        using var cache = new TempDir();
        var variantDir = Path.Combine(cache.Path, "llama.cpp", Tag, "cuda");
        Directory.CreateDirectory(variantDir);
        await File.WriteAllTextAsync(Path.Combine(variantDir, "old.dll"), "old");
        var stagingDir = Directory.CreateDirectory($"{variantDir}.staged.tmp").FullName;
        await File.WriteAllTextAsync(Path.Combine(stagingDir, "new.dll"), "new");

        LlamaCppBinaryManager.PublishStagedVariant(stagingDir, variantDir);

        AssertEx.Equal(expected: "new", await File.ReadAllTextAsync(Path.Combine(variantDir, "new.dll")));
        AssertEx.False(File.Exists(Path.Combine(variantDir, "old.dll")), "The old contents must be fully replaced.");
        AssertEx.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(variantDir)!, "cuda.*.tmp"));
    }

    [Test]
    public async Task PublishStagedVariant_WhenTheMoveIntoPlaceFails_LeavesTheExistingVariantDirIntact()
    {
        // A missing staging dir makes the move into place fail after the old dir was already dealt with: it must come back untouched.
        using var cache = new TempDir();
        var variantDir = Path.Combine(cache.Path, "llama.cpp", Tag, "cuda");
        Directory.CreateDirectory(Path.Combine(variantDir, "build", "bin"));
        await File.WriteAllTextAsync(Path.Combine(variantDir, "build", "bin", "llama-server.exe"), "old-server");
        await File.WriteAllTextAsync(Path.Combine(variantDir, "build", "bin", "cudart64_12.dll"), "old-cudart");

        AssertEx.Throws<IOException>(() => LlamaCppBinaryManager.PublishStagedVariant($"{variantDir}.missing.tmp", variantDir));

        AssertEx.Equal(expected: "old-server", await File.ReadAllTextAsync(Path.Combine(variantDir, "build", "bin", "llama-server.exe")));
        AssertEx.Equal(expected: "old-cudart", await File.ReadAllTextAsync(Path.Combine(variantDir, "build", "bin", "cudart64_12.dll")));
        AssertEx.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(variantDir)!, "cuda.*.tmp"));
    }

    private static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>Windows-CUDA install inputs plus an HTTP fake that snapshots variant-dir visibility per cudart request.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly ScriptedHandler _handler;
        private readonly HttpClient _http;
        private readonly string _cacheRoot;
        private readonly bool _cudartServed;
        private readonly string _cudartName;
        private readonly byte[] _cudartArchive = BuildZip("cudart64_12.dll", "fake-cuda-runtime", executable: false);

        public Fixture(string cacheRoot, bool cudartServed, string tag = Tag)
        {
            _cacheRoot = cacheRoot;
            _cudartServed = cudartServed;
            Pin = AssertEx.NotNull(LlamaCppReleasePins.TryResolveExact(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda));
            _cudartName = AssertEx.NotNull(LlamaCppReleasePins.DeriveCudartAssetName(Pin.AssetName));
            MainArchive = BuildZip(Pin.ServerRelativePath, "#!/bin/sh\necho 'version: b9799'\nexit 0\n", executable: true);
            VariantDir = Path.Combine(cacheRoot, "llama.cpp", tag, "cuda");
            _handler = new ScriptedHandler(Respond);
            _http = new HttpClient(_handler, disposeHandler: false);
        }

        public LlamaCppAssetPin Pin { get; }

        public byte[] MainArchive { get; }

        public string VariantDir { get; }

        public List<bool> VariantDirVisibleAtCudartRequest { get; } = [];

        public LlamaCppBinaryManager Manager()
        {
            return new LlamaCppBinaryManager(_http,
                _cacheRoot,
                LlamaCppReleasePins.PinnedTag,
                OSPlatform.Windows,
                Architecture.X64,
                TimeProvider.System,
                new CompanionCatalog(_cudartName, Sha256Hex(_cudartArchive), _cudartArchive.LongLength));
        }

        // The real Windows-CUDA pin with its digests swapped for the fake archives', so the pinned path verifies them.
        public LlamaCppBinaryManager PinnedManager()
        {
            var pin = new LlamaCppAssetPin
            {
                AssetName = Pin.AssetName,
                Sha256 = Sha256Hex(MainArchive),
                ServerRelativePath = Pin.ServerRelativePath,
                CudartAssetName = _cudartName,
                CudartSha256 = Sha256Hex(_cudartArchive)
            };
            return new LlamaCppBinaryManager(_http,
                _cacheRoot,
                LlamaCppReleasePins.PinnedTag,
                OSPlatform.Windows,
                Architecture.X64,
                TimeProvider.System,
                pinResolver: (_, _, _) => pin);
        }

        public void Dispose()
        {
            _http.Dispose();
            _handler.Dispose();
        }

        public IEnumerable<string> StagingLeftovers()
        {
            var parent = Path.GetDirectoryName(VariantDir)!;
            return Directory.Exists(parent) ? Directory.EnumerateDirectories(parent, "cuda.*.tmp") : [];
        }

        private static byte[] BuildZip(string path, string content, bool executable)
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry(path);
                if (executable)
                {
                    // Unix mode 0755 in the high word; extraction on POSIX restores it so the smoke test can exec the stub.
                    entry.ExternalAttributes = Convert.ToInt32("755", 8) << 16;
                }

                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }

            return buffer.ToArray();
        }

        private HttpResponseMessage Respond(Uri uri)
        {
            if (!uri.AbsoluteUri.Contains(_cudartName, StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MainArchive) };
            }

            VariantDirVisibleAtCudartRequest.Add(Directory.Exists(VariantDir));
            return _cudartServed
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_cudartArchive) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>Resolves only the cudart companion; every other lookup reports no live data.</summary>
    private sealed class CompanionCatalog : ILlamaCppReleaseCatalog
    {
        private readonly string _asset;
        private readonly string _digest;
        private readonly long _size;

        public CompanionCatalog(string asset, string digest, long size)
        {
            _asset = asset;
            _digest = digest;
            _size = size;
        }

        public Task<LlamaCppReleaseResult> ResolveRecommendedAsync(string recommendedTag, CancellationToken ct) => Task.FromResult(LlamaCppReleaseResult.Offline());

        public Task<LlamaCppReleaseResult> ResolveUpstreamLatestAsync(CancellationToken ct) => Task.FromResult(LlamaCppReleaseResult.Offline());

        public Task<LlamaCppReleaseResult> ResolveAssetAsync(string tag, OSPlatform os, Architecture arch, GpuVariant variant, CancellationToken ct) =>
            Task.FromResult(LlamaCppReleaseResult.Offline());

        public Task<LlamaCppReleaseResult> ResolveCompanionAssetAsync(string tag, string assetName, CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.ForAsset(tag,
                new LlamaCppReleaseAsset
                {
                    Name = _asset,
                    DownloadUrl = LlamaCppReleasePins.DownloadUri(tag, _asset),
                    Digest = _digest,
                    Size = _size
                }));
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<Uri, HttpResponseMessage> _responder;

        public ScriptedHandler(Func<Uri, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request.RequestUri!));
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-cudart-publish-" + Guid.NewGuid().ToString("N"));
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
