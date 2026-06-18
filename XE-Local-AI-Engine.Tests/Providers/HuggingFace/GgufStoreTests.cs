namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     Plan §12 store rows: disk guard, quant resolution, resume, disk-full survival, hash verification, cancel,
///     progress reporting, and gated/token behaviour. All HTTP is faked — no network, no Docker, no real DriveInfo.
/// </summary>
public sealed class GgufStoreTests
{
    private static readonly byte[] ModelBytes = Encoding.UTF8.GetBytes(new string('g', 4096));

    [Test]
    public async Task GgufStore_DiskGuard_BlocksBeforeAnyBytes_WhenInsufficient()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        // Free space is well below the file size → the hard guard must throw before any stream opens.
        var probe = Infra.FixedSpace(ModelBytes.Length - 1);
        using var handler = new Infra.ScriptedHandler((_, _) =>
            throw new InvalidOperationException("The disk guard must block before any HTTP call."));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), probe, options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        await AssertEx.ThrowsAsync<InsufficientDiskSpaceException>(
            () => store.EnsureModelAsync(new GgufModelRequest { RepoId = Infra.RepoId }, progress: null, CancellationToken.None));

        // No .part written.
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path, "*.part"));
        AssertEx.Equal(0, handler.CallCount);
    }

    [Test]
    public async Task GgufStore_RejectsPathTraversalFileName_WritesNothingOutsideModelsDir()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, _) =>
            throw new InvalidOperationException("A traversal file name must be rejected before any HTTP call."));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        // A malicious repo returns a .gguf whose name escapes the models directory but still parses as Q4_K_M.
        var malicious = Infra.RepoFile("../../../../tmp/evil-Q4_K_M.gguf", "Q4_K_M", ModelBytes.Length);
        var store = Infra.Store(download, Infra.DiscoveryWith(malicious), registry, options);

        await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(
            () => store.EnsureModelAsync(new GgufModelRequest { RepoId = Infra.RepoId }, progress: null, CancellationToken.None));

        // Rejected before any HTTP call; nothing written anywhere under the models directory.
        AssertEx.Equal(0, handler.CallCount);
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path));
    }

    [Test]
    public async Task GgufStore_EnsureModel_DefaultsToQ4_K_M_WhenNoQuant()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(
            Infra.RepoFile("Demo-Model-Q8_0.gguf", "Q8_0", 10),
            Infra.RepoFile(Infra.FileName, "Q4_K_M", ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(
            new GgufModelRequest { RepoId = Infra.RepoId }, progress: null, CancellationToken.None);

        AssertEx.Equal("Q4_K_M", handle.Quant);
        AssertEx.Equal(Infra.ModelName, handle.ModelName);
        AssertEx.True(File.Exists(handle.LocalPath));
        AssertEx.Equal(Infra.FileName, Path.GetFileName(handle.LocalPath));
    }

    [Test]
    public async Task GgufStore_EnsureModel_HonorsQuantOverride()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(
            Infra.RepoFile(Infra.FileName, "Q4_K_M", 10),
            Infra.RepoFile("Demo-Model-Q8_0.gguf", "Q8_0", ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(
            new GgufModelRequest { RepoId = Infra.RepoId, Quant = "Q8_0" }, progress: null, CancellationToken.None);

        AssertEx.Equal("Q8_0", handle.Quant);
        AssertEx.Equal("Demo-Model-Q8_0.gguf", Path.GetFileName(handle.LocalPath));
    }

    [Test]
    public async Task GgufStore_Resume_ContinuesFromPartialPart_AcrossRuns()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        const int prefix = 1024;
        // Pre-seed a .part with the first 1024 bytes, simulating an interrupted earlier run.
        var partPath = dir.FilePath(Infra.FileName) + ".part";
        await File.WriteAllBytesAsync(partPath, ModelBytes[..prefix]);

        using var handler = new Infra.ScriptedHandler((_, _) => PartialDownload(ModelBytes, prefix));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(
            new GgufModelRequest { RepoId = Infra.RepoId }, progress: null, CancellationToken.None);

        // A Range request was issued from the partial offset and the final file is the full, intact byte stream.
        AssertEx.NotNull(handler.Requests[0].Range);
        AssertEx.Contains(handler.Requests[0].Range!, prefix.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var finalBytes = await File.ReadAllBytesAsync(handle.LocalPath);
        AssertEx.Equal(ModelBytes.Length, finalBytes.Length);
        AssertEx.True(finalBytes.SequenceEqual(ModelBytes));
        AssertEx.False(File.Exists(partPath));
    }

    [Test]
    public async Task GgufStore_Resume_RecoversFrom416_ByRestartingFromStart()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        // A stale .part at/over the real length → the first (ranged) request 416s; the store must drop it and the
        // retry (no Range) must complete the full file from byte 0.
        var partPath = dir.FilePath(Infra.FileName) + ".part";
        await File.WriteAllBytesAsync(partPath, ModelBytes);

        using var handler = new Infra.ScriptedHandler((_, callIndex) => callIndex == 0
            ? new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            : FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(
            new GgufModelRequest { RepoId = Infra.RepoId }, progress: null, CancellationToken.None);

        // Recovered after exactly two requests: the 416 reset, then a clean full download.
        AssertEx.Equal(2, handler.CallCount);
        // The retry sent no Range (restart from byte 0).
        AssertEx.Null(handler.Requests[1].Range);
        var finalBytes = await File.ReadAllBytesAsync(handle.LocalPath);
        AssertEx.True(finalBytes.SequenceEqual(ModelBytes));
        AssertEx.False(File.Exists(partPath));
    }

    [Test]
    public async Task GgufStore_DiskFullMidDownload_SurfacesReason_LeavesPartIntact()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // A stream that throws ENOSPC partway through the copy loop.
            Content = new StreamContent(new DiskFullStream(ModelBytes, throwAfter: 512))
        });
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var exception = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(
            () => store.EnsureModelAsync(new GgufModelRequest { RepoId = Infra.RepoId }, progress: null, CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.DiskFull, exception.Reason);
        // .part retained for resume; final never created.
        AssertEx.True(File.Exists(dir.FilePath(Infra.FileName) + ".part"));
        AssertEx.False(File.Exists(dir.FilePath(Infra.FileName)));
    }

    [Test]
    public async Task GgufStore_VerifiesHash_RejectsCorruptDownload()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        // The handler advertises a sha that does NOT match the bytes it streams → integrity failure.
        var wrongSha = Infra.Sha256Upper(Encoding.UTF8.GetBytes("different-content"));
        using var handler = new Infra.ScriptedHandler((_, _) => FullDownload(ModelBytes, lfsSha256: wrongSha));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var exception = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(
            () => store.EnsureModelAsync(new GgufModelRequest { RepoId = Infra.RepoId }, progress: null, CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.HashMismatch, exception.Reason);
        AssertEx.False(File.Exists(dir.FilePath(Infra.FileName)));
        AssertEx.Empty(await registry.ListAsync(CancellationToken.None));
    }

    [Test]
    public async Task GgufStore_AcceptsDownload_WhenLfsShaMatches()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        var correctSha = Infra.Sha256Upper(ModelBytes);
        using var handler = new Infra.ScriptedHandler((_, _) => FullDownload(ModelBytes, lfsSha256: correctSha));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var handle = await store.EnsureModelAsync(
            new GgufModelRequest { RepoId = Infra.RepoId }, progress: null, CancellationToken.None);

        AssertEx.NotNull(handle.Sha256);
        AssertEx.Equal(correctSha, handle.Sha256!, message: null);
        AssertEx.True(File.Exists(handle.LocalPath));
    }

    [Test]
    public async Task GgufStore_CancelDuringDownload_StopsAndLeavesNoFinal()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var cts = new CancellationTokenSource();
        using var handler = new Infra.ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            // Cancel is tripped after some bytes flow, mid copy loop.
            Content = new StreamContent(new CancelTriggeringStream(ModelBytes, cts, cancelAfter: 512))
        });
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        await AssertEx.ThrowsAsync<OperationCanceledException>(
            () => store.EnsureModelAsync(new GgufModelRequest { RepoId = Infra.RepoId }, progress: null, cts.Token));

        AssertEx.False(File.Exists(dir.FilePath(Infra.FileName)));
        AssertEx.Empty(await registry.ListAsync(CancellationToken.None));
    }

    [Test]
    public async Task GgufStore_ReportsProgress_AsPullProgressDto()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var reports = new List<PullProgress>();
        var progress = new SynchronousProgress(reports.Add);

        await store.EnsureModelAsync(new GgufModelRequest { RepoId = Infra.RepoId }, progress, CancellationToken.None);

        AssertEx.NotEmpty(reports);
        AssertEx.Contains(reports, report => report.ModelName == Infra.ModelName);
        AssertEx.Contains(reports, report => report.Status == "downloading");
        AssertEx.Contains(reports, report => report.Status == "completed" && report.CompletedBytes == ModelBytes.Length);
    }

    [Test]
    public async Task GgufStore_GatedRepo_UsesBearerToken_WhenPresent()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        const string token = "hf_secret_token_value";
        using var handler = new Infra.ScriptedHandler((_, _) => FullDownload(ModelBytes));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.TokenStore(token), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        await store.EnsureModelAsync(new GgufModelRequest { RepoId = Infra.RepoId }, progress: null, CancellationToken.None);

        AssertEx.Equal("Bearer", handler.Requests[0].AuthScheme!, message: null);
        AssertEx.Equal(token, handler.Requests[0].AuthParameter!, message: null);
    }

    [Test]
    public async Task GgufStore_GatedRepo_NoToken_SurfacesUnauthorized()
    {
        using var dir = new Infra.TempModelsDir();
        var options = Infra.Options(dir.Path);
        using var handler = new Infra.ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var http = new HttpClient(handler);
        using var registry = Infra.Registry(options);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var discovery = Infra.DiscoveryWith(Infra.RepoFile(Infra.FileName, Infra.Quant, ModelBytes.Length));
        var store = Infra.Store(download, discovery, registry, options);

        var exception = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(
            () => store.EnsureModelAsync(new GgufModelRequest { RepoId = Infra.RepoId }, progress: null, CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.Gated, exception.Reason);
        // No retry on a 401 — exactly one call.
        AssertEx.Equal(1, handler.CallCount);
        // The surfaced message never carries a token (none configured here, but the contract is asserted).
        AssertEx.False(exception.Message.Contains("hf_", StringComparison.OrdinalIgnoreCase));
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
        public void Report(PullProgress value) => onReport(value);
    }

    // A read stream that yields some bytes then throws an ENOSPC IOException, simulating a disk-full write failure.
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
                throw new IOException("No space left on device.", 28);
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
                throw new IOException("No space left on device.", 28);
            }

            var toCopy = Math.Min(buffer.Length, throwAfter - _position);
            bytes.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return ValueTask.FromResult(toCopy);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

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
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
