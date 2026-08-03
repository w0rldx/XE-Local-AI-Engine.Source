namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="LlamaCppBinaryManager" /> runtime-acquisition reporting: the download → verify → extract lifecycle is
///     announced through <see cref="IRuntimeAcquisitionStatusRegistry" /> with monotonic bytes and a sequenced,
///     sanitized terminal status — and stays completely silent when nothing was actually acquired. All HTTP is faked —
///     no network.
/// </summary>
public sealed class RuntimeAcquisitionProgressTests
{
    private const string UpgradeTag = "b9799";

    [Test]
    public async Task EnsureBinary_WhileDownloading_ReportsMonotonicBytesAgainstTheContentLengthTotal()
    {
        using var cache = new TempCacheDir();
        // Bytes that cannot match the pinned SHA256, but enough of them to span many 81 920-byte read-loop iterations.
        var body = new byte[512 * 1024];
        using var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RecordingRegistry();
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64,
            acquisitionStatus: registry);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));

        // Only the FIRST download attempt: the pipeline retries once on a hash mismatch, and the retry legitimately
        // restarts the byte counter from zero.
        var firstAttempt = registry.Writes
                                   .TakeWhile(status => status.Phase != nameof(RuntimeAcquisitionPhase.Verifying))
                                   .Where(status => status.CompletedBytes is not null)
                                   .Select(status => status.CompletedBytes!.Value)
                                   .ToList();

        AssertEx.NotEmpty(firstAttempt);
        AssertEx.True(firstAttempt.Zip(firstAttempt.Skip(1)).All(pair => pair.Second > pair.First),
            "Byte progress must be strictly monotonic within one download attempt.");
        AssertEx.Equal(body.LongLength, firstAttempt[^1]);
        // The total is the response Content-Length, so the UI can render a determinate bar.
        AssertEx.True(registry.Writes.Where(status => status.CompletedBytes is not null).All(status => status.TotalBytes == body.LongLength),
            "Every byte update must carry the Content-Length total.");
    }

    [Test]
    public async Task EnsureBinary_ReportsTheFullLifecycle_WithStrictlyIncreasingSequences()
    {
        using var cache = new TempCacheDir();
        using var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("not-the-pinned-archive"u8.ToArray())
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RecordingRegistry();
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64,
            acquisitionStatus: registry);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));

        // The sequence is the client's ONLY defense against a hydrate response racing a push, so it must advance on
        // every single write — never repeat, never reset.
        var sequences = registry.Writes.Select(status => status.Sequence).ToList();
        AssertEx.True(sequences.Zip(sequences.Skip(1)).All(pair => pair.Second > pair.First),
            "Every status write must be stamped with a strictly greater sequence.");

        // Downloading is announced before the request (so the header wait is narrated) and Verifying before the hash.
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Downloading), registry.Writes[0].Phase);
        AssertEx.Contains(registry.Writes, status => status.Phase == nameof(RuntimeAcquisitionPhase.Verifying));
        // A single-archive acquisition never claims a second step.
        AssertEx.True(registry.Writes.All(status => status is { StepIndex: 1, StepCount: 1 }),
            "A Linux CPU acquisition fetches exactly one archive.");
    }

    [Test]
    public async Task EnsureBinary_WhenVerificationFails_ReportsTerminalFailed_WithASanitizedReason()
    {
        using var cache = new TempCacheDir();
        using var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("not-the-pinned-archive"u8.ToArray())
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RecordingRegistry();
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64,
            acquisitionStatus: registry);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));

        var terminal = registry.Writes[^1];
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), terminal.Phase);
        AssertEx.Contains(terminal.SanitizedError, "integrity", StringComparison.OrdinalIgnoreCase);
        // The failure text reaches an operator UI, so it must never carry an internal path.
        AssertEx.False(terminal.SanitizedError!.Contains(cache.Path, StringComparison.Ordinal));
        AssertEx.False(terminal.SanitizedError.Contains(Path.GetTempPath(), StringComparison.Ordinal));
    }

    [Test]
    public async Task EnsureBinary_WhenDownloadFails_ReportsTerminalFailed_WithNoTransportDetail()
    {
        using var cache = new TempCacheDir();
        // A transport failure, not a LlamaRuntimeException: its message is NOT sanitized by contract, so it must be
        // collapsed to a generic reason rather than surfaced verbatim (it names the host).
        using var handler = new ScriptedHandler(() => throw new HttpRequestException("connect failed to https://internal.example.invalid/secret-path"));
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RecordingRegistry();
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64,
            acquisitionStatus: registry);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));

        var terminal = registry.Writes[^1];
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), terminal.Phase);
        AssertEx.False(terminal.SanitizedError!.Contains("internal.example.invalid", StringComparison.OrdinalIgnoreCase));
        AssertEx.False(terminal.SanitizedError.Contains("secret-path", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task EnsureBinary_WhenCancelled_ReportsNoTerminalStatus()
    {
        // The supervisor passes a REQUEST-scoped token into EnsureBinaryAsync, so abandoning a chat mid-download must
        // not persist a terminal Failed: the banner would stick on a network diagnosis for something that never broke,
        // behind a retry attached to a non-failure. Host shutdown (the first-run service's stoppingToken) is the same.
        using var cache = new TempCacheDir();
        using var cts = new CancellationTokenSource();
        using var handler = new CancellingHandler(cts);
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RecordingRegistry();
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64,
            acquisitionStatus: registry);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => manager.EnsureBinaryAsync(GpuVariant.Cpu, cts.Token));

        // Acquisition DID start, so the guard being tested is the cancellation filter — not the "reported nothing" one.
        AssertEx.NotEmpty(registry.Writes);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Downloading), registry.Writes[^1].Phase);
        AssertEx.False(registry.Writes.Any(status => status.Phase is nameof(RuntimeAcquisitionPhase.Failed) or nameof(RuntimeAcquisitionPhase.Completed)),
            "A cancelled acquisition must never report a terminal status.");
    }

    [Test]
    public async Task EnsureBinary_WithNoRegistry_BehavesExactlyAsBefore()
    {
        // Invariant: the registry is an OPTIONAL trailing ctor parameter, so a provider-only / headless host that omits
        // it must keep the retry-once + sanitized-failure contract byte-for-byte.
        using var cache = new TempCacheDir();
        using var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("not-the-pinned-archive"u8.ToArray())
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64);

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));

        AssertEx.Equal(expected: 2, handler.CallCount);
        AssertEx.Contains(exception.Message, "integrity", StringComparison.OrdinalIgnoreCase);
    }

    [Test]
    public async Task EnsureBinary_CacheHitServe_ReportsNothing()
    {
        // This path runs on EVERY model spawn. A Completed here would push a terminal status per spawn and make the
        // banner flicker on a warm cache, so an acquisition that acquired nothing must stay entirely silent.
        using var cache = new TempCacheDir();
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        var serverPath = Path.Combine(cache.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cpu", pin.ServerRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        await File.WriteAllTextAsync(serverPath, "fake-llama-server");

        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("A cache hit must not download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RecordingRegistry();
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64,
            acquisitionStatus: registry);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.Empty(registry.Writes);
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Idle), registry.Current.Phase);
    }

    [Test]
    public async Task EnsureBinary_WhenTheRuntimeIsUnavailableForTheHost_ReportsNothing()
    {
        // The no-prebuilt throw happens BEFORE any acquisition starts, so there is nothing to narrate and no banner to
        // put into a Failed state — the operator never asked for this variant, the variant selector did.
        using var cache = new TempCacheDir();
        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("An unresolvable variant must not download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RecordingRegistry();
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64,
            acquisitionStatus: registry);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None));

        AssertEx.Empty(registry.Writes);
    }

    [Test]
    public async Task InstallTag_WindowsCudaTwoArchivePath_ReportsBothStepsAgainstAStepCountOfTwo()
    {
        // Windows CUDA fetches TWO archives back to back — the build plus its cudart companion. Without the step
        // counter the banner would run 0→100 % twice with nothing to explain the second pass.
        using var cache = new TempCacheDir();
        var pin = AssertEx.NotNull(LlamaCppReleasePins.TryResolveExact(OSPlatform.Windows, Architecture.X64, GpuVariant.Cuda));
        var cudartName = AssertEx.NotNull(LlamaCppReleasePins.DeriveCudartAssetName(pin.AssetName));

        var mainArchive = BuildZip((pin.ServerRelativePath, "fake-llama-server"));
        var cudartArchive = BuildZip(("cudart64_12.dll", "fake-cuda-runtime"));
        using var handler = new ScriptedHandler(uri => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(uri.AbsoluteUri.Contains(cudartName, StringComparison.Ordinal) ? cudartArchive : mainArchive)
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RecordingRegistry();
        var catalog = new CompanionCatalog(UpgradeTag, cudartName, Sha256Hex(cudartArchive), cudartArchive.LongLength);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Windows, Architecture.X64,
            catalog,
            acquisitionStatus: registry);

        // The post-install smoke test spawns the extracted llama-server.exe, which is a text stub here, so the install
        // ends Failed on every host. The step reporting under test all happens before that.
        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync(UpgradeTag,
            pin.AssetName,
            Sha256Hex(mainArchive),
            mainArchive.LongLength,
            GpuVariant.Cuda,
            CancellationToken.None));

        AssertEx.True(registry.Writes.All(status => status.StepCount == 2), "A Windows-CUDA acquisition fetches two archives.");
        AssertEx.Contains(registry.Writes, status => status is { StepIndex: 1, Phase: nameof(RuntimeAcquisitionPhase.Downloading) });
        AssertEx.Contains(registry.Writes, status => status is { StepIndex: 2, Phase: nameof(RuntimeAcquisitionPhase.Downloading) });
        AssertEx.Contains(registry.Writes, status => status is { StepIndex: 2, Phase: nameof(RuntimeAcquisitionPhase.Verifying) });
        AssertEx.Contains(registry.Writes, status => status is { StepIndex: 2, Phase: nameof(RuntimeAcquisitionPhase.Extracting) });
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Failed), registry.Writes[^1].Phase);
        AssertEx.Equal(nameof(GpuVariant.Cuda), registry.Writes[^1].Variant);
        AssertEx.Equal(UpgradeTag, registry.Writes[^1].Tag);
    }

    [Test]
    public async Task InstallTag_WhenTheFirstAttemptFailsAndTheRetrySucceeds_EndsAtCompletedWithNoFailed()
    {
        // The pipeline retries once on a hash mismatch. Failed is reported ONLY from the outer catch, and the retry is
        // driven by a returned exception rather than a thrown one — so a recovered acquisition must end at Completed
        // with no Failed anywhere in the stream, or the banner would latch onto a failure that was already repaired.
        // POSIX only: the post-install smoke test spawns the extracted shell stub, which needs a real exec bit.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var cache = new TempCacheDir();
        var archive = BuildExecutableTarGz();
        var attempt = 0;
        using var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Attempt 1 hashes wrong; attempt 2 serves the real archive.
            Content = new ByteArrayContent(++attempt == 1 ? "corrupt-first-attempt"u8.ToArray() : archive)
        });
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RecordingRegistry();
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64,
            acquisitionStatus: registry);

        var binary = await manager.InstallTagAsync(UpgradeTag,
            "llama-b9799-bin-ubuntu-x64.tar.gz",
            Sha256Hex(archive),
            expectedSize: 0,
            GpuVariant.Cpu,
            CancellationToken.None);

        AssertEx.Equal(UpgradeTag, binary.Version);
        AssertEx.Equal(expected: 2, handler.CallCount);
        // The first attempt reached Verifying before it failed, proving the retry really was exercised.
        AssertEx.Equal(expected: 2, registry.Writes.Count(status => status.Phase == nameof(RuntimeAcquisitionPhase.Verifying)));
        AssertEx.Equal(nameof(RuntimeAcquisitionPhase.Completed), registry.Writes[^1].Phase);
        AssertEx.False(registry.Writes.Any(status => status.Phase == nameof(RuntimeAcquisitionPhase.Failed)),
            "A recovered retry must never leave a Failed in the status stream.");
    }

    [Test]
    public async Task InstallTag_WhenTheRequestIsRejected_ReportsNothing()
    {
        // Request validation acquires nothing, so a malformed tag must not put the banner into a runtime-failure state.
        using var cache = new TempCacheDir();
        using var handler = new ScriptedHandler(() => throw new InvalidOperationException("A malformed tag must never download."));
        using var http = new HttpClient(handler, disposeHandler: false);
        var registry = new RecordingRegistry();
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag, OSPlatform.Linux, Architecture.X64,
            acquisitionStatus: registry);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.InstallTagAsync("../escape",
            "llama-b9799-bin-ubuntu-x64.tar.gz",
            new string('a', 64),
            expectedSize: 0,
            GpuVariant.Cpu,
            CancellationToken.None));

        AssertEx.Empty(registry.Writes);
    }

    /// <summary>A tar.gz carrying an executable <c>llama-server</c> stub, so the post-install smoke test can pass on POSIX.</summary>
    private static byte[] BuildExecutableTarGz()
    {
        using var raw = new MemoryStream();
        using (var tar = new TarWriter(raw, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "build/bin/llama-server")
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                DataStream = new MemoryStream("#!/bin/sh\necho 'version: b9799'\nexit 0\n"u8.ToArray())
            });
        }

        raw.Position = 0;
        using var gz = new MemoryStream();
        using (var gzip = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
        {
            raw.CopyTo(gzip);
        }

        return gz.ToArray();
    }

    private static byte[] BuildZip(params (string Path, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var stream = archive.CreateEntry(path).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }

    private static string Sha256Hex(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    ///     Capturing <see cref="IRuntimeAcquisitionStatusRegistry" /> that stamps sequences exactly as the real registry
    ///     does but keeps EVERY write (the real one throttles only the push, never the write), so a test can assert on
    ///     the complete status stream the manager produced.
    /// </summary>
    private sealed class RecordingRegistry : IRuntimeAcquisitionStatusRegistry
    {
        private readonly Lock _gate = new();
        private readonly List<RuntimeAcquisitionStatusHubEvent> _writes = [];
        private long _sequence;

        public RuntimeAcquisitionStatusHubEvent Current
        {
            get
            {
                lock (_gate)
                {
                    return _writes.Count == 0 ? RuntimeAcquisitionStatusRegistry.Empty : _writes[^1];
                }
            }
        }

        public IReadOnlyList<RuntimeAcquisitionStatusHubEvent> Writes
        {
            get
            {
                lock (_gate)
                {
                    return [.. _writes];
                }
            }
        }

        public void Report(RuntimeAcquisitionUpdate update)
        {
            lock (_gate)
            {
                _writes.Add(new RuntimeAcquisitionStatusHubEvent(++_sequence,
                    update.Phase.ToString(),
                    update.Variant,
                    update.Tag,
                    update.CompletedBytes,
                    update.TotalBytes,
                    update.StepIndex,
                    update.StepCount,
                    update.SanitizedError));
            }
        }
    }

    /// <summary>Resolves only the Windows-CUDA cudart companion; every other lookup reports no live data.</summary>
    private sealed class CompanionCatalog(string companionTag, string companionAsset, string companionDigest, long companionSize) : ILlamaCppReleaseCatalog
    {
        public Task<LlamaCppReleaseResult> ResolveRecommendedAsync(string recommendedTag, CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.Offline());
        }

        public Task<LlamaCppReleaseResult> ResolveUpstreamLatestAsync(CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.Offline());
        }

        public Task<LlamaCppReleaseResult> ResolveAssetAsync(string tag, OSPlatform os, Architecture arch, GpuVariant variant, CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.Offline());
        }

        public Task<LlamaCppReleaseResult> ResolveCompanionAssetAsync(string tag, string assetName, CancellationToken ct)
        {
            return Task.FromResult(LlamaCppReleaseResult.ForAsset(companionTag,
                new LlamaCppReleaseAsset(companionAsset, LlamaCppReleasePins.DownloadUri(companionTag, companionAsset), companionDigest, companionSize)));
        }
    }

    private sealed class ScriptedHandler(Func<Uri, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public ScriptedHandler(Func<HttpResponseMessage> responder)
            : this(_ => responder())
        {
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responder(request.RequestUri!));
        }
    }

    /// <summary>Cancels the caller's token the moment the request is issued, i.e. mid-acquisition rather than before it.</summary>
    private sealed class CancellingHandler(CancellationTokenSource cts) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await cts.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class TempCacheDir : IDisposable
    {
        public TempCacheDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-llama-acq-" + Guid.NewGuid().ToString("N"));
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
