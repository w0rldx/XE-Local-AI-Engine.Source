namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     GGUF discovery: repo search filters out non-GGUF repos, per-repo inspection parses
///     quant/size/gated/license + GGUF header metadata via a range read, the LFS sha is optional, and the summary maps
///     popularity fields. No network — a stubbed <see cref="HttpMessageHandler" /> returns canned Hub JSON
///     plus canned GGUF header bytes.
/// </summary>
public sealed class GgufDiscoveryTests
{
    private const string RepoId = "bartowski/Llama-3.2-3B-Instruct-GGUF";
    private const string Commit = "5ab33fa94d1d04e903623ae72c95d1696f09f9e8";

    [Test]
    public async Task GgufDiscovery_IgnoresNonGgufRepos_AndKeepsGgufRepos()
    {
        // One repo with a usable .gguf, one with only non-GGUF files → only the first survives.
        var listing = """
                      [
                        {
                          "id": "owner/Has-Gguf",
                          "gated": false,
                          "downloads": 100,
                          "likes": 5,
                          "lastModified": "2026-01-01T00:00:00.000Z",
                          "siblings": [ { "rfilename": "model-Q4_K_M.gguf" }, { "rfilename": "README.md" } ]
                        },
                        {
                          "id": "owner/No-Gguf",
                          "gated": false,
                          "downloads": 999,
                          "likes": 9,
                          "lastModified": "2026-01-02T00:00:00.000Z",
                          "siblings": [ { "rfilename": "model.safetensors" }, { "rfilename": "config.json" } ]
                        }
                      ]
                      """;

        using var harness = BuildHarness(listing);

        var results = await harness.Discovery.SearchAsync(new GgufSearchQuery(), CancellationToken.None);

        AssertEx.Equal(expected: 1, results.Count);
        AssertEx.Equal("owner/Has-Gguf", results[0].RepoId);
        AssertEx.True(results[0].HasUsableGguf);
    }

    [Test]
    public async Task GgufDiscovery_SortsByPopularity_ReadsDownloadsLikesLastModified()
    {
        var listing = """
                      [
                        {
                          "id": "owner/Popular",
                          "gated": "manual",
                          "downloads": 1234567,
                          "likes": 4321,
                          "lastModified": "2026-05-10T12:34:56.000Z",
                          "cardData": { "license": "apache-2.0" },
                          "siblings": [ { "rfilename": "weights-Q4_K_M.gguf" } ]
                        }
                      ]
                      """;

        using var harness = BuildHarness(listing);

        var results = await harness.Discovery.SearchAsync(new GgufSearchQuery
            {
                Sort = GgufSearchSort.Downloads
            },
            CancellationToken.None);

        AssertEx.Equal(expected: 1, results.Count);
        var summary = results[0];
        AssertEx.Equal(expected: 1234567L, summary.Downloads);
        AssertEx.Equal(expected: 4321, summary.Likes);
        AssertEx.Equal("apache-2.0", summary.License);
        AssertEx.True(summary.IsGated, "gated:\"manual\" must map to IsGated=true.");
        AssertEx.Equal(new DateTimeOffset(year: 2026, month: 5, day: 10, hour: 12, minute: 34, second: 56, TimeSpan.Zero), summary.LastModified);

        // Popularity sort is requested via the Hub sort=downloads query parameter.
        AssertEx.Contains(harness.Handler.LastListUrl, "sort=downloads");
        AssertEx.Contains(harness.Handler.LastListUrl, "filter=gguf");
    }

    [Test]
    public async Task GgufDiscovery_InspectsRepo_ParsesPerFileQuantSizeGatedLicense()
    {
        // Two valid .gguf (different quants), plus one .gguf with no recognizable quant token (skipped, not repo-dropping),
        // plus a non-GGUF sibling (ignored).
        var detail = $$"""
                       {
                         "id": "{{RepoId}}",
                         "sha": "{{Commit}}",
                         "gated": false,
                         "cardData": { "license": "llama3.2" },
                         "siblings": [
                           { "rfilename": "Llama-3.2-3B-Instruct-Q4_K_M.gguf", "size": 2019377440,
                             "lfs": { "sha256": "aaaa", "size": 2019377440 } },
                           { "rfilename": "Llama-3.2-3B-Instruct-Q8_0.gguf", "size": 3421899296,
                             "lfs": { "sha256": "bbbb", "size": 3421899296 } },
                           { "rfilename": "mystery-no-quant.gguf", "size": 10, "lfs": { "sha256": "cccc", "size": 10 } },
                           { "rfilename": "README.md" }
                         ]
                       }
                       """;

        using var harness = BuildHarness(repoDetail: detail, headerBytes: MinimalHeaderBytes());

        var result = await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);

        AssertEx.Equal(RepoId, result.RepoId);
        AssertEx.Equal("llama3.2", result.License);
        AssertEx.False(result.IsGated);
        AssertEx.Equal(expected: 2, result.Files.Count); // unparseable + non-gguf both excluded.

