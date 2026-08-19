namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;
using Infra = GgufStoreTestInfrastructure;

/// <summary>
///     Parallel byte-range downloads: the happy path reassembles the file exactly, an origin that ignores
///     <c>Range</c> falls back to the single stream, an interrupted run resumes only the incomplete ranges, and the
///     connection count is clamped at the point of use. All HTTP is faked — no network.
/// </summary>
public sealed class HfParallelDownloadTests
{
    // A per-position-distinct payload: an off-by-one in the chunk maths corrupts the file visibly, which a repeated
    // filler byte would hide.
    private static readonly byte[] Payload = BuildPayload(8192);

    [Test]
    public async Task ParallelDownload_ReassemblesEveryRangeExactly_AndVerifiesHash()
    {
        using var dir = new Infra.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 4);
        var correctSha = Infra.Sha256Upper(Payload);
        using var handler = new Infra.ScriptedHandler((request, _) => RangeResponse(Payload, request, correctSha));
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var destination = dir.FilePath(Infra.FileName);
        var progress = new ConcurrentProgress();

        var result = await download.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            Payload.Length,
            expectedSha256: null,
            progress,
            CancellationToken.None);

        // One probe + one request per chunk, and the four chunk ranges tile the file exactly.
        AssertEx.Equal(expected: 5, handler.CallCount);
        AssertEx.Equal("bytes=0-0", handler.Requests[0].Range);
        var chunkRanges = handler.Requests.Skip(count: 1).Select(request => request.Range).Order(StringComparer.Ordinal).ToArray();
        AssertEx.Equal(expected: 4, chunkRanges.Length);
        AssertEx.Contains(chunkRanges, "bytes=0-2047");
        AssertEx.Contains(chunkRanges, "bytes=2048-4095");
        AssertEx.Contains(chunkRanges, "bytes=4096-6143");
        AssertEx.Contains(chunkRanges, "bytes=6144-8191");

        // The reassembled file is byte-identical and the sha256 from the probe was still verified.
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(Payload), "The parallel download must reassemble the exact source bytes.");
        AssertEx.True(string.Equals(correctSha, result.Sha256, StringComparison.OrdinalIgnoreCase));
        AssertEx.Equal(Payload.Length, result.SizeBytes);

        // No artifacts survive a successful download.
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path, "*.part"));

        // Aggregate progress never goes backwards even though four chunks report concurrently.
        var completed = progress.Reports.Where(report => report.Status == "downloading").Select(report => report.CompletedBytes).ToArray();
        AssertEx.NotEmpty(completed);
        AssertEx.True(completed.SequenceEqual(completed.Order()), "Aggregate download progress must be monotonic.");
        AssertEx.Contains(progress.Reports, report => report.Status == "completed" && report.CompletedBytes == Payload.Length);
    }

    [Test]
    public async Task ParallelDownload_WhenOriginIgnoresRange_FallsBackToSingleStream()
    {
        using var dir = new Infra.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 4);
        // An origin that answers every request — the probe included — with the whole file and a plain 200.
        using var handler = new Infra.ScriptedHandler((_, _) => FullDownload(Payload));
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var destination = dir.FilePath(Infra.FileName);

        _ = await download.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            Payload.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None);

        // The probe answered 200, so exactly one single-stream GET followed — no chunking was attempted.
        AssertEx.Equal(expected: 2, handler.CallCount);
        AssertEx.Equal("bytes=0-0", handler.Requests[0].Range);
        AssertEx.Null(handler.Requests[1].Range);
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(Payload));
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path, "*.part"));
    }

    [Test]
    public async Task ParallelDownload_Resume_RefetchesOnlyTheIncompleteRange()
    {
        using var dir = new Infra.TempModelsDir();
        // Two connections over 8192 bytes: chunk 0 is [0, 4096), chunk 1 is [4096, 8192).
        var options = ParallelOptions(dir.Path, connections: 2, retries: 0);
        var destination = dir.FilePath(Infra.FileName);
        const int chunkSize = 4096;
        const int chunkOneBytes = 1024;

        // Run 1: chunk 0 completes; chunk 1 delivers 1024 bytes, holds the connection open long enough for its sibling
        // to finish, then ends short. With no retries left, the truncated range surfaces as a network failure.
        using var interrupted = new Infra.ScriptedHandler((request, _) =>
        {
            var from = request.Headers.Range!.Ranges.Single().From!.Value;
            return from == chunkSize
                ? PartialStreamResponse(Payload, (int)from, chunkOneBytes)
                : RangeResponse(Payload, request);
        });
        using var interruptedHttp = new HttpClient(interrupted);
        var interruptedDownload = Infra.DownloadClient(interruptedHttp, Infra.NoTokenStore(), Infra.AbundantSpace(), options);

        var failure = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => interruptedDownload.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            Payload.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.Network, failure.Reason);
        AssertEx.False(File.Exists(destination), "A truncated range must never be committed.");
        // The cursors survive the interruption and record exactly what landed.
        var cursors = await File.ReadAllTextAsync(destination + ".part" + ".ranges.part");
        AssertEx.Equal(string.Create(CultureInfo.InvariantCulture, $"1 {Payload.Length} {chunkSize} {chunkOneBytes}"), cursors);

        // Run 2: a healthy origin. The completed range must not be requested again.
        using var resumed = new Infra.ScriptedHandler((request, _) => RangeResponse(Payload, request));
        using var resumedHttp = new HttpClient(resumed);
        var resumedDownload = Infra.DownloadClient(resumedHttp, Infra.NoTokenStore(), Infra.AbundantSpace(), options);

        _ = await resumedDownload.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            Payload.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None);

        // Only the probe and the remainder of chunk 1 — chunk 0's 4096 bytes were never re-fetched.
        AssertEx.Equal(expected: 2, resumed.CallCount);
        AssertEx.Equal("bytes=0-0", resumed.Requests[0].Range);
        AssertEx.Equal(string.Create(CultureInfo.InvariantCulture, $"bytes={chunkSize + chunkOneBytes}-{Payload.Length - 1}"), resumed.Requests[1].Range);
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(Payload), "A resumed parallel download must still reassemble the exact source bytes.");
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path, "*.part"));
    }

    [Test]
    public async Task ParallelDownload_AdoptsExistingSingleStreamPart_WithoutRefetchingIt()
    {
        using var dir = new Infra.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 2);
        var destination = dir.FilePath(Infra.FileName);
        // A .part left by the single-stream path: contiguous from byte 0, and with no cursors beside it. Chunk 0 is
        // [0, 4096), so 5000 bytes covers all of it and the first 904 of chunk 1.
        const int alreadyFetched = 5000;
        await File.WriteAllBytesAsync(destination + ".part", Payload[..alreadyFetched]);

        using var handler = new Infra.ScriptedHandler((request, _) => RangeResponse(Payload, request));
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);

        _ = await download.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            Payload.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None);

        // Chunk 0 was already covered, so only the probe and the tail of chunk 1 went out.
        AssertEx.Equal(expected: 2, handler.CallCount);
        AssertEx.Equal("bytes=0-0", handler.Requests[0].Range);
        AssertEx.Equal(string.Create(CultureInfo.InvariantCulture, $"bytes={alreadyFetched}-{Payload.Length - 1}"), handler.Requests[1].Range);
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(Payload));
    }

    [Test]
    public async Task ParallelDownload_ConnectionCount_IsClampedToSixteen()
    {
        using var dir = new Infra.TempModelsDir();
        var payload = BuildPayload(16384);
        var options = ParallelOptions(dir.Path, connections: 99);
        using var handler = new Infra.ScriptedHandler((request, _) => RangeResponse(payload, request));
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var destination = dir.FilePath(Infra.FileName);

        _ = await download.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            payload.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None);

        // Clamped to 16 connections: one probe plus 16 chunk requests, not the 99 that were configured.
        AssertEx.Equal(expected: 17, handler.CallCount);
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(payload));
    }

    [Test]
    public async Task ParallelDownload_ConnectionCountBelowOne_UsesSingleStreamWithoutProbing()
    {
        using var dir = new Infra.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 0);
        using var handler = new Infra.ScriptedHandler((_, _) => FullDownload(Payload));
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var destination = dir.FilePath(Infra.FileName);

        _ = await download.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            Payload.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None);

        // A clamped-to-one connection count is the single-stream path verbatim: no range probe at all.
        AssertEx.Equal(expected: 1, handler.CallCount);
        AssertEx.Null(handler.Requests[0].Range);
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(Payload));
    }

    [Test]
    public async Task ParallelDownload_FileBelowThreshold_UsesSingleStreamWithoutProbing()
    {
        using var dir = new Infra.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 4);
        // One byte under the worth-it threshold: splitting would cost more than it returns.
        options.ParallelDownloadMinimumBytes = Payload.Length + 1;
        using var handler = new Infra.ScriptedHandler((_, _) => FullDownload(Payload));
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);
        var destination = dir.FilePath(Infra.FileName);

        _ = await download.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            Payload.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None);

        AssertEx.Equal(expected: 1, handler.CallCount);
        AssertEx.Null(handler.Requests[0].Range);
    }

    private static HuggingFaceOptions ParallelOptions(string modelsDir, int connections, int retries = 2)
    {
        return new HuggingFaceOptions
        {
            ModelsDirectory = modelsDir,
            DiskMarginBytes = 0,
            DefaultQuant = Infra.Quant,
            MaxDownloadRetries = retries,
            DownloadConnections = connections,
            // The production default is 64 MiB; these fixtures work in kilobytes.
            ParallelDownloadMinimumBytes = 1024
        };
    }

    private static byte[] BuildPayload(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++)
        {
            payload[index] = (byte)((index * 31) + 7);
        }

        return payload;
    }

    // Serves exactly the requested range as a 206, advertising the full file length via Content-Range.
    private static HttpResponseMessage RangeResponse(byte[] bytes, HttpRequestMessage request, string? lfsSha256 = null)
    {
        var range = request.Headers.Range!.Ranges.Single();
        var from = (int)range.From!.Value;
        var to = (int)range.To!.Value;
        var slice = bytes[from..(to + 1)];
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice)
        };
        response.Content.Headers.ContentLength = slice.Length;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, bytes.Length);
        response.Headers.TryAddWithoutValidation("X-Repo-Commit", "abc123def456");
        if (lfsSha256 is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Linked-Etag", $"\"{lfsSha256}\"");
        }

        return response;
    }

    // A 206 whose body ends short after delivering <paramref name="deliverBytes" />, simulating a dropped connection.
    private static HttpResponseMessage PartialStreamResponse(byte[] bytes, int from, int deliverBytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new StreamContent(new TruncatingStream(bytes[from..(from + deliverBytes)]))
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.Length - 1, bytes.Length);
        return response;
    }

    private static HttpResponseMessage FullDownload(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentLength = bytes.Length;
        response.Headers.TryAddWithoutValidation("X-Repo-Commit", "abc123def456");
        return response;
    }

    private sealed class ConcurrentProgress : IProgress<PullProgress>
    {
        private readonly Lock _gate = new();
        private readonly List<PullProgress> _reports = [];

        public IReadOnlyList<PullProgress> Reports
        {
            get
            {
                lock (_gate)
                {
                    return [.. _reports];
                }
            }
        }

        public void Report(PullProgress value)
        {
            lock (_gate)
            {
                _reports.Add(value);
            }
        }
    }

    // Yields its payload, then pauses before signalling EOF so a sibling chunk is guaranteed to have finished first,
    // making the "one range interrupted, the others complete" scenario deterministic.
    private sealed class TruncatingStream(byte[] payload) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= payload.Length)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                return 0;
            }

            var toCopy = Math.Min(buffer.Length, payload.Length - _position);
            payload.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return toCopy;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("TruncatingStream is async-only.");
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
}
