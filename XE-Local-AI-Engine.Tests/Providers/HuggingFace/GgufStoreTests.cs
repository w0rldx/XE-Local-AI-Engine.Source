namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Providers.HuggingFace.Telemetry;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     GGUF store: disk guard, quant resolution, resume, disk-full survival, hash verification, cancel,
///     progress reporting, and gated/token behaviour. All HTTP is faked — no network, no real DriveInfo.
/// </summary>
public sealed class GgufStoreTests
{
    private static readonly byte[] ModelBytes = Encoding.UTF8.GetBytes(new string(c: 'g', count: 4096));

    [Test]
    public async Task GgufStore_DiskGuard_BlocksBeforeAnyBytes_WhenInsufficient()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        // Free space is well below the file size → the hard guard must throw before any stream opens.
        var probe = Infra.FixedSpace(ModelBytes.Length - 1);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
            throw new InvalidOperationException("The disk guard must block before any HTTP call."));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), probe, options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        await AssertEx.ThrowsAsync<InsufficientDiskSpaceException>(() => store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None));

        // No .part written.
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path, "*.part"));
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task GgufStore_RejectsPathTraversalFileName_WritesNothingOutsideModelsDir()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
            throw new InvalidOperationException("A traversal file name must be rejected before any HTTP call."));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        // A malicious repo returns a .gguf whose name escapes the models directory but still parses as Q4_K_M.
        var malicious = Infra.RepoFile("../../../../tmp/evil-Q4_K_M.gguf", "Q4_K_M", ModelBytes.Length);
        var store = Infra.Store(download, Infra.DiscoveryWith(malicious), registry, options);

        await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None));

        // Rejected before any HTTP call; nothing written anywhere under the models directory.
        AssertEx.Equal(expected: 0, handler.CallCount);
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path));
    }

    [Test]
    public async Task GgufStore_EnsureModel_DefaultsToQ4_K_M_WhenNoQuant()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile("Demo-Model-Q8_0.gguf", "Q8_0", sizeBytes: 10),
            Infra.RepoFile(Infra.FileName, "Q4_K_M", ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None);

        AssertEx.Equal("Q4_K_M", handle.Quant);
        AssertEx.Equal(Infra.ModelName, handle.ModelName);
        AssertEx.True(File.Exists(handle.LocalPath));
        AssertEx.Equal(Infra.FileName, Path.GetFileName(handle.LocalPath));
        AssertEx.True(File.Exists(handle.LocalPath + GgufAcquisitionSidecar.Suffix));
        var installed = await registry.FindAsync(handle.ModelName, CancellationToken.None);
        AssertEx.NotNull(installed);
        AssertEx.Equal(LocalModelOrigin.HuggingFace, installed!.Origin);
        AssertEx.True(GgufRegistryRevision.IsCanonical(installed.RegistryRevision));
        AssertEx.True(GgufRegistryRevision.IsCanonical(installed.ModelContentFingerprint));
        AssertEx.Equal(64, installed.Sha256!.Length);
        AssertEx.True(GgufMemberFingerprint.IsCanonicalSha256(installed.Sha256));

        // The universal sidecar is authoritative when the manifest is corrupt and reconstructs exact provenance/fingerprints.
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "index.json"), "{ corrupt manifest");
        using var recoveredRegistry = Infra.Registry(options);
        var recovered = await recoveredRegistry.FindAsync(handle.ModelName, CancellationToken.None);
        AssertEx.NotNull(recovered);
        AssertEx.Equal(installed.RegistryRevision!, recovered!.RegistryRevision);
        AssertEx.Equal(installed.ModelContentFingerprint!, recovered.ModelContentFingerprint);
        AssertEx.Equal(LocalModelOrigin.HuggingFace, recovered.Origin);
    }

    [Test]
    public async Task GgufStore_DownloadCommitRejectsCaseOnlyDestinationCollision()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var collisionPath = dir.FilePath(Infra.FileName.ToUpperInvariant());
        await File.WriteAllTextAsync(collisionPath, "preserve-existing");
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var store = Infra.Store(download,
            Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length)),
            registry,
            options);

        var exception = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.DestinationConflict, exception.Reason);
        AssertEx.Equal("preserve-existing", await File.ReadAllTextAsync(collisionPath));
    }

    [Test]
    public async Task GgufStore_EnsureModel_HonorsQuantOverride()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, "Q4_K_M", sizeBytes: 10),
            Infra.RepoFile("Demo-Model-Q8_0.gguf", "Q8_0", ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId,
            Quant = "Q8_0"
        }, progress: null, CancellationToken.None);

        AssertEx.Equal("Q8_0", handle.Quant);
        AssertEx.Equal("Demo-Model-Q8_0.gguf", Path.GetFileName(handle.LocalPath));
    }

    [Test]
    public async Task GgufStore_EnsureModel_ResolvesExplicitUnslothDynamicQuant()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        // A repo offering both a plain and a Dynamic quant; the request pins the Dynamic one.
        var discovery = Infra.DiscoveryWith(Infra.RepoFile("Demo-Model-Q4_K_M.gguf", "Q4_K_M", sizeBytes: 10),
            Infra.RepoFile("Demo-Model-UD-Q4_K_XL.gguf", "UD-Q4_K_XL", ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId,
            Quant = "UD-Q4_K_XL"
        }, progress: null, CancellationToken.None);

        AssertEx.Equal("UD-Q4_K_XL", handle.Quant);
        AssertEx.Equal("Demo-Model-UD-Q4_K_XL.gguf", Path.GetFileName(handle.LocalPath));
        // The Dynamic marker round-trips into the canonical registry key (repo:UD-Q4_K_XL).
        AssertEx.Equal(GgufModelName.Format(Infra.RepoId, "UD-Q4_K_XL"), handle.ModelName);
    }

    [Test]
    public async Task GgufStore_EnsureModel_BaseQuantFallsBackToUnslothDynamic_WhenNoPlainFile()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        // A UD-only repo: the default quant (Q4_K_M) has no exact file and must fall back to the Dynamic variant.
        var discovery = Infra.DiscoveryWith(Infra.RepoFile("Demo-Model-UD-Q4_K_M.gguf", "UD-Q4_K_M", ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None);

        AssertEx.Equal("UD-Q4_K_M", handle.Quant);
        AssertEx.Equal("Demo-Model-UD-Q4_K_M.gguf", Path.GetFileName(handle.LocalPath));
    }

    [Test]
    public async Task GgufStore_EnsureModel_BaseQuantPrefersPlainOverDynamic_WhenBothExist()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        // Both a plain and a Dynamic Q4_K_M exist; a bare Q4_K_M request must pick the exact (plain) one.
        var discovery = Infra.DiscoveryWith(Infra.RepoFile("Demo-Model-Q4_K_M.gguf", "Q4_K_M", ModelBytes.Length),
            Infra.RepoFile("Demo-Model-UD-Q4_K_M.gguf", "UD-Q4_K_M", sizeBytes: 10));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId,
            Quant = "Q4_K_M"
        }, progress: null, CancellationToken.None);

        AssertEx.Equal("Q4_K_M", handle.Quant);
        AssertEx.Equal("Demo-Model-Q4_K_M.gguf", Path.GetFileName(handle.LocalPath));
    }

    [Test]
    public async Task GgufStore_EnsureModel_ExplicitDynamicQuant_DoesNotFallBackToPlain()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
            throw new InvalidOperationException("No download must occur when the Dynamic quant is absent."));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        // Only a plain Q4_K_M exists; an explicit UD- request must NOT silently resolve to it.
        var discovery = Infra.DiscoveryWith(Infra.RepoFile("Demo-Model-Q4_K_M.gguf", "Q4_K_M", ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var exception = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId,
            Quant = "UD-Q4_K_M"
        }, progress: null, CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.NotFound, exception.Reason);
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task GgufStore_Resume_ContinuesFromPartialPart_AcrossRuns()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        const int prefix = 1024;
        // Pre-seed a .part with the first 1024 bytes, simulating an interrupted earlier run.
        var partPath = dir.FilePath(Infra.FileName) + ".part";
        await File.WriteAllBytesAsync(partPath, ModelBytes[..prefix]);

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => PartialDownload(ModelBytes, prefix));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None);

        // A Range request was issued from the partial offset and the final file is the full, intact byte stream.
        AssertEx.NotNull(handler.Requests[0].Range);
        AssertEx.Contains(handler.Requests[0].Range!, prefix.ToString(CultureInfo.InvariantCulture));
        var finalBytes = await File.ReadAllBytesAsync(handle.LocalPath);
        AssertEx.Equal(ModelBytes.Length, finalBytes.Length);
        AssertEx.True(finalBytes.SequenceEqual(ModelBytes));
        AssertEx.False(File.Exists(partPath));
    }

    [Test]
    public async Task GgufStore_Resume_RecoversFrom416_ByRestartingFromStart()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        // A stale .part at/over the real length → the first (ranged) request 416s; the store must drop it and the
        // retry (no Range) must complete the full file from byte 0.
        var partPath = dir.FilePath(Infra.FileName) + ".part";
        await File.WriteAllBytesAsync(partPath, ModelBytes);

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, callIndex) => callIndex == 0
            ? new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            : FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None);

        // Recovered after exactly two requests: the 416 reset, then a clean full download.
        AssertEx.Equal(expected: 2, handler.CallCount);
        // The retry sent no Range (restart from byte 0).
        AssertEx.Null(handler.Requests[1].Range);
        var finalBytes = await File.ReadAllBytesAsync(handle.LocalPath);
        AssertEx.True(finalBytes.SequenceEqual(ModelBytes));
        AssertEx.False(File.Exists(partPath));
    }

    [Test]
    public async Task Download_WhenBodyStallsMidCopy_ReadIdleTimeoutSurfacesTransientFailureAndRetryResumes()
    {
        // AUD4-18: ResponseHeadersRead means the HttpClient timeout covers only the headers; a CDN that stalls mid-body
        // must be bounded by the read-idle timeout, surfaced as a TRANSIENT failure so the existing retry/resume path
        // completes the download rather than hanging forever.
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = new HuggingFaceOptions
        {
            ModelsDirectory = dir.Path,
            DiskMarginBytes = 0,
            DefaultQuant = Infra.Quant,
            MaxDownloadRetries = 3,
            DownloadReadIdleTimeoutSeconds = 1
        };
        var metrics = new CountingDownloadMetrics();

        // Attempt 0 yields 256 bytes then stalls forever (no more data) → the 1 s read-idle deadline fires. Attempt 1
        // serves the full file.
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, callIndex) => callIndex == 0
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream(ModelBytes, yieldBytes: 256))
            }
            : FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var resolveHandler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => new HttpResponseMessage());
        using var resolveHttp = new HttpClient(resolveHandler, disposeHandler: false);
        var download = new HfDownloadClient(http, resolveHttp, Infra.NoTokenStore(), Infra.AbundantSpace(), options, NullLogger<HfDownloadClient>.Instance, metrics);

        var destination = dir.FilePath(Infra.FileName);
        _ = await download.DownloadAsync(Infra.RepoId, Infra.FileName, Infra.Revision, Infra.ModelName, destination, ModelBytes.Length, expectedSha256: null, progress: null, CancellationToken.None);

        // The idle stall on attempt 0 counted as one transient failure; attempt 1 completed the file.
        AssertEx.Equal(expected: 2, handler.CallCount);
        AssertEx.Equal(expected: 1, metrics.ReadIdleTimeouts);
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(ModelBytes));
        AssertEx.False(File.Exists(destination + ".part"));
    }

    [Test]
    public async Task GgufStore_DiskFullMidDownload_SurfacesReason_LeavesPartIntact()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // A stream that throws ENOSPC partway through the copy loop.
            Content = new StreamContent(new DiskFullStream(ModelBytes, throwAfter: 512))
        });
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var exception = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.DiskFull, exception.Reason);
        // .part retained for resume; final never created.
        AssertEx.True(File.Exists(dir.FilePath(Infra.FileName) + ".part"));
        AssertEx.False(File.Exists(dir.FilePath(Infra.FileName)));
    }

    [Test]
    public async Task GgufStore_VerifiesHash_RejectsCorruptDownload()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        // The handler advertises a sha that does NOT match the bytes it streams → integrity failure.
        var wrongSha = Infra.Sha256Upper(Encoding.UTF8.GetBytes("different-content"));
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes, wrongSha));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var exception = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.HashMismatch, exception.Reason);
        AssertEx.False(File.Exists(dir.FilePath(Infra.FileName)));
        AssertEx.Empty(await registry.ListAsync(CancellationToken.None));
    }

    [Test]
    public async Task GgufStore_AcceptsDownload_WhenLfsShaMatches()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var correctSha = Infra.Sha256Upper(ModelBytes);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes, correctSha));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None);

        AssertEx.NotNull(handle.Sha256);
        AssertEx.Equal(correctSha, handle.Sha256!);
        AssertEx.True(File.Exists(handle.LocalPath));
    }

    [Test]
    public async Task GgufStore_VerifiesAgainstDiscoveryDigest_WhenNoResolveOid()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var correctSha = Infra.Sha256Upper(ModelBytes);
        // Neither the resolve probe nor the byte GET expose an X-Linked-Etag; the discovery digest is the sole integrity
        // source and MUST be used to verify the stream.
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length, correctSha));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None);

        AssertEx.NotNull(handle.Sha256);
        AssertEx.Equal(correctSha, handle.Sha256!);
        AssertEx.True(File.Exists(handle.LocalPath));
    }

    [Test]
    public async Task GgufStore_DiscoveryDigestMismatch_RejectsDownload()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        // The discovery digest does NOT match the streamed bytes, and no OID is on the wire → the fallback verification
        // must reject the download.
        var wrongSha = Infra.Sha256Upper(Encoding.UTF8.GetBytes("different-content"));
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length, wrongSha));
        var store = Infra.Store(download, discovery, registry, options);

        var exception = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.HashMismatch, exception.Reason);
        AssertEx.False(File.Exists(dir.FilePath(Infra.FileName)));
        AssertEx.Empty(await registry.ListAsync(CancellationToken.None));
    }

    [Test]
    public async Task GgufStore_NoShaAnywhere_PersistsNullSha_NotDiscoveryValue()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        // No OID on the resolve probe, no X-Linked-Etag on the GET, and no discovery digest → nothing to verify, so the
        // persisted sha256 MUST be null rather than an unverified echo.
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None);

        AssertEx.Null(handle.Sha256);
        AssertEx.True(File.Exists(handle.LocalPath));

        var stored = await registry.ListAsync(CancellationToken.None);
        AssertEx.Null(stored.Single().Sha256);
    }

    [Test]
    public async Task GgufStore_CancelDuringDownload_StopsAndLeavesNoFinal()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var cts = new CancellationTokenSource();
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Cancel is tripped after some bytes flow, mid copy loop.
            Content = new StreamContent(new CancelTriggeringStream(ModelBytes, cts, cancelAfter: 512))
        });
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, cts.Token));

        AssertEx.False(File.Exists(dir.FilePath(Infra.FileName)));
        AssertEx.Empty(await registry.ListAsync(CancellationToken.None));
    }

    [Test]
    public async Task GgufStore_ReportsProgress_AsPullProgressDto()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var reports = new List<PullProgress>();
        var progress = new SynchronousProgress(reports.Add);

        await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress, CancellationToken.None);

        AssertEx.NotEmpty(reports);
        AssertEx.Contains(reports, report => report.ModelName == Infra.ModelName);
        AssertEx.Contains(reports, report => report.Status == "downloading");
        AssertEx.Contains(reports, report => report.Status == "completed" && report.CompletedBytes == ModelBytes.Length);
    }

    [Test]
    public async Task GgufStore_GatedRepo_UsesBearerToken_WhenPresent()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        const string token = "hf_secret_token_value";
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.TokenStore(token), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None);

        AssertEx.Equal("Bearer", handler.Requests[0].AuthScheme!);
        AssertEx.Equal(token, handler.Requests[0].AuthParameter!);
    }

    [Test]
    public async Task GgufStore_GatedRepo_NoToken_SurfacesUnauthorized()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var exception = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.Gated, exception.Reason);
        // No retry on a 401 — exactly one call.
        AssertEx.Equal(expected: 1, handler.CallCount);
        // The surfaced message never carries a token (none configured here, but the contract is asserted).
        AssertEx.False(exception.Message.Contains("hf_", StringComparison.OrdinalIgnoreCase));
    }

    [Test]
    public async Task GgufStore_XetBacked_VerifiesAgainstLinkedEtagProbe_NotCdnEtag()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var correctSha = Infra.Sha256Upper(ModelBytes);
        // A non-sha 64-hex value standing in for the Xet content-defined-chunking hash the CDN returns as ETag.
        const string xetCdnHash = "72b4dc491f5f3256ee30377cfbc5b3134991f5e58906bb88a012786c09e1cca8";

        // Resolve probe (no-redirect HEAD): the hf.co 302 carries the TRUE sha256 on X-Linked-Etag.
        using var resolveHandler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
        {
            var probe = new HttpResponseMessage(HttpStatusCode.Redirect);
            probe.Headers.TryAddWithoutValidation("X-Linked-Etag", $"\"{correctSha}\"");
            probe.Headers.TryAddWithoutValidation("X-Repo-Commit", "abc123def456");
            return probe;
        });
        using var resolveHttp = new HttpClient(resolveHandler);

        // Byte GET (post-redirect CDN): exposes ONLY the Xet ETag (a 64-hex non-sha) and NO X-Linked-Etag — trusting it
        // would be a guaranteed false HashMismatch.
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
        {
            var get = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ModelBytes)
            };
            get.Content.Headers.ContentLength = ModelBytes.Length;
            get.Headers.ETag = new EntityTagHeaderValue($"\"{xetCdnHash}\"");
            return get;
        });
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, resolveHttp, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None);

        // The download succeeds and records the sha from the probe's X-Linked-Etag — the CDN Xet ETag was ignored.
        AssertEx.NotNull(handle.Sha256);
        AssertEx.Equal(correctSha, handle.Sha256!);
        AssertEx.True(File.Exists(handle.LocalPath));
    }

    [Test]
    public async Task GgufStore_XetProbeShaMismatch_RejectsDownload()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        // The probe advertises a sha that does NOT match the streamed bytes → integrity failure from the probe OID.
        var wrongSha = Infra.Sha256Upper(Encoding.UTF8.GetBytes("different-content"));
        using var resolveHandler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
        {
            var probe = new HttpResponseMessage(HttpStatusCode.Redirect);
            probe.Headers.TryAddWithoutValidation("X-Linked-Etag", $"\"{wrongSha}\"");
            return probe;
        });
        using var resolveHttp = new HttpClient(resolveHandler);
        // The byte GET exposes no X-Linked-Etag (CDN), so the probe sha is the sole integrity source.
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, resolveHttp, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var exception = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => store.EnsureModelAsync(new GgufModelRequest
        {
            RepoId = Infra.RepoId
        }, progress: null, CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.HashMismatch, exception.Reason);
        AssertEx.False(File.Exists(dir.FilePath(Infra.FileName)));
    }

    [Test]
    public async Task GgufStore_ListInstalled_PopulatesMaxContextTokens_FromLocalHeader()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var registry = Infra.Registry(options);

        // A real installed GGUF on disk whose header advertises qwen2.context_length = 32768.
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "qwen2")
                     .WithUint32("qwen2.context_length", value: 32768)
                     .Build();
        var entry = await SeedInstalledModel(dir, registry, "qwen2-Q4_K_M.gguf", header);

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
            throw new InvalidOperationException("Listing installed models must not download."));
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var store = Infra.Store(download, Infra.DiscoveryWith(), registry, options);

        var descriptors = await store.ListInstalledModelsAsync(CancellationToken.None);

        var descriptor = descriptors.Single(d => d.ModelName == entry.ModelName);
        AssertEx.Equal(expected: 32768, descriptor.MaxContextTokens!.Value);
        AssertEx.True(descriptor.IsAvailable);
        AssertEx.Equal(entry.SizeBytes, descriptor.SizeBytes);
    }

    [Test]
    public async Task GgufStore_ListInstalled_GarbageFile_YieldsNullContext_StillLists()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var registry = Infra.Registry(options);

        // A present-but-not-GGUF file → no context_length; the model must still appear with a null context window.
        var entry = await SeedInstalledModel(dir, registry, "garbage-Q4_K_M.gguf", "not a gguf header at all"u8.ToArray());

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
            throw new InvalidOperationException("Listing installed models must not download."));
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var store = Infra.Store(download, Infra.DiscoveryWith(), registry, options);

        var descriptors = await store.ListInstalledModelsAsync(CancellationToken.None);

        var descriptor = descriptors.Single(d => d.ModelName == entry.ModelName);
        AssertEx.Null(descriptor.MaxContextTokens);
        AssertEx.True(descriptor.IsAvailable);
    }

    [Test]
    public async Task GgufStore_ListInstalled_CachesHeaderRead_AcrossCalls()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var registry = Infra.Registry(options);

        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "qwen2")
                     .WithUint32("qwen2.context_length", value: 32768)
                     .Build();
        var entry = await SeedInstalledModel(dir, registry, "cached-Q4_K_M.gguf", header);

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) =>
            throw new InvalidOperationException("Listing installed models must not download."));
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var store = Infra.Store(download, Infra.DiscoveryWith(), registry, options);

        var first = await store.ListInstalledModelsAsync(CancellationToken.None);
        AssertEx.Equal(expected: 32768, first.Single(d => d.ModelName == entry.ModelName).MaxContextTokens!.Value);

        // Corrupt the on-disk header AFTER the first read; the (size+mtime-keyed) cache must serve the prior result
        // because the registry size/timestamp are unchanged — proving the file was not re-read on the second call.
        await File.WriteAllBytesAsync(entry.LocalPath, "corrupted after first read"u8.ToArray());

        var second = await store.ListInstalledModelsAsync(CancellationToken.None);
        AssertEx.Equal(expected: 32768, second.Single(d => d.ModelName == entry.ModelName).MaxContextTokens!.Value);
    }

    // Writes a GGUF file to the temp models dir and registers it so ListInstalledModelsAsync returns it.
    private static async Task<GgufModelRegistryEntry> SeedInstalledModel(GgufStoreTestInfrastructure.TempModelsDir dir,
        GgufModelRegistry registry,
        string fileName,
        byte[] fileBytes)
    {
        var localPath = dir.FilePath(fileName);
        await File.WriteAllBytesAsync(localPath, fileBytes);
        var entry = new GgufModelRegistryEntry
        {
            ModelName = GgufModelName.Format(Infra.RepoId, "Q4_K_M"),
            RepoId = Infra.RepoId,
            FileName = fileName,
            Quant = "Q4_K_M",
            LocalPath = localPath,
            SizeBytes = fileBytes.Length,
            Sha256 = null,
            SourceRevision = Infra.Revision,
            DownloadedAtUtc = DateTimeOffset.UtcNow
        };
        await registry.UpsertAsync(entry, CancellationToken.None);
        return entry;
    }

    // Builds a 200 OK full-file response, optionally advertising the LFS sha256 OID via X-Linked-Etag.
    private static HttpResponseMessage FullDownload(byte[] bytes, string? lfsSha256 = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentLength = bytes.Length;
        response.Headers.TryAddWithoutValidation("X-Repo-Commit", "abc123def456");
        if (lfsSha256 is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Linked-Etag", $"\"{lfsSha256}\"");
        }

        return response;
    }

    // Builds a 206 Partial Content response covering [from, end), with a Content-Range advertising the full length.
    private static HttpResponseMessage PartialDownload(byte[] bytes, int from)
    {
        var slice = bytes[from..];
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice)
        };
        response.Content.Headers.ContentLength = slice.Length;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.Length - 1, bytes.Length);
        response.Headers.TryAddWithoutValidation("X-Repo-Commit", "abc123def456");
        return response;
    }

    // Reports progress on the calling thread so assertions see every report deterministically.
    private sealed class SynchronousProgress(Action<PullProgress> onReport) : IProgress<PullProgress>
    {
        public void Report(PullProgress value)
        {
            onReport(value);
        }
    }

    // A read stream that yields some bytes then throws an ENOSPC IOException, simulating a disk-full write failure.
    private sealed class CountingDownloadMetrics : IHfDownloadMetrics
    {
        private int _readIdleTimeouts;

        public int ReadIdleTimeouts => Volatile.Read(ref _readIdleTimeouts);

        public void RecordReadIdleTimeout()
        {
            _ = Interlocked.Increment(ref _readIdleTimeouts);
        }
    }

    // Yields <paramref name="yieldBytes" /> then stalls forever, honoring cancellation so the read-idle deadline cancels it.
    private sealed class StallingStream(byte[] bytes, int yieldBytes) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < yieldBytes)
            {
                var toCopy = Math.Min(buffer.Length, yieldBytes - _position);
                bytes.AsSpan(_position, toCopy).CopyTo(buffer.Span);
                _position += toCopy;
                return toCopy;
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("StallingStream is async-only.");
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class DiskFullStream(byte[] bytes, int throwAfter) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= throwAfter)
            {
                // HResult low word 28 == ENOSPC.
                throw new IOException("No space left on device.", hresult: 28);
            }

            var toCopy = Math.Min(count, throwAfter - _position);
            Array.Copy(bytes, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= throwAfter)
            {
                throw new IOException("No space left on device.", hresult: 28);
            }

            var toCopy = Math.Min(buffer.Length, throwAfter - _position);
            bytes.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    // A read stream that trips the supplied CancellationTokenSource partway through, then honours the token.
    private sealed class CancelTriggeringStream(byte[] bytes, CancellationTokenSource cts, int cancelAfter) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= cancelAfter)
            {
                // Trip the source token deterministically (CancelAfter avoids the synchronous Cancel() analyzer flag),
                // then surface the cancellation the way an aborted copy loop would.
                cts.CancelAfter(TimeSpan.Zero);
                var spin = new SpinWait();
                while (!cts.IsCancellationRequested)
                {
                    spin.SpinOnce();
                }

                cts.Token.ThrowIfCancellationRequested();
            }

            var toCopy = Math.Min(buffer.Length, cancelAfter - _position);
            bytes.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
