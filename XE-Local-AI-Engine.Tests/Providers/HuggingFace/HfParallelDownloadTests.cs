namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Collections.Concurrent;
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
    // The commit the fake origin reports for the caller's mutable "main" ref.
    private const string ProbeCommit = "abc123def456";

    // A per-position-distinct payload: an off-by-one in the chunk maths corrupts the file visibly, which a repeated
    // filler byte would hide.
    private static readonly byte[] Payload = BuildPayload(8192);

    [Test]
    public async Task ParallelDownload_ReassemblesEveryRangeExactly_AndVerifiesHash()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 4);
        var correctSha = Infra.Sha256Upper(Payload);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) => RangeResponse(Payload, request, correctSha));
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
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 4);
        // An origin that answers every request — the probe included — with the whole file and a plain 200.
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(Payload));
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
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        // Two connections over 8192 bytes: chunk 0 is [0, 4096), chunk 1 is [4096, 8192).
        var options = ParallelOptions(dir.Path, connections: 2, retries: 0);
        var destination = dir.FilePath(Infra.FileName);
        const int chunkSize = 4096;
        const int chunkOneBytes = 1024;

        // Run 1: chunk 0 completes; chunk 1 delivers 1024 bytes, holds the connection open long enough for its sibling
        // to finish, then ends short. With no retries left, the truncated range surfaces as a network failure.
        using var interrupted = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
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
        AssertEx.Equal(string.Create(CultureInfo.InvariantCulture, $"2 {Payload.Length} {ProbeCommit} {chunkSize} {chunkOneBytes}"), cursors);

        // Run 2: a healthy origin. The completed range must not be requested again.
        using var resumed = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) => RangeResponse(Payload, request));
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
    public async Task ParallelDownload_WhenResumeSidecarIsTorn_RefetchesEveryRange()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 2, retries: 0);
        var destination = dir.FilePath(Infra.FileName);
        var partPath = destination + ".part";
        const int chunkSize = 4096;

        // Run 1: chunk 1 ends short, so the attempt fails with a pre-sized (sparse) .part and cursors beside it.
        using var interrupted = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
        {
            var from = request.Headers.Range!.Ranges.Single().From!.Value;
            return from == chunkSize ? PartialStreamResponse(Payload, (int)from, deliverBytes: 1024) : RangeResponse(Payload, request);
        });
        using var interruptedHttp = new HttpClient(interrupted);
        var interruptedDownload = Infra.DownloadClient(interruptedHttp, Infra.NoTokenStore(), Infra.AbundantSpace(), options);

        _ = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => interruptedDownload.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            Payload.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None));

        // A crash mid-rewrite leaves the one-line sidecar torn. The .part it describes is full-length but full of holes.
        AssertEx.Equal(Payload.Length, new FileInfo(partPath).Length);
        await File.WriteAllTextAsync(partPath + ".ranges.part", "2 81");

        // Run 2: an unreadable cursor line must not be mistaken for "no cursors at all" — every range is refetched.
        using var resumed = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) => RangeResponse(Payload, request));
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

        AssertEx.Equal(expected: 3, resumed.CallCount);
        AssertEx.Equal("bytes=0-0", resumed.Requests[0].Range);
        var chunkRanges = resumed.Requests.Skip(count: 1).Select(request => request.Range).Order(StringComparer.Ordinal).ToArray();
        AssertEx.Contains(chunkRanges, string.Create(CultureInfo.InvariantCulture, $"bytes=0-{chunkSize - 1}"));
        AssertEx.Contains(chunkRanges, string.Create(CultureInfo.InvariantCulture, $"bytes={chunkSize}-{Payload.Length - 1}"));
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(Payload), "A torn cursor line must never let a sparse partial be committed.");
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path, "*.part"));
    }

    [Test]
    public async Task ParallelDownload_WhenFullLengthPartHasNoSidecar_RefetchesEveryRange()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 2);
        var destination = dir.FilePath(Infra.FileName);
        // A pre-sized parallel .part whose cursors were swept away: its length is the whole file, but its content is
        // holes. Nothing on disk distinguishes it from a finished single-stream .part, so neither may be adopted.
        await File.WriteAllBytesAsync(destination + ".part", new byte[Payload.Length]);

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) => RangeResponse(Payload, request));
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

        AssertEx.Equal(expected: 3, handler.CallCount);
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(Payload), "A full-length .part with no cursors must be refetched, not trusted.");
    }

    [Test]
    public async Task ParallelDownload_AdoptsExistingSingleStreamPart_WithoutRefetchingIt()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 2);
        var destination = dir.FilePath(Infra.FileName);
        // A .part left by the single-stream path: contiguous from byte 0, with a one-cursor record naming the commit
        // that wrote it. Chunk 0 is [0, 4096), so 5000 bytes covers all of it and the first 904 of chunk 1.
        const int alreadyFetched = 5000;
        await File.WriteAllBytesAsync(destination + ".part", Payload[..alreadyFetched]);
        await WriteSidecarAsync(destination, Payload.Length, ProbeCommit, alreadyFetched);

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) => RangeResponse(Payload, request));
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
    public async Task ParallelDownload_WhenSingleStreamPrefixNamesAnotherCommit_RefetchesEveryRange()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 2);
        var destination = dir.FilePath(Infra.FileName);
        // A contiguous prefix whose record names a commit the ref has since moved off. Adopting it would splice the old
        // file's first 5000 bytes onto the new file's tail — exactly the file that never existed upstream.
        const int alreadyFetched = 5000;
        await File.WriteAllBytesAsync(destination + ".part", Payload[..alreadyFetched]);
        await WriteSidecarAsync(destination, Payload.Length, "999888777666", alreadyFetched);

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) => RangeResponse(Payload, request));
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

        // The probe plus both full chunks: nothing of the foreign prefix was kept.
        AssertEx.Equal(expected: 3, handler.CallCount);
        var chunkRanges = handler.Requests.Skip(count: 1).Select(request => request.Range).Order(StringComparer.Ordinal).ToArray();
        AssertEx.Contains(chunkRanges, "bytes=0-4095");
        AssertEx.Contains(chunkRanges, "bytes=4096-8191");
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(Payload), "A prefix from another commit must never be adopted.");
    }

    [Test]
    public async Task ParallelDownload_WhenSingleStreamPrefixHasNoRecord_RefetchesEveryRange()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 2);
        var destination = dir.FilePath(Infra.FileName);
        // A contiguous prefix with nothing beside it — a .part from before the writing commit was recorded. Which
        // version of the file it holds is unknowable, so it earns no head start.
        await File.WriteAllBytesAsync(destination + ".part", Payload[..5000]);

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) => RangeResponse(Payload, request));
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

        AssertEx.Equal(expected: 3, handler.CallCount);
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(Payload), "A prefix with no recorded commit must be refetched, not trusted.");
    }

    [Test]
    public async Task SingleStreamDownload_Resume_PinsTheRecordedCommit_AndKeepsThePrefix()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        // One connection: the single-stream path verbatim, with no range probe in front of it.
        var options = ParallelOptions(dir.Path, connections: 1);
        var destination = dir.FilePath(Infra.FileName);
        const int alreadyFetched = 3000;
        await File.WriteAllBytesAsync(destination + ".part", Payload[..alreadyFetched]);
        await WriteSidecarAsync(destination, Payload.Length, ProbeCommit, alreadyFetched);

        var uris = new List<Uri>();
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
        {
            uris.Add(request.RequestUri!);
            return RangeTailResponse(Payload, request);
        });
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

        // Exactly one request, for the remainder only, asked of the commit that wrote the prefix — not the mutable ref.
        AssertEx.Equal(expected: 1, handler.CallCount);
        AssertEx.Equal(string.Create(CultureInfo.InvariantCulture, $"bytes={alreadyFetched}-"), handler.Requests[0].Range);
        AssertEx.True(uris[0].AbsolutePath.Contains($"/resolve/{ProbeCommit}/", StringComparison.Ordinal),
            "A resumed single stream must be pinned to the commit that wrote the partial.");
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(Payload));
        // A completed single stream cleans up its record exactly as the parallel path does.
        AssertEx.Empty(Directory.EnumerateFiles(dir.Path, "*.part"));
    }

    [Test]
    public async Task SingleStreamDownload_WhenTheRecordedCommitMoved_DiscardsThePartialAndRefetchesFromZero()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 1);
        var destination = dir.FilePath(Infra.FileName);
        const int alreadyFetched = 3000;
        const string movedCommit = "111222333444";
        // Same length, entirely different content: a spliced file would be visible byte-for-byte.
        var moved = Payload.Select(value => (byte)~value).ToArray();

        // The partial holds the OLD commit's bytes; the origin now serves only the new one.
        await File.WriteAllBytesAsync(destination + ".part", Payload[..alreadyFetched]);
        await WriteSidecarAsync(destination, Payload.Length, ProbeCommit, alreadyFetched);

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) => request.Headers.Range is null
            ? FullDownload(moved, movedCommit)
            : RangeTailResponse(moved, request, movedCommit));
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);

        _ = await download.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            moved.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None);

        // Attempt 1 asked to resume and was answered by a different commit — discarded. Attempt 2 sent no Range at all.
        AssertEx.Equal(expected: 2, handler.CallCount);
        AssertEx.Equal(string.Create(CultureInfo.InvariantCulture, $"bytes={alreadyFetched}-"), handler.Requests[0].Range);
        AssertEx.Null(handler.Requests[1].Range);
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(moved), "A moved ref must refetch the whole file, never splice two commits.");
    }

    [Test]
    public async Task SingleStreamDownload_WhenThePartialHasNoRecord_RefetchesFromZero()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 1);
        var destination = dir.FilePath(Infra.FileName);
        // A legacy .part: contiguous, but with no record of what wrote it.
        await File.WriteAllBytesAsync(destination + ".part", Payload[..3000]);

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(Payload));
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

        AssertEx.Equal(expected: 1, handler.CallCount);
        AssertEx.Null(handler.Requests[0].Range);
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(Payload), "An unvouched-for partial must be refetched from byte 0.");
    }

    [Test]
    public async Task ParallelDownload_ConnectionCount_IsClampedToSixteen()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var payload = BuildPayload(16384);
        var options = ParallelOptions(dir.Path, connections: 99);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) => RangeResponse(payload, request));
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
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 0);
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(Payload));
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
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 4);
        // One byte under the worth-it threshold: splitting would cost more than it returns.
        options.ParallelDownloadMinimumBytes = Payload.Length + 1;
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((_, _) => FullDownload(Payload));
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

    [Test]
    public async Task ParallelDownload_PinsEveryChunkToTheProbedCommit_NotTheMutableRef()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 2);
        var destination = dir.FilePath(Infra.FileName);
        // Chunks run concurrently, so the recorder has to be thread-safe; the probe is still enqueued first.
        var uris = new ConcurrentQueue<Uri>();

        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
        {
            uris.Enqueue(request.RequestUri!);
            return RangeResponse(Payload, request);
        });
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

        // The probe resolves the caller's ref; every chunk after it names the commit that ref resolved to, so a branch
        // that advances mid-download cannot hand two chunks bytes from two different commits.
        var requested = uris.ToArray();
        AssertEx.Equal(expected: 3, requested.Length);
        AssertEx.True(requested[0].AbsolutePath.Contains($"/resolve/{Infra.Revision}/", StringComparison.Ordinal),
            "The range probe must still use the caller's revision.");
        AssertEx.True(requested.Skip(count: 1).All(uri => uri.AbsolutePath.Contains($"/resolve/{ProbeCommit}/", StringComparison.Ordinal)),
            "Every chunk must be fetched from the commit the probe resolved.");
    }

    [Test]
    public async Task ParallelDownload_WhenChunkContentRangeDoesNotMatchTheRequest_IsRejectedWithoutCommitting()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 2, retries: 0);
        var destination = dir.FilePath(Infra.FileName);

        // An intermediary that answers each chunk with the right NUMBER of bytes taken from the wrong place. The body
        // would land at the offset we asked for, so only Content-Range can give it away.
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            return range.To == 0 ? RangeResponse(Payload, request) : MisrangedResponse(Payload, request);
        });
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);

        var failure = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => download.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            Payload.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.Network, failure.Reason);
        AssertEx.False(File.Exists(destination), "Bytes from the wrong offset must never be committed.");
        // Rejected before the copy: the pre-sized .part is still untouched, so no mis-ranged body reached the disk.
        var partBytes = await File.ReadAllBytesAsync(destination + ".part");
        AssertEx.Equal(Payload.Length, partBytes.Length);
        AssertEx.True(Array.TrueForAll(partBytes, value => value == 0), "A rejected chunk must be rejected before anything is written.");
    }

    [Test]
    public async Task ParallelDownload_WhenAChunkReportsADifferentCommit_IsRejectedWithoutCommitting()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 2, retries: 0);
        var destination = dir.FilePath(Infra.FileName);

        // The probe pins one commit; the origin then serves a chunk from another (the branch moved under the pin).
        using var handler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
        {
            var range = request.Headers.Range!.Ranges.Single();
            return range.To == 0 ? RangeResponse(Payload, request) : RangeResponse(Payload, request, commit: "999888777666");
        });
        using var http = new HttpClient(handler);
        var download = Infra.DownloadClient(http, Infra.NoTokenStore(), Infra.AbundantSpace(), options);

        var failure = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => download.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            Payload.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None));

        AssertEx.Equal(HuggingFaceDownloadFailure.Network, failure.Reason);
        AssertEx.False(File.Exists(destination), "Chunks from two commits must never be assembled into one file.");
    }

    [Test]
    public async Task ParallelDownload_WhenTheRefMovedBetweenAttempts_RefetchesEveryRange()
    {
        using var dir = new GgufStoreTestInfrastructure.TempModelsDir();
        var options = ParallelOptions(dir.Path, connections: 2, retries: 0);
        var destination = dir.FilePath(Infra.FileName);
        const int chunkSize = 4096;
        // What the moved branch now serves: same length, different content.
        var moved = BuildPayload(Payload.Length).Select(value => (byte)~value).ToArray();

        // Run 1 stops mid-flight against the original commit, leaving cursors that describe ITS bytes.
        using var interrupted = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
        {
            var from = request.Headers.Range!.Ranges.Single().From!.Value;
            return from == chunkSize ? PartialStreamResponse(Payload, (int)from, deliverBytes: 1024) : RangeResponse(Payload, request);
        });
        using var interruptedHttp = new HttpClient(interrupted);
        var interruptedDownload = Infra.DownloadClient(interruptedHttp, Infra.NoTokenStore(), Infra.AbundantSpace(), options);

        _ = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(() => interruptedDownload.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            Payload.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None));

        // Run 2: the mutable ref now resolves to a different commit serving different content. Resuming the old cursors
        // would splice the two versions together into a file that never existed upstream.
        const string movedCommit = "111222333444";
        using var resumed = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) => RangeResponse(moved, request, commit: movedCommit));
        using var resumedHttp = new HttpClient(resumed);
        var resumedDownload = Infra.DownloadClient(resumedHttp, Infra.NoTokenStore(), Infra.AbundantSpace(), options);

        _ = await resumedDownload.DownloadAsync(Infra.RepoId,
            Infra.FileName,
            Infra.Revision,
            Infra.ModelName,
            destination,
            moved.Length,
            expectedSha256: null,
            progress: null,
            CancellationToken.None);

        AssertEx.Equal(expected: 3, resumed.CallCount);
        var finalBytes = await File.ReadAllBytesAsync(destination);
        AssertEx.True(finalBytes.SequenceEqual(moved), "A revision that moved between attempts must refetch, never splice two commits.");
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

    // The v2 resume record both download paths share: "2 <total> <revision> <cursor…>", written beside the .part.
    private static Task WriteSidecarAsync(string destination, long totalBytes, string revision, params long[] cursors)
    {
        var line = string.Join(' ',
        [
            "2", totalBytes.ToString(CultureInfo.InvariantCulture), revision,
            .. cursors.Select(cursor => cursor.ToString(CultureInfo.InvariantCulture))
        ]);
        return File.WriteAllTextAsync(destination + ".part" + ".ranges.part", line);
    }

    // Serves an open-ended "bytes=N-" resume request: everything from N to the end of the file, as a 206.
    private static HttpResponseMessage RangeTailResponse(byte[] bytes, HttpRequestMessage request, string commit = ProbeCommit)
    {
        var from = (int)request.Headers.Range!.Ranges.Single().From!.Value;
        var slice = bytes[from..];
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice)
        };
        response.Content.Headers.ContentLength = slice.Length;
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.Length - 1, bytes.Length);
        response.Headers.TryAddWithoutValidation("X-Repo-Commit", commit);
        return response;
    }

    // Serves exactly the requested range as a 206, advertising the full file length via Content-Range.
    private static HttpResponseMessage RangeResponse(byte[] bytes, HttpRequestMessage request, string? lfsSha256 = null, string commit = ProbeCommit)
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
        response.Headers.TryAddWithoutValidation("X-Repo-Commit", commit);
        if (lfsSha256 is not null)
        {
            response.Headers.TryAddWithoutValidation("X-Linked-Etag", $"\"{lfsSha256}\"");
        }

        return response;
    }

    // A 206 of the right LENGTH whose Content-Range describes a different part of the file (the mirror of what was
    // asked for) — the shape a broken intermediary produces, and the one the assembled file cannot survive.
    private static HttpResponseMessage MisrangedResponse(byte[] bytes, HttpRequestMessage request)
    {
        var range = request.Headers.Range!.Ranges.Single();
        var from = (int)range.From!.Value;
        var to = (int)range.To!.Value;
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(bytes[from..(to + 1)])
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(bytes.Length - 1 - to, bytes.Length - 1 - from, bytes.Length);
        response.Headers.TryAddWithoutValidation("X-Repo-Commit", ProbeCommit);
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

    private static HttpResponseMessage FullDownload(byte[] bytes, string commit = ProbeCommit)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentLength = bytes.Length;
        response.Headers.TryAddWithoutValidation("X-Repo-Commit", commit);
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
    //
    // real-timer: releasing this on a gate would need the test to observe "the sibling ranges finished", and the
    // downloader publishes no per-range completion a fake could wait on. Ordering by elapsed time is the only seam
    // available; no assertion is made on the duration itself.
    private sealed class TruncatingStream(byte[] payload) : Stream
    {
        private static readonly TimeSpan EofPause = TimeSpan.FromMilliseconds(500);

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
                await Task.Delay(EofPause, cancellationToken).ConfigureAwait(false);
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
