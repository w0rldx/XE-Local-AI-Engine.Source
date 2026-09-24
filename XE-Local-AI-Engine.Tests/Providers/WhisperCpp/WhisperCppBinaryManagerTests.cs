namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Brief tests 1 (the tampered byte) and 5 (the fail-closed override). The repo has no shared HTTP-handler fake, so
///     these drive a hand-written <see cref="HttpMessageHandler" /> serving a real in-memory archive — no network, no
///     real binary, and the OS/arch test constructor keeps asset selection deterministic on any host.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class WhisperCppBinaryManagerTests
{
    [Test]
    public async Task EnsureBinary_CorruptDownload_RetriesOnce_ThenSurfacesSanitizedFailure()
    {
        using var cache = new TempCacheDir();
        // Bytes that cannot match the pinned digest: forced corruption on every attempt.
        using var handler = new CountingHandler(static () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("not-the-pinned-archive"u8.ToArray())
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new WhisperCppBinaryManager(http, cache.Path, WhisperCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64);

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => manager.EnsureBinaryAsync(WhisperBackend.Cpu, CancellationToken.None));

        AssertEx.Equal(expected: 2, handler.CallCount, "A corrupt download must be re-downloaded exactly once.");
        AssertEx.False(exception.Message.Contains(cache.Path, StringComparison.Ordinal),
            "The operator-facing message must not leak the cache path.");
        AssertEx.False(exception.Message.Contains(Path.GetTempPath(), StringComparison.Ordinal),
            "The operator-facing message must not leak the temp path.");
        AssertEx.Contains(exception.Message, "integrity", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task EnsureBinary_TamperedByte_FailsVerification()
    {
        // The same pipeline as above, but the payload is a REAL archive whose single flipped byte breaks the digest —
        // which is what brief test 1 asks for: a valid-looking asset must still be rejected on its hash.
        using var cache = new TempCacheDir();
        var pin = WhisperCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, WhisperBackend.Cpu)!;
        var archive = BuildTarGz(pin.ServerRelativePath, "fake-whisper-server");
        archive[^1] ^= 0xFF;

        using var handler = new CountingHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(archive)
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new WhisperCppBinaryManager(http, cache.Path, WhisperCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64);

        await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => manager.EnsureBinaryAsync(WhisperBackend.Cpu, CancellationToken.None));

        AssertEx.False(Directory.Exists(Path.Combine(cache.Path, "whisper.cpp", WhisperCppReleasePins.PinnedTag, "cpu")),
            "A failed verification must leave no extracted backend directory behind.");
    }

    [Test]
    public async Task EnsureBinary_TarGzAsset_ExtractsAndResolvesServer()
    {
        // The Linux asset is a .tar.gz, unlike every stable-diffusion.cpp asset. The manager must extract it and find
        // whisper-server at the pinned path inside the single top-level directory.
        using var cache = new TempCacheDir();
        var pin = WhisperCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, WhisperBackend.Cpu)!;
        var archive = BuildTarGz(pin.ServerRelativePath, "fake-whisper-server");

        using var handler = new DigestPinningHandler(archive);
        using var http = new HttpClient(handler, disposeHandler: false);
        // The pin carries the digest of the archive this test just built, so verification stays ENABLED rather than
        // being bypassed: the production digests are the real upstream assets' and no synthetic payload can match one.
        var manager = new WhisperCppBinaryManager(http,
            cache.Path,
            WhisperCppReleasePins.PinnedTag,
            OSPlatform.Linux,
            Architecture.X64,
            overrideOptions: null,
            installedRuntimeStore: null,
            managedSourceSignal: null,
            new WhisperAssetPin
            {
                AssetName = pin.AssetName,
                Sha256 = handler.Digest,
                ServerRelativePath = pin.ServerRelativePath,
                ArchiveKind = WhisperArchiveKind.TarGz
            });

        var binary = await manager.EnsureBinaryAsync(WhisperBackend.Cpu, CancellationToken.None);

        AssertEx.True(File.Exists(binary.ServerExecutablePath), "The extracted server executable must exist on disk.");
        AssertEx.Contains(binary.ServerExecutablePath, "whisper-server", StringComparison.Ordinal);
        AssertEx.Equal(WhisperBackend.Cpu, binary.Backend);
        AssertEx.True(binary.IsPinnedFallback);
        AssertEx.Equal(WhisperCppReleasePins.PinnedTag, binary.Version);
    }

    [Test]
    public async Task EnsureBinary_CachedBinaryPresent_ReusedOffline_NoDownload()
    {
        using var cache = new TempCacheDir();
        var pin = WhisperCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, WhisperBackend.Cpu)!;
        var serverPath = Path.Combine(cache.Path, "whisper.cpp", WhisperCppReleasePins.PinnedTag, "cpu", pin.ServerRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        await File.WriteAllTextAsync(serverPath, "fake-whisper-server");

        using var handler = new CountingHandler(static () =>
            throw new InvalidOperationException("Offline reuse must not hit the network."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new WhisperCppBinaryManager(http, cache.Path, WhisperCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64);

        var binary = await manager.EnsureBinaryAsync(WhisperBackend.Cpu, CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.True(binary.IsPinnedFallback);
    }

    [Test]
    public async Task EnsureBinary_BringYourOwnOverride_ServesConfiguredBinary_NoDownload()
    {
        using var cache = new TempCacheDir();
        var byoPath = Path.Combine(cache.Path, "byo-whisper-server");
        await File.WriteAllTextAsync(byoPath, "operator-built-whisper-server");
        MakeExecutable(byoPath);

        var overrideOptions = new WhisperServerRuntimeOverrideOptions
        {
            ServerPath = byoPath,
            Backend = WhisperBackend.Cuda
        };

        using var handler = new CountingHandler(static () =>
            throw new InvalidOperationException("An active override must not hit the network."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new WhisperCppBinaryManager(http, cache.Path, WhisperCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64, overrideOptions);

        // Even when the caller asks for CPU, an active override serves its OWN backend and path.
        var binary = await manager.EnsureBinaryAsync(WhisperBackend.Cpu, CancellationToken.None);

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Equal(byoPath, binary.ServerExecutablePath);
        AssertEx.Equal(WhisperBackend.Cuda, binary.Backend);
        AssertEx.False(binary.IsPinnedFallback);
        AssertEx.Equal("byo", binary.Version);
    }

    [Test]
    public async Task EnsureBinary_BrokenOverride_ThrowsSanitized_NoFallThroughToAcquisition()
    {
        using var cache = new TempCacheDir();
        var overrideOptions = new WhisperServerRuntimeOverrideOptions
        {
            ServerPath = Path.Combine(cache.Path, "does-not-exist-whisper-server"),
            Backend = WhisperBackend.Cuda
        };

        using var handler = new CountingHandler(static () =>
            throw new InvalidOperationException("A broken override must not fall through to a download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new WhisperCppBinaryManager(http, cache.Path, WhisperCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64, overrideOptions);

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => manager.EnsureBinaryAsync(WhisperBackend.Cuda, CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount, "A broken override must never reach acquisition.");
        AssertEx.Contains(exception.Message, "does not point to an existing file", StringComparison.Ordinal);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task EnsureBinary_OverrideNotExecutable_ThrowsSanitized()
    {
        // A path that exists but cannot be executed would otherwise surface much later as an opaque spawn failure.
        using var cache = new TempCacheDir();
        var byoPath = Path.Combine(cache.Path, "not-executable-whisper-server");
        await File.WriteAllTextAsync(byoPath, "operator-built-whisper-server");
        File.SetUnixFileMode(byoPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        using var handler = new CountingHandler(static () =>
            throw new InvalidOperationException("A broken override must not fall through to a download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new WhisperCppBinaryManager(http,
            cache.Path,
            WhisperCppReleasePins.PinnedTag,
            OSPlatform.Linux,
            Architecture.X64,
            new WhisperServerRuntimeOverrideOptions
            {
                ServerPath = byoPath,
                Backend = WhisperBackend.Cuda
            });

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => manager.EnsureBinaryAsync(WhisperBackend.Cuda, CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Contains(exception.Message, "not executable", StringComparison.Ordinal);
    }

    [Test]
    public async Task EnsureBinary_TombstonedManagedRecord_Throws_NeverFallsBackToPrebuilt()
    {
        using var cache = new TempCacheDir();
        var store = new StubInstalledRuntimeStore(new WhisperInstalledRuntimeState(WhisperInstalledRuntimeValidity.Invalid,
            WhisperBackend.Cuda,
            WhisperCppSourceBuildRequestValidation.OfficialRepository,
            WhisperCppReleasePins.PinnedSourceCommitSha,
            WhisperCppSourceSelection.Official,
            WhisperCppSourceRevisionMode.EnginePinned,
            SourceRequestedCommit: null,
            SourceBuildPath: null,
            ServerSha256: null,
            DateTimeOffset.UtcNow,
            "The managed runtime binary failed integrity verification."));

        using var handler = new CountingHandler(static () =>
            throw new InvalidOperationException("A tombstoned managed record must never fall back to a prebuilt."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new WhisperCppBinaryManager(http,
            cache.Path,
            WhisperCppReleasePins.PinnedTag,
            OSPlatform.Linux,
            Architecture.X64,
            overrideOptions: null,
            store);

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => manager.EnsureBinaryAsync(WhisperBackend.Cuda, CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Contains(exception.Message, "rebuilt or removed", StringComparison.Ordinal);
    }

    [Test]
    public async Task EnsureBinary_ManagedRecordForAnotherBackend_Throws_NeverServesContradictingBytes()
    {
        using var cache = new TempCacheDir();
        var store = new StubInstalledRuntimeStore(new WhisperInstalledRuntimeState(WhisperInstalledRuntimeValidity.Active,
            WhisperBackend.Cuda,
            WhisperCppSourceBuildRequestValidation.OfficialRepository,
            WhisperCppReleasePins.PinnedSourceCommitSha,
            WhisperCppSourceSelection.Official,
            WhisperCppSourceRevisionMode.EnginePinned,
            SourceRequestedCommit: null,
            SourceBuildPath: Path.Combine(cache.Path, "managed", "bin"),
            ServerSha256: new string('a', 64),
            DateTimeOffset.UtcNow));

        using var handler = new CountingHandler(static () =>
            throw new InvalidOperationException("A managed record must never fall back to a prebuilt."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new WhisperCppBinaryManager(http,
            cache.Path,
            WhisperCppReleasePins.PinnedTag,
            OSPlatform.Linux,
            Architecture.X64,
            overrideOptions: null,
            store);

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => manager.EnsureBinaryAsync(WhisperBackend.Cpu, CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Contains(exception.Message, "backend is unavailable", StringComparison.Ordinal);
    }

    [Test]
    public async Task EnsureBinary_ManagedRecordWithAMissingBinary_IsTombstonedAndTheSignalCleared()
    {
        using var cache = new TempCacheDir();
        var store = new StubInstalledRuntimeStore(new WhisperInstalledRuntimeState(WhisperInstalledRuntimeValidity.Active,
            WhisperBackend.Cuda,
            WhisperCppSourceBuildRequestValidation.OfficialRepository,
            WhisperCppReleasePins.PinnedSourceCommitSha,
            WhisperCppSourceSelection.Official,
            WhisperCppSourceRevisionMode.EnginePinned,
            SourceRequestedCommit: null,
            SourceBuildPath: Path.Combine(cache.Path, "managed", "bin"),
            ServerSha256: new string('a', 64),
            DateTimeOffset.UtcNow));
        var signal = new WhisperManagedSourceBuildSignal();
        signal.SetActive(WhisperBackend.Cuda);

        using var handler = new CountingHandler(static () =>
            throw new InvalidOperationException("A managed record must never fall back to a prebuilt."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new WhisperCppBinaryManager(http,
            cache.Path,
            WhisperCppReleasePins.PinnedTag,
            OSPlatform.Linux,
            Architecture.X64,
            overrideOptions: null,
            store,
            signal);

        await AssertEx.ThrowsAsync<WhisperRuntimeException>(() => manager.EnsureBinaryAsync(WhisperBackend.Cuda, CancellationToken.None));

        AssertEx.Equal(WhisperInstalledRuntimeValidity.Invalid, AssertEx.NotNull(store.LastWritten).Validity,
            "A managed runtime whose binary cannot be validated must be tombstoned, not silently ignored.");
        AssertEx.Null(signal.ActiveBackend,
            "The in-memory signal must stop advertising a runtime the manager has just proven unusable.");
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>Builds a real .tar.gz containing one entry at <paramref name="entryPath" />.</summary>
    private static byte[] BuildTarGz(string entryPath, string content)
    {
        var payload = new MemoryStream();
        using (var gzip = new GZipStream(payload, CompressionMode.Compress, leaveOpen: true))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, entryPath)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                };
                tar.WriteEntry(entry);
            }

        return payload.ToArray();
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responder;

        public CountingHandler(Func<HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder());
        }
    }

    /// <summary>
    ///     Serves a fixed archive AND reports its real digest, so the manager's verification can succeed without this
    ///     test having to know the pinned production hash of a payload it just built.
    /// </summary>
    private sealed class DigestPinningHandler : HttpMessageHandler
    {
        private readonly byte[] _archive;

        public DigestPinningHandler(byte[] archive)
        {
            _archive = archive;
            Digest = Convert.ToHexStringLower(SHA256.HashData(archive));
        }

        public string Digest { get; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_archive)
            });
        }
    }

    private sealed class StubInstalledRuntimeStore : IWhisperInstalledRuntimeStore
    {
        private WhisperInstalledRuntimeState? _state;

        public StubInstalledRuntimeStore(WhisperInstalledRuntimeState? state)
        {
            _state = state;
        }

        public WhisperInstalledRuntimeState? LastWritten { get; private set; }

        public Task<WhisperInstalledRuntimeState?> ReadAsync(CancellationToken ct) =>
            Task.FromResult(_state);

        public Task WriteAsync(WhisperInstalledRuntimeState state, CancellationToken ct)
        {
            LastWritten = state;
            _state = state;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken ct)
        {
            _state = null;
            return Task.CompletedTask;
        }
    }

    private sealed class TempCacheDir : IDisposable
    {
        public TempCacheDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-whispercpp-test-" + Guid.NewGuid().ToString("N"));
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
