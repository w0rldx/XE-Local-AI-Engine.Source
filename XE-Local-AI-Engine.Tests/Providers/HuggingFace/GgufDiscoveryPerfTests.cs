namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
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
[Category(TestCategories.Unit)]
public sealed class GgufDiscoveryPerfTests
{
    private const string RepoId = "bartowski/Many-Quant-GGUF";
    private const string Commit = "c0ffee00000000000000000000000000000000";
    private const long ProbeBytes = 64 * 1024;

    // The ~380 KiB large-vocabulary header outgrows 64, 128 and 256 KiB, so a complete read issues four requests.
    private const int CompleteReadRequests = 4;

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
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var harness = new PerfHarness(repoDetail: detail, headerCacheTtl: TimeSpan.FromMinutes(1), timeProvider: timeProvider);
        var fileName = FileNameFor(QuantTokens[0]);

        await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);
        AssertEx.Equal(expected: 1, harness.Handler.RangeCallCountByFile[fileName]);

        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);

        AssertEx.Equal(expected: 2, harness.Handler.RangeCallCountByFile[fileName]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InspectRepo_LargeTokenizer_ReadsItOncePerRepo_AndEveryFileCarriesTheOptionalKeys(bool optionalKeysAfterTokenizer)
    {
        // Normal order, and the mixed order a post-conversion tool produces (optional keys appended after the tokenizer).
        var header = LargeVocabHeader(blockCount: 32, keyLength: 256, optionalKeysAfterTokenizer);
        using var harness = new PerfHarness(repoDetail: BuildRepoDetailJson(count: 3), headerBytesFor: _ => header, headerProbeBytes: ProbeBytes);

        var files = (await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None)).Files;

        // One first probe per file, plus ONE complete read of the smallest file (64, 128, 256, 512 KiB).
        AssertEx.Equal(expected: 1 + CompleteReadRequests, RangeCalls(harness, QuantTokens[0]));
        AssertEx.Equal(expected: 1, RangeCalls(harness, QuantTokens[1]));
        AssertEx.Equal(expected: 1, RangeCalls(harness, QuantTokens[2]));
        foreach (var file in files)
        {
            AssertEx.Equal(expected: 4_000_000_000L, file.ParamCount!.Value);
            AssertEx.Equal(expected: 32L, file.BlockCount!.Value);
            AssertEx.Equal(expected: 256L, file.AttentionKeyLength!.Value);
            AssertEx.Equal(expected: 256L, file.AttentionValueLength!.Value);
            AssertEx.Equal(expected: 4L, file.FullAttentionInterval!.Value);
            // The quant label is per file: never borrowed from the complete read of another file.
            AssertEx.Null(file.QuantType);
        }
    }

    [Test]
    public async Task InspectRepo_TwoArchitectureSignatures_OneCompleteReadEach_AndNoValuesCross()
    {
        var model = LargeVocabHeader(blockCount: 32, keyLength: 256, optionalKeysAfterTokenizer: true);
        var drafter = LargeVocabHeader(blockCount: 2, keyLength: 64, optionalKeysAfterTokenizer: true);
        using var harness = new PerfHarness(repoDetail: BuildRepoDetailJson(count: 3),
            headerBytesFor: fileName => fileName == FileNameFor(QuantTokens[2]) ? drafter : model,
            headerProbeBytes: ProbeBytes);

        var files = (await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None)).Files;

        AssertEx.Equal(expected: 1 + CompleteReadRequests, RangeCalls(harness, QuantTokens[0]));
        AssertEx.Equal(expected: 1, RangeCalls(harness, QuantTokens[1]));
        AssertEx.Equal(expected: 1 + CompleteReadRequests, RangeCalls(harness, QuantTokens[2]));
        foreach (var quant in QuantTokens[..2])
        {
            var file = files.Single(f => f.Quant == quant);
            AssertEx.Equal(expected: 32L, file.BlockCount!.Value);
            AssertEx.Equal(expected: 256L, file.AttentionKeyLength!.Value);
        }

        var draft = files.Single(f => f.Quant == QuantTokens[2]);
        AssertEx.Equal(expected: 2L, draft.BlockCount!.Value);
        AssertEx.Equal(expected: 64L, draft.AttentionKeyLength!.Value);
    }

    [Test]
    public async Task InspectRepo_CompleteReadFails_KeepsThePartialMetadata()
    {
        var header = LargeVocabHeader(blockCount: 32, keyLength: 256, optionalKeysAfterTokenizer: true);
        using var harness = new PerfHarness(repoDetail: BuildRepoDetailJson(count: 3),
            headerBytesFor: _ => header,
            headerProbeBytes: ProbeBytes,
            failRangesAbove: ProbeBytes);

        var files = (await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None)).Files;

        AssertEx.Equal(expected: 3, files.Count);
        foreach (var file in files)
        {
            AssertEx.Equal(expected: 32L, file.BlockCount!.Value);
            AssertEx.Null(file.AttentionKeyLength);
        }
    }

    [Test]
    public async Task InspectRepo_SameSignature_DifferentAttentionGeometry_EachFileKeepsItsOwn()
    {
        // Same five-field signature; the key length sits before the tokenizer, so each early read already found its own.
        var smallest = LargeVocabHeader(blockCount: 32, keyLength: 256, optionalKeysAfterTokenizer: false);
        var other = LargeVocabHeader(blockCount: 32, keyLength: 128, optionalKeysAfterTokenizer: false);
        using var harness = new PerfHarness(repoDetail: BuildRepoDetailJson(count: 2),
            headerBytesFor: fileName => fileName == FileNameFor(QuantTokens[0]) ? smallest : other,
            headerProbeBytes: ProbeBytes);

        var files = (await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None)).Files;

        AssertEx.Equal(expected: 1 + CompleteReadRequests, RangeCalls(harness, QuantTokens[0]));
        AssertEx.Equal(expected: 1, RangeCalls(harness, QuantTokens[1]));
        AssertEx.Equal(expected: 256L, files.Single(f => f.Quant == QuantTokens[0]).AttentionKeyLength!.Value);
        AssertEx.Equal(expected: 128L, files.Single(f => f.Quant == QuantTokens[1]).AttentionKeyLength!.Value);
    }

    [Test]
    public async Task InspectRepo_CompleteReadLacksAFieldTheFileHas_TheFileKeepsItsOwn()
    {
        // The representative's complete read never finds the optional keys (as when it stops truncated at the cap).
        var smallest = LargeVocabHeader(blockCount: 32, keyLength: 256, optionalKeysAfterTokenizer: true, includeOptionalKeys: false);
        var other = LargeVocabHeader(blockCount: 32, keyLength: 128, optionalKeysAfterTokenizer: false);
        using var harness = new PerfHarness(repoDetail: BuildRepoDetailJson(count: 2),
            headerBytesFor: fileName => fileName == FileNameFor(QuantTokens[0]) ? smallest : other,
            headerProbeBytes: ProbeBytes);

        var files = (await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None)).Files;

        AssertEx.Equal(expected: 1 + CompleteReadRequests, RangeCalls(harness, QuantTokens[0]));
        AssertEx.Null(files.Single(f => f.Quant == QuantTokens[0]).AttentionKeyLength);
        var kept = files.Single(f => f.Quant == QuantTokens[1]);
        AssertEx.Equal(expected: 128L, kept.AttentionKeyLength!.Value);
        AssertEx.Equal(expected: 4L, kept.FullAttentionInterval!.Value);
    }

    [Test]
    public void FillMissing_FillsEveryNullableField_ExceptTheFilesOwnQuantType()
    {
        // Reflection, so a field added to GgufHeaderMetadata later is covered without editing this test.
        var nullableFields = typeof(GgufHeaderMetadata).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                                       .Where(static p => p.CanWrite && (p.PropertyType == typeof(string) || Nullable.GetUnderlyingType(p.PropertyType) is not null))
                                                       .ToList();
        var complete = GgufHeaderMetadata.Empty with { };
        foreach (var field in nullableFields)
        {
            field.SetValue(complete, field.PropertyType == typeof(string) ? "set" : Convert.ChangeType(1, Nullable.GetUnderlyingType(field.PropertyType)!, CultureInfo.InvariantCulture));
        }

        var merged = HuggingFaceGgufDiscovery.FillMissing(GgufHeaderMetadata.Empty with { }, complete);

        AssertEx.True(nullableFields.Count > 10, "the reflection must find the header fields.");
        AssertEx.Null(merged.QuantType, "the quant label is per file, never borrowed.");
        var missing = nullableFields.Where(static p => p.Name != nameof(GgufHeaderMetadata.QuantType)).Where(p => p.GetValue(merged) is null).Select(static p => p.Name).ToList();
        AssertEx.Empty(missing, $"FillMissing drops: {string.Join(", ", missing)}");
    }

    [Test]
    public async Task HeaderCache_HoldsMoreThan256Headers_SecondPassMakesNoRequest()
    {
        using var harness = new PerfHarness();
        var fileNames = Enumerable.Range(0, 300).Select(static i => $"file-{i}.gguf").ToList();

        foreach (var fileName in fileNames)
        {
            await harness.HeaderReader.ReadHeaderAsync(RepoId, fileName, Commit, CancellationToken.None);
        }

        foreach (var fileName in fileNames)
        {
            await harness.HeaderReader.ReadHeaderAsync(RepoId, fileName, Commit, CancellationToken.None);
        }

        AssertEx.Equal(expected: 300, harness.Handler.RangeCallCountByFile.Count);
        AssertEx.Equal(expected: 300, harness.Handler.RangeCallCountByFile.Values.Sum());
    }

    private static int RangeCalls(PerfHarness harness, string quant)
    {
        return harness.Handler.RangeCallCountByFile.GetValueOrDefault(FileNameFor(quant));
    }

    // The fit-relevant keys come first, then a vocabulary far past the 64 KiB probe; the optional keys sit before or after it.
    private static byte[] LargeVocabHeader(uint blockCount, uint keyLength, bool optionalKeysAfterTokenizer, bool includeOptionalKeys = true)
    {
        var builder = new GgufHeaderBytesBuilder()
                      .WithString("general.architecture", "qwen35")
                      .WithUint64("general.parameter_count", value: 4_000_000_000UL)
                      .WithUint32("qwen35.block_count", blockCount)
                      .WithUint32("qwen35.embedding_length", value: 2560)
                      .WithUint32("qwen35.attention.head_count", value: 16)
                      .WithUint32("qwen35.attention.head_count_kv", value: 4);
        if (!optionalKeysAfterTokenizer && includeOptionalKeys)
        {
            WithOptionalKeys(builder, keyLength);
        }

        builder.WithString("tokenizer.ggml.model", "gpt2")
               .WithStringArray("tokenizer.ggml.tokens", BuildVocab(20_000))
               .WithString("tokenizer.chat_template", "{{ messages }}");
        if (optionalKeysAfterTokenizer && includeOptionalKeys)
        {
            WithOptionalKeys(builder, keyLength);
        }

        return builder.WithUint32("general.file_type", value: 15).Build();
    }

    private static void WithOptionalKeys(GgufHeaderBytesBuilder builder, uint keyLength)
    {
        builder.WithUint32("qwen35.attention.key_length", keyLength)
               .WithUint32("qwen35.attention.value_length", keyLength)
               .WithUint32("qwen35.full_attention_interval", value: 4);
    }

    [Test]
    public async Task InspectRepo_ArchitectureKeysBehindTheTokenizer_StillGrowsUntilTheyParse()
    {
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "llama")
                     .WithStringArray("tokenizer.ggml.tokens", BuildVocab(20_000))
                     .WithUint32("llama.block_count", value: 28)
                     .WithUint32("llama.embedding_length", value: 3072)
                     .WithUint32("llama.attention.head_count", value: 24)
                     .WithUint32("llama.attention.head_count_kv", value: 8)
                     .Build();
        using var harness = new PerfHarness(repoDetail: BuildRepoDetailJson(count: 1), headerBytesFor: _ => header, headerProbeBytes: ProbeBytes);

        var file = (await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None)).Files.Single();

        AssertEx.True(harness.Handler.RangeCallCountByFile[FileNameFor(QuantTokens[0])] > 1, "an incomplete architecture block must keep growing the range.");
        AssertEx.Equal(expected: 28L, file.BlockCount!.Value);
        AssertEx.Equal(expected: 8L, file.AttentionHeadCountKV!.Value);
    }

    private static string[] BuildVocab(int count)
    {
        return [.. Enumerable.Range(0, count).Select(static i => $"token_{i}")];
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
            TimeProvider? timeProvider = null,
            Func<string, byte[]>? headerBytesFor = null,
            long headerProbeBytes = 4L * 1024 * 1024,
            long? failRangesAbove = null)
        {
            Handler = new TrackingStubHandler(listing, repoDetail, headerDelay ?? TimeSpan.Zero, headerBytesFor, failRangesAbove);
            _hubHttp = new HttpClient(Handler, disposeHandler: false);
            _downloadHttp = new HttpClient(Handler, disposeHandler: false);

            var options = new HuggingFaceOptions
            {
                HeaderReadConcurrency = headerReadConcurrency,
                HeaderCacheTtl = headerCacheTtl ?? TimeSpan.FromDays(30),
                HubMetadataCacheTtl = TimeSpan.FromHours(6),
                HeaderProbeBytes = headerProbeBytes
            };

            var clock = timeProvider ?? TimeProvider.System;
            var hubClient = new HfHubClient(_hubHttp, options, NullLogger<HfHubClient>.Instance, clock);
            HeaderReader = new GgufHeaderReader(_downloadHttp, options, NullLogger<GgufHeaderReader>.Instance, clock);
            Discovery = new HuggingFaceGgufDiscovery(hubClient, HeaderReader, options, NullLogger<HuggingFaceGgufDiscovery>.Instance);
        }

        public HuggingFaceGgufDiscovery Discovery { get; }

        public GgufHeaderReader HeaderReader { get; }

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
    private sealed class TrackingStubHandler : HttpMessageHandler
    {
        private readonly string? _listing;
        private readonly string? _repoDetail;
        private readonly TimeSpan _headerDelay;
        private readonly Func<string, byte[]>? _headerBytesFor;
        private readonly long? _failRangesAbove;
        private int _inFlight;
        private int _listCallCount;
        private int _repoDetailCallCount;
        private int _maxObservedConcurrency;

        public TrackingStubHandler(string? listing, string? repoDetail, TimeSpan headerDelay, Func<string, byte[]>? headerBytesFor, long? failRangesAbove)
        {
            _listing = listing;
            _repoDetail = repoDetail;
            _headerDelay = headerDelay;
            _headerBytesFor = headerBytesFor;
            _failRangesAbove = failRangesAbove;
        }

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
                    if (_headerDelay > TimeSpan.Zero)
                    {
                        // real-timer: per-request latency is the input of a parallelism measurement — the observed
                        // concurrency above is only meaningful while requests genuinely overlap in time.
                        await Task.Delay(_headerDelay, cancellationToken);
                    }

                    if (request.Headers.Range?.Ranges.FirstOrDefault()?.To + 1 > _failRangesAbove)
                    {
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    }

                    var index = Array.IndexOf(QuantTokens, fileName.Replace("model-", "", StringComparison.Ordinal).Replace(".gguf", "", StringComparison.Ordinal));
                    return BuildRangeResponse(request, _headerBytesFor?.Invoke(fileName) ?? HeaderBytesFor(Math.Max(index, val2: 0)));
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }

            if (url.Contains("/api/models/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _repoDetailCallCount);
                return Json(_repoDetail ?? "{}");
            }

            if (url.Contains("/api/models?", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _listCallCount);
                return Json(_listing ?? "[]");
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
}
