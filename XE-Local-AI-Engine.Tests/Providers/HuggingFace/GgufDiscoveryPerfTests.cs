namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Perf-lane coverage for the HF discovery seam: bounded-concurrency parallel GGUF header reads (a repo can ship
///     10-25 quant variants; sequential range reads dominated inspection latency), and TTL caching of search
///     listings, repo-blob listings, and GGUF headers (all safe to cache — Hub listings drift slowly, and a header
///     is immutable for a pinned resolved revision). <see cref="HuggingFaceGgufDiscovery.ListRepoFilesAsync" />
///     (the pre-existing header-free selective path backing the quant picker) is covered by
///     <c>GgufDiscoveryTests.GgufDiscovery_ListRepoFiles_SkipsHeaderReads_AndExcludesProjectors</c>.
/// </summary>
public sealed class GgufDiscoveryPerfTests
{
    private const string RepoId = "bartowski/Many-Quant-GGUF";
    private const string Commit = "c0ffee00000000000000000000000000000000";

    // Real, parser-recognized quant tokens so every generated file passes IsUsableGgufFile.
    private static readonly string[] QuantTokens =
    [
        "Q2_K", "Q3_K_S", "Q3_K_M", "Q3_K_L", "Q4_0", "Q4_K_S", "Q4_K_M", "Q5_K_S", "Q5_K_M", "Q6_K"
    ];

    [Test]
    public async Task InspectRepo_ReadsHeadersConcurrently_BoundedByConfiguredCap_AndPreservesPerFileMapping()
    {
        var detail = BuildRepoDetailJson(QuantTokens.Length);
        using var harness = new PerfHarness(repoDetail: detail, headerReadConcurrency: 3, headerDelay: TimeSpan.FromMilliseconds(25));

        var result = await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);

        AssertEx.Equal(QuantTokens.Length, result.Files.Count);

        // Bounded: never more than the configured cap in flight at once.
        AssertEx.True(harness.Handler.MaxObservedConcurrency <= 3,
            $"Observed concurrency {harness.Handler.MaxObservedConcurrency} exceeded the configured cap of 3.");
        // Parallel: with 10 files and a 25ms per-read delay, a sequential implementation could never overlap two
        // reads — seeing more than one in flight at once proves the reads actually run concurrently.
        AssertEx.True(harness.Handler.MaxObservedConcurrency > 1, "Header reads did not overlap; parallelization did not take effect.");

        // Each file's BlockCount was encoded as 100+index in its own canned header. A correct implementation zips
        // the (possibly out-of-order-completing) parallel reads back to their originating file by index/filename.
        for (var i = 0; i < QuantTokens.Length; i++)
        {
            var file = result.Files.Single(f => f.Quant == QuantTokens[i]);
            AssertEx.Equal(expected: 100L + i, file.BlockCount!.Value);
        }
    }

    [Test]
    public async Task InspectRepo_CachesHeaderReads_SecondInspectionDoesNotReReadRange()
    {
        var detail = BuildRepoDetailJson(count: 3);
        using var harness = new PerfHarness(repoDetail: detail);

        await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);
        await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);

        // Every file's header was range-requested exactly once total across BOTH inspections — the second call is
        // served entirely from the header cache.
        AssertEx.Equal(expected: 3, harness.Handler.RangeCallCountByFile.Count);
        foreach (var (fileName, count) in harness.Handler.RangeCallCountByFile)
        {
            AssertEx.Equal(expected: 1, count, $"Expected exactly one range read for {fileName}.");
        }
    }

    [Test]
    public async Task SearchAndRepoDetail_AreCached_SecondCallReusesTheFirstFetch()
    {
        var listing = BuildListingJson();
        var detail = BuildRepoDetailJson(count: 1);
        using var harness = new PerfHarness(listing: listing, repoDetail: detail);

        await harness.Discovery.SearchAsync(new GgufSearchQuery(), CancellationToken.None);
        await harness.Discovery.SearchAsync(new GgufSearchQuery(), CancellationToken.None);
        AssertEx.Equal(expected: 1, harness.Handler.ListCallCount);

        await harness.Discovery.ListRepoFilesAsync(RepoId, CancellationToken.None);
        await harness.Discovery.ListRepoFilesAsync(RepoId, CancellationToken.None);
        AssertEx.Equal(expected: 1, harness.Handler.RepoDetailCallCount);
    }

    [Test]
    public async Task HeaderCache_ExpiresAfterTtl_ReReadsRangeOnNextInspection()
    {
        var detail = BuildRepoDetailJson(count: 1);
        var timeProvider = new FakeTimeProvider();
        using var harness = new PerfHarness(repoDetail: detail, headerCacheTtl: TimeSpan.FromMinutes(1), timeProvider: timeProvider);
        var fileName = FileNameFor(QuantTokens[0]);

        await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);
        AssertEx.Equal(expected: 1, harness.Handler.RangeCallCountByFile[fileName]);

        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);

        AssertEx.Equal(expected: 2, harness.Handler.RangeCallCountByFile[fileName]);
    }

    private static string FileNameFor(string quant)
    {
        return $"model-{quant}.gguf";
    }

    private static string BuildListingJson()
    {
        return $$"""
                 [ { "id": "{{RepoId}}", "gated": false, "downloads": 1, "likes": 1,
                     "siblings": [ { "rfilename": "{{FileNameFor(QuantTokens[0])}}" } ] } ]
                 """;
    }

    private static string BuildRepoDetailJson(int count)
    {
        var siblings = string.Join(",\n",
            Enumerable.Range(0, count)
                      .Select(i => $$"""{ "rfilename": "{{FileNameFor(QuantTokens[i])}}", "size": {{(i + 1) * 1000}}, "lfs": { "sha256": "sha-{{i}}", "size": {{(i + 1) * 1000}} } }"""));

        return $$"""
                 { "id": "{{RepoId}}", "sha": "{{Commit}}", "gated": false, "siblings": [ {{siblings}} ] }
                 """;
    }

    /// <summary>
    ///     Owns the tracking stub handler + HTTP clients + wired discovery stack for one test, with knobs for the
    ///     concurrency cap, per-read delay, cache TTLs, and an injectable <see cref="TimeProvider" /> so tests can
    ///     advance time past a cache TTL deterministically.
    /// </summary>
    private sealed class PerfHarness : IDisposable
    {
        private readonly HttpClient _downloadHttp;
        private readonly HttpClient _hubHttp;

        public PerfHarness(string? listing = null,
            string? repoDetail = null,
            int headerReadConcurrency = 6,
            TimeSpan? headerDelay = null,
            TimeSpan? headerCacheTtl = null,
            TimeProvider? timeProvider = null)
        {
            Handler = new TrackingStubHandler(listing, repoDetail, headerDelay ?? TimeSpan.Zero);
            _hubHttp = new HttpClient(Handler, disposeHandler: false);
            _downloadHttp = new HttpClient(Handler, disposeHandler: false);

            var options = new HuggingFaceOptions
            {
                HeaderReadConcurrency = headerReadConcurrency,
                HeaderCacheTtl = headerCacheTtl ?? TimeSpan.FromDays(30),
                HubMetadataCacheTtl = TimeSpan.FromHours(6)
            };

            var hubClient = new HfHubClient(_hubHttp, options, NullLogger<HfHubClient>.Instance, timeProvider);
            var headerReader = new GgufHeaderReader(_downloadHttp, options, NullLogger<GgufHeaderReader>.Instance, timeProvider);
            Discovery = new HuggingFaceGgufDiscovery(hubClient, headerReader, options, NullLogger<HuggingFaceGgufDiscovery>.Instance);
        }

        public HuggingFaceGgufDiscovery Discovery { get; }

        public TrackingStubHandler Handler { get; }

        public void Dispose()
        {
            _hubHttp.Dispose();
            _downloadHttp.Dispose();
            Handler.Dispose();
        }
    }

    /// <summary>
    ///     Routes by URL like <c>GgufDiscoveryTests.StubHandler</c>, plus per-file canned header bytes (BlockCount =
    ///     100 + the file's index, so a test can verify the parallel reads were zipped back to the right file) and
    ///     call-count/concurrency tracking for the range-read endpoint.
    /// </summary>
    private sealed class TrackingStubHandler(string? listing, string? repoDetail, TimeSpan headerDelay) : HttpMessageHandler
    {
        private int _inFlight;
        private int _listCallCount;
        private int _repoDetailCallCount;
        private int _maxObservedConcurrency;

        public int ListCallCount => _listCallCount;

        public int RepoDetailCallCount => _repoDetailCallCount;

        public int MaxObservedConcurrency => _maxObservedConcurrency;

        public ConcurrentDictionary<string, int> RangeCallCountByFile { get; } = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/resolve/", StringComparison.Ordinal))
            {
                var fileName = url[(url.LastIndexOf('/') + 1)..];
                RangeCallCountByFile.AddOrUpdate(fileName, addValue: 1, (_, existing) => existing + 1);

                var concurrent = Interlocked.Increment(ref _inFlight);
                InterlockedMax(ref _maxObservedConcurrency, concurrent);
                try
                {
                    if (headerDelay > TimeSpan.Zero)
                    {
                        // real-timer: per-request latency is the input of a parallelism measurement — the observed
                        // concurrency above is only meaningful while requests genuinely overlap in time.
                        await Task.Delay(headerDelay, cancellationToken).ConfigureAwait(false);
                    }

                    var index = Array.IndexOf(QuantTokens, fileName.Replace("model-", "", StringComparison.Ordinal).Replace(".gguf", "", StringComparison.Ordinal));
                    return BuildRangeResponse(request, HeaderBytesFor(Math.Max(index, val2: 0)));
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }

            if (url.Contains("/api/models/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _repoDetailCallCount);
                return Json(repoDetail ?? "{}");
            }

            if (url.Contains("/api/models?", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _listCallCount);
                return Json(listing ?? "[]");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static byte[] HeaderBytesFor(int index)
        {
            return new GgufHeaderBytesBuilder()
                   .WithString("general.architecture", "llama")
                   .WithUint32("llama.block_count", value: (uint)(100 + index))
                   .Build();
        }

        private static void InterlockedMax(ref int target, int candidate)
        {
            int initial;
            do
            {
                initial = target;
                if (candidate <= initial)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref target, candidate, initial) != initial);
        }

        private static HttpResponseMessage Json(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        private static HttpResponseMessage BuildRangeResponse(HttpRequestMessage request, byte[] full)
        {
            var range = request.Headers.Range?.Ranges.FirstOrDefault();
            var from = (int)(range?.From ?? 0);
            var to = (int)Math.Min(range?.To ?? full.Length - 1, full.Length - 1);
            var length = Math.Max(val1: 0, Math.Min(to, full.Length - 1) - from + 1);

            var slice = new byte[length];
            if (length > 0)
            {
                Array.Copy(full, from, slice, destinationIndex: 0, length);
            }

            var status = length < full.Length ? HttpStatusCode.PartialContent : HttpStatusCode.OK;
            return new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(slice)
            };
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan timeSpan)
        {
            _utcNow = _utcNow.Add(timeSpan);
        }
    }
}
