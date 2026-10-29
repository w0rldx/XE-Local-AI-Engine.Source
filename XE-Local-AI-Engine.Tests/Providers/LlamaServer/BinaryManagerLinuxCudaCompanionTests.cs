namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The Linux x64 CUDA prebuilt: its tar.gz cudart companion lands its <c>.so</c> libraries beside <c>llama-server</c>, and an
///     incomplete set fails clearly or is repaired. HTTP is faked; archives are small synthetic tar.gz files.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BinaryManagerLinuxCudaCompanionTests
{
    private const string MainTopFolder = "llama-b11803";
    private const string CompanionTopFolder = "cudart-llama-b11803-bin-ubuntu-cuda-12.8-x64";

    private static readonly string[] CompleteCompanionSet = ["libcudart.so.12", "libcublas.so.12", "libcublasLt.so.12"];

    [Test]
    public void LinuxCudaPin_IsWhatTheFixtureStandsIn()
    {
        var pin = AssertEx.NotNull(LlamaCppReleasePins.ResolveExactOrNull(OSPlatform.Linux, Architecture.X64, GpuVariant.Cuda));

        AssertEx.Equal("cudart-" + pin.AssetName, pin.CudartAssetName);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task EnsureBinary_FirstRunLinuxCuda_LandsTheServerAndTheThreeLibrariesBesideIt_OwnerOnly()
    {
        using var cache = new TempDirectory("xe-linux-cuda");
        using var fixture = new Fixture(cache.Path, CompleteCompanionSet);

        var binary = await fixture.Manager().EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(Path.Combine(fixture.VariantDir, MainTopFolder, "llama-server"), binary.ServerExecutablePath);
        AssertEx.Equal(GpuVariant.Cuda, binary.Variant);
        AssertEx.Equal($"{fixture.Pin.AssetName}, {fixture.Pin.CudartAssetName}", string.Join(", ", fixture.RequestedAssets));
        var serverDir = Path.GetDirectoryName(binary.ServerExecutablePath)!;
        foreach (var library in CompleteCompanionSet)
        {
            var landed = Path.Combine(serverDir, library);
            AssertEx.True(File.Exists(landed), $"{library} must sit next to the served server.");
            AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(landed));
        }

        AssertEx.False(Directory.Exists(Path.Combine(serverDir, CompanionTopFolder)), "The companion's top folder is stripped, never landed.");
        AssertEx.True((File.GetUnixFileMode(binary.ServerExecutablePath) & UnixFileMode.UserExecute) != 0, "The server keeps its execute bit.");
    }

    [Test]
    [Arguments("libcublas.so.12", "libcublas.so*")]
    [Arguments("libcublasLt.so.12", "libcublasLt.so*")]
    [Arguments("libcudart.so.12", "libcudart.so*")]
    public async Task EnsureBinary_FirstRunLinuxCuda_CompanionArchiveWithoutAFamily_FailsNamingItAndPublishesNothing(string omitted, string family)
    {
        using var cache = new TempDirectory("xe-linux-cuda");
        using var fixture = new Fixture(cache.Path, [.. CompleteCompanionSet.Where(library => library != omitted)]);

        var failure = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => fixture.Manager().EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None));

        AssertEx.Contains(failure.Message, $"no {family} next to the server", StringComparison.Ordinal);
        AssertEx.Contains(failure.Message, "Libraries found:", StringComparison.Ordinal);
        AssertEx.False(Directory.Exists(fixture.VariantDir), "An incomplete companion set must leave no variant dir behind.");
    }

    [Test]
    [Arguments("12")]
    [Arguments("13")]
    public async Task TryGetInstalled_LinuxCudaDir_ReadsInstalledOnlyWithEveryLibraryFamily(string cudaMajor)
    {
        using var cache = new TempDirectory("xe-linux-cuda");
        using var fixture = new Fixture(cache.Path, CompleteCompanionSet);
        var serverDir = await fixture.WritePublishedServerAsync([$"libcudart.so.{cudaMajor}", $"libcublas.so.{cudaMajor}"]);
        var manager = fixture.Manager();

        AssertEx.Null(await manager.FindInstalledBinaryAsync(GpuVariant.Cuda, CancellationToken.None));
        AssertEx.True(await manager.IsCompanionSetIncompleteAsync(GpuVariant.Cuda, CancellationToken.None));

        await File.WriteAllTextAsync(Path.Combine(serverDir, $"libcublasLt.so.{cudaMajor}"), "fake-cuda-runtime");

        AssertEx.NotNull(await manager.FindInstalledBinaryAsync(GpuVariant.Cuda, CancellationToken.None));
        AssertEx.False(await manager.IsCompanionSetIncompleteAsync(GpuVariant.Cuda, CancellationToken.None));
        AssertEx.Empty(fixture.RequestedAssets);
    }

    [Test]
    public async Task EnsureBinary_PublishedLinuxCudaDirMissingTheCublasFamilies_RepairsFromTheCompanionOnly()
    {
        using var cache = new TempDirectory("xe-linux-cuda");
        using var fixture = new Fixture(cache.Path, CompleteCompanionSet);
        var serverDir = await fixture.WritePublishedServerAsync(["libcudart.so.12", "libggml-cuda.so"]);
        var manager = fixture.Manager();

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None);

        AssertEx.Equal(serverDir, Path.GetDirectoryName(binary.ServerExecutablePath));
        AssertEx.Equal(fixture.Pin.CudartAssetName!, string.Join(", ", fixture.RequestedAssets));
        AssertEx.False(await manager.IsCompanionSetIncompleteAsync(GpuVariant.Cuda, CancellationToken.None));
        AssertEx.Empty(Directory.EnumerateFiles(serverDir, "*.partial"));
    }

    [Test]
    public void FlattenCompanion_OnLinux_LandsSharedObjectsOnlyAndCudartLast()
    {
        using var cache = new TempDirectory("xe-linux-cuda");
        var source = Directory.CreateDirectory(Path.Combine(cache.Path, "extracted", CompanionTopFolder)).FullName;
        foreach (var name in (string[])[.. CompleteCompanionSet, "LICENSE.txt", "cudart64_12.dll"])
        {
            File.WriteAllText(Path.Combine(source, name), name);
        }

        var serverDir = Path.Combine(cache.Path, "server");

        LlamaCppBinaryManager.FlattenCompanionInto(Path.Combine(cache.Path, "extracted"), serverDir, OSPlatform.Linux);

        AssertEx.Equal(string.Join(", ", CompleteCompanionSet.Order(StringComparer.Ordinal)),
            string.Join(", ", Directory.EnumerateFiles(serverDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)));
    }

    /// <summary>The real Linux x64 CUDA pin with its digests swapped for the synthetic archives', served by a recording HTTP fake.</summary>
    private sealed class Fixture : IDisposable
    {
        private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        private readonly string _cacheRoot;
        private readonly byte[] _companionArchive;
        private readonly RecordingHandler _handler;
        private readonly HttpClient _http;
        private readonly byte[] _mainArchive;

        public Fixture(string cacheRoot, string[] companionLibraries)
        {
            _cacheRoot = cacheRoot;
            var real = AssertEx.NotNull(LlamaCppReleasePins.ResolveExactOrNull(OSPlatform.Linux, Architecture.X64, GpuVariant.Cuda));
            _mainArchive = BuildTarGz(OwnerOnly | UnixFileMode.UserExecute, $"{MainTopFolder}/llama-server", $"{MainTopFolder}/libggml-cuda.so");
            _companionArchive = BuildTarGz(OwnerOnly, [.. companionLibraries.Select(library => $"{CompanionTopFolder}/{library}"), $"{CompanionTopFolder}/LICENSE.txt"]);
            Pin = new LlamaCppAssetPin
            {
                AssetName = real.AssetName,
                Sha256 = Sha256Hex(_mainArchive),
                ServerRelativePath = real.ServerRelativePath,
                CudartAssetName = real.CudartAssetName,
                CudartSha256 = Sha256Hex(_companionArchive)
            };
            VariantDir = Path.Combine(cacheRoot, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cuda");
            _handler = new RecordingHandler(Respond);
            _http = new HttpClient(_handler, disposeHandler: false);
        }

        public LlamaCppAssetPin Pin { get; }

        public string VariantDir { get; }

        public List<string> RequestedAssets { get; } = [];

        public LlamaCppBinaryManager Manager() =>
            new(_http,
                _cacheRoot,
                LlamaCppReleasePins.PinnedTag,
                OSPlatform.Linux,
                Architecture.X64,
                TimeProvider.System,
                pinResolver: (_, _, variant) => variant == GpuVariant.Cuda ? Pin : null);

        /// <summary>An already-published variant dir in upstream's layout: the server under its top folder plus <paramref name="libraries" /> beside it.</summary>
        public async Task<string> WritePublishedServerAsync(string[] libraries)
        {
            var serverDir = Directory.CreateDirectory(Path.Combine(VariantDir, MainTopFolder)).FullName;
            await File.WriteAllTextAsync(Path.Combine(serverDir, "llama-server"), "fake-llama-server");
            foreach (var library in libraries)
            {
                await File.WriteAllTextAsync(Path.Combine(serverDir, library), "fake-cuda-runtime");
            }

            return serverDir;
        }

        public void Dispose()
        {
            _http.Dispose();
            _handler.Dispose();
        }

        private static byte[] BuildTarGz(UnixFileMode mode, params string[] paths)
        {
            using var raw = new MemoryStream();
            using (var tar = new TarWriter(raw, leaveOpen: true))
            {
                foreach (var path in paths)
                {
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                    {
                        Mode = mode,
                        DataStream = new MemoryStream("#!/bin/sh\nexit 0\n"u8.ToArray())
                    });
                }
            }

            raw.Position = 0;
            using var gz = new MemoryStream();
            using (var gzip = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
            {
                raw.CopyTo(gzip);
            }

            return gz.ToArray();
        }

        private static string Sha256Hex(byte[] bytes) =>
            Convert.ToHexStringLower(SHA256.HashData(bytes));

        private HttpResponseMessage Respond(Uri uri)
        {
            var asset = uri.Segments[^1];
            RequestedAssets.Add(asset);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(asset == Pin.CudartAssetName ? _companionArchive : _mainArchive)
            };
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<Uri, HttpResponseMessage> _respond;

        public RecordingHandler(Func<Uri, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request.RequestUri!));
    }
}