        var q4 = result.Files.Single(f => f.FileName.Contains("Q4_K_M", StringComparison.Ordinal));
        AssertEx.Equal("Q4_K_M", q4.Quant);
        AssertEx.Equal(expected: 2019377440L, q4.SizeBytes);
        AssertEx.Equal("aaaa", q4.Sha256!);
        AssertEx.Equal(Commit, q4.Revision);

        var q8 = result.Files.Single(f => f.FileName.Contains("Q8_0", StringComparison.Ordinal));
        AssertEx.Equal("Q8_0", q8.Quant);
        AssertEx.Equal(expected: 3421899296L, q8.SizeBytes);
    }

    [Test]
    public async Task GgufDiscovery_InspectsRepo_PreservesUnslothDynamicQuantMarker()
    {
        // An Unsloth repo shipping a Dynamic (UD-) file plus a plain file; the UD- marker is preserved as a distinct
        // per-file quant so each is independently selectable.
        var detail = $$"""
                       {
                         "id": "unsloth/gemma-3-12b-it-GGUF",
                         "sha": "{{Commit}}",
                         "gated": false,
                         "siblings": [
                           { "rfilename": "gemma-3-12b-it-UD-Q4_K_XL.gguf", "size": 7000000000,
                             "lfs": { "sha256": "aaaa", "size": 7000000000 } },
                           { "rfilename": "gemma-3-12b-it-Q4_K_M.gguf", "size": 6900000000,
                             "lfs": { "sha256": "bbbb", "size": 6900000000 } }
                         ]
                       }
                       """;

        using var harness = BuildHarness(repoDetail: detail, headerBytes: MinimalHeaderBytes());

        var result = await harness.Discovery.InspectRepoAsync("unsloth/gemma-3-12b-it-GGUF", CancellationToken.None);

        AssertEx.Equal(expected: 2, result.Files.Count);
        AssertEx.Equal("UD-Q4_K_XL", result.Files.Single(f => f.FileName.Contains("UD-Q4_K_XL", StringComparison.Ordinal)).Quant);
        AssertEx.Equal("Q4_K_M", result.Files.Single(f => f.FileName.Contains("it-Q4_K_M", StringComparison.Ordinal)).Quant);
    }

    [Test]
    public async Task GgufDiscovery_InspectsRepo_ExcludesProjectorFiles()
    {
        // A multimodal repo ships an mmproj projector companion alongside the real weights; the projector is not a
        // selectable model file and must be excluded from inspection.
        var detail = $$"""
                       {
                         "id": "{{RepoId}}",
                         "sha": "{{Commit}}",
                         "gated": false,
                         "siblings": [
                           { "rfilename": "model-Q4_K_M.gguf", "size": 100, "lfs": { "sha256": "aaaa", "size": 100 } },
                           { "rfilename": "mmproj-F16.gguf", "size": 50, "lfs": { "sha256": "bbbb", "size": 50 } }
                         ]
                       }
                       """;

        using var harness = BuildHarness(repoDetail: detail, headerBytes: MinimalHeaderBytes());

        var result = await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);

        AssertEx.Equal(expected: 1, result.Files.Count);
        AssertEx.Equal("model-Q4_K_M.gguf", result.Files.Single().FileName);
    }

    [Test]
    public async Task GgufDiscovery_ListRepoFiles_SkipsHeaderReads_AndExcludesProjectors()
    {
        var detail = $$"""
                       {
                         "id": "{{RepoId}}",
                         "sha": "{{Commit}}",
                         "gated": false,
                         "siblings": [
                           { "rfilename": "model-Q4_K_M.gguf", "size": 2048, "lfs": { "sha256": "aaaa", "size": 2048 } },
                           { "rfilename": "mmproj-F16.gguf", "size": 50, "lfs": { "sha256": "bbbb", "size": 50 } }
                         ]
                       }
                       """;

        using var harness = BuildHarness(repoDetail: detail, headerBytes: MinimalHeaderBytes());

        var result = await harness.Discovery.ListRepoFilesAsync(RepoId, CancellationToken.None);

        // mmproj excluded; the real file is present with its quant + size but NO header metadata (the range read is skipped).
        var file = result.Files.Single();
        AssertEx.Equal("model-Q4_K_M.gguf", file.FileName);
        AssertEx.Equal("Q4_K_M", file.Quant);
        AssertEx.Equal(expected: 2048L, file.SizeBytes);
        AssertEx.Null(file.Architecture);
        AssertEx.Null(file.ContextLength);
    }

    [Test]
    public async Task GgufDiscovery_InspectsRepo_ReturnsGgufHeaderMetadata_ViaRangeRead()
    {
        var detail = $$"""
                       {
                         "id": "{{RepoId}}",
                         "sha": "{{Commit}}",
                         "gated": false,
                         "siblings": [
                           { "rfilename": "model-Q4_K_M.gguf", "size": 100, "lfs": { "sha256": "aaaa", "size": 100 } }
                         ]
                       }
                       """;

        // Full header: architecture=llama plus llama.* keys + general.* keys, incl. file_type (the quant enum).
        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "llama")
                     .WithUint32("general.file_type", value: 15) // LLAMA_FTYPE_MOSTLY_Q4_K_M.
                     .WithUint64("general.parameter_count", value: 3_212_749_888UL)
                     .WithUint32("llama.block_count", value: 28)
                     .WithUint32("llama.attention.head_count", value: 24)
                     .WithUint32("llama.attention.head_count_kv", value: 8)
                     .WithUint32("llama.embedding_length", value: 3072)
                     .WithUint32("llama.context_length", value: 131072)
                     .Build();

        using var harness = BuildHarness(repoDetail: detail, headerBytes: header);

        var result = await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);

        var file = result.Files.Single();
        AssertEx.Equal("llama", file.Architecture!);
        AssertEx.Equal(expected: 3_212_749_888L, file.ParamCount!.Value);
        AssertEx.Equal(expected: 28L, file.BlockCount!.Value);
        AssertEx.Equal(expected: 24L, file.AttentionHeadCount!.Value);
        AssertEx.Equal(expected: 8L, file.AttentionHeadCountKV!.Value);
        AssertEx.Equal(expected: 3072L, file.EmbeddingLength!.Value);
        AssertEx.Equal(expected: 131072L, file.ContextLength!.Value);
        // general.file_type (uint enum) is stringified into QuantType.
        AssertEx.Equal("15", file.QuantType!);
    }

    [Test]
    public async Task GgufDiscovery_InspectsRepo_ShaOptional_DoesNotThrow_WhenAbsent()
    {
        // No lfs/sha256 on the sibling → Sha256 must be null; the revision (sha) is still present.
        var detail = $$"""
                       {
                         "id": "{{RepoId}}",
                         "sha": "{{Commit}}",
                         "gated": false,
                         "siblings": [
                           { "rfilename": "model-Q4_K_M.gguf", "size": 4242 }
                         ]
                       }
                       """;

        using var harness = BuildHarness(repoDetail: detail, headerBytes: MinimalHeaderBytes());

        var result = await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);

        var file = result.Files.Single();
        AssertEx.Null(file.Sha256);
        AssertEx.Equal(expected: 4242L, file.SizeBytes);
        AssertEx.Equal(Commit, file.Revision);
    }

    private static byte[] MinimalHeaderBytes()
    {
        return new GgufHeaderBytesBuilder().WithString("general.architecture", "llama").Build();
    }

    private static DiscoveryHarness BuildHarness(string? listing = null, string? repoDetail = null, byte[]? headerBytes = null)
    {
        return new DiscoveryHarness(listing, repoDetail, headerBytes);
    }

    /// <summary>
    ///     Owns the stubbed handler + HTTP clients + wired discovery so a <c>using var</c> in each test disposes them
    ///     deterministically (and CA2000 is satisfied — every disposable is created and owned here).
    /// </summary>
    private sealed class DiscoveryHarness : IDisposable
    {
        private readonly HttpClient _downloadHttp;
        private readonly HttpClient _hubHttp;

        public DiscoveryHarness(string? listing, string? repoDetail, byte[]? headerBytes)
        {
            Handler = new StubHandler(listing, repoDetail, headerBytes);
            _hubHttp = new HttpClient(Handler, disposeHandler: false);
            _downloadHttp = new HttpClient(Handler, disposeHandler: false);

            var options = new HuggingFaceOptions();
            var hubClient = new HfHubClient(_hubHttp, options, NullLogger<HfHubClient>.Instance);
            var headerReader = new GgufHeaderReader(_downloadHttp, options, NullLogger<GgufHeaderReader>.Instance);
            Discovery = new HuggingFaceGgufDiscovery(hubClient, headerReader, options, NullLogger<HuggingFaceGgufDiscovery>.Instance);
        }

        public HuggingFaceGgufDiscovery Discovery { get; }

        public StubHandler Handler { get; }

        public void Dispose()
        {
            _hubHttp.Dispose();
            _downloadHttp.Dispose();
            Handler.Dispose();
        }
    }

    /// <summary>
    ///     Routes by URL: <c>/api/models?filter=gguf</c> → listing JSON; <c>/api/models/{repo}</c> → repo detail JSON;
    ///     <c>/resolve/</c> (a range read) → the canned GGUF header bytes honoring the requested byte range.
    /// </summary>
    private sealed class StubHandler(string? listing = null, string? repoDetail = null, byte[]? headerBytes = null)
        : HttpMessageHandler
    {
        public string LastListUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/resolve/", StringComparison.Ordinal))
            {
                return Task.FromResult(BuildRangeResponse(request, headerBytes ?? []));
            }

            if (url.Contains("/api/models/", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(repoDetail ?? "{}"));
            }

            if (url.Contains("/api/models?", StringComparison.Ordinal))
            {
                LastListUrl = url;
                return Task.FromResult(Json(listing ?? "[]"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
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

            // 206 Partial Content for an honored range; the reader treats a short body as "whole (small) file".
            var status = length < full.Length ? HttpStatusCode.PartialContent : HttpStatusCode.OK;
            return new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(slice)
            };
        }
    }
}
