namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Multi-part (split) GGUF handling: llama.cpp's <c>-00001-of-00002.gguf</c> split convention must collapse into
///     ONE candidate per logical model+quant (name = first split, size = sum of all splits, header read from the
///     first split only) rather than surface each split as an independent, individually-unloadable file. Live defect
///     (2026-07-10): Qwen/Qwen2.5-Coder-14B-Instruct-GGUF ships Q4_K_M as two splits (8.0GB + 0.99GB); treating them
///     as independent files let the advisor pick the 0.99GB second split alone and estimate a 14B model at ~1.8GB.
/// </summary>
public sealed class GgufDiscoveryShardTests
{
    private const string RepoId = "Qwen/Qwen2.5-Coder-14B-Instruct-GGUF";
    private const string Commit = "5hard0000000000000000000000000000000000";

    [Test]
    public async Task InspectRepo_CollapsesSplitShards_SumsSize_AndReadsHeaderFromFirstShardOnly()
    {
        const string first = "qwen2.5-coder-14b-instruct-q4_k_m-00001-of-00002.gguf";
        const string second = "qwen2.5-coder-14b-instruct-q4_k_m-00002-of-00002.gguf";
        var detail = $$"""
                       { "id": "{{RepoId}}", "sha": "{{Commit}}", "gated": false,
                         "siblings": [
                           { "rfilename": "{{first}}", "size": 8000000000, "lfs": { "sha256": "aaaa", "size": 8000000000 } },
                           { "rfilename": "{{second}}", "size": 990000000, "lfs": { "sha256": "bbbb", "size": 990000000 } }
                         ] }
                       """;

        var header = new GgufHeaderBytesBuilder()
                     .WithString("general.architecture", "qwen2")
                     .WithUint32("qwen2.block_count", value: 48)
                     .Build();

        using var harness = new ShardHarness(detail, new Dictionary<string, byte[]>
        {
            [first] = header
        });

        var result = await harness.Discovery.InspectRepoAsync(RepoId, CancellationToken.None);

        AssertEx.Equal(expected: 1, result.Files.Count);
        var file = result.Files.Single();
        AssertEx.Equal(first, file.FileName);
        AssertEx.Equal(expected: 8000000000L + 990000000L, file.SizeBytes);
        AssertEx.Equal("qwen2", file.Architecture!);
        AssertEx.Equal(expected: 48L, file.BlockCount!.Value);

        // The second shard must never be surfaced as its own candidate, and its header must never be range-read —
        // it carries no GGUF metadata of its own; only the first shard does.
        AssertEx.False(harness.Handler.RequestedRangeFileNames.Contains(second), "The non-first shard's header must never be range-read.");
        AssertEx.Contains(harness.Handler.RequestedRangeFileNames, first);
    }

    [Test]
    public async Task InspectRepo_PrefersMergedSingleFile_OverSplitGroupOfSameQuant()
    {
        const string merged = "model-Q4_K_M.gguf";
        const string shard1 = "model-q4_k_m-00001-of-00002.gguf";
        const string shard2 = "model-q4_k_m-00002-of-00002.gguf";
        var detail = $$"""
                       { "id": "{{RepoId}}", "sha": "{{Commit}}", "gated": false,
                         "siblings": [
                           { "rfilename": "{{merged}}", "size": 9000000000, "lfs": { "sha256": "cccc", "size": 9000000000 } },
                           { "rfilename": "{{shard1}}", "size": 8000000000, "lfs": { "sha256": "aaaa", "size": 8000000000 } },
                           { "rfilename": "{{shard2}}", "size": 990000000, "lfs": { "sha256": "bbbb", "size": 990000000 } }
                         ] }
                       """;

        using var harness = new ShardHarness(detail, new Dictionary<string, byte[]>());

        var result = await harness.Discovery.ListRepoFilesAsync(RepoId, CancellationToken.None);

        AssertEx.Equal(expected: 1, result.Files.Count);
        var file = result.Files.Single();
        AssertEx.Equal(merged, file.FileName);
        AssertEx.Equal(expected: 9000000000L, file.SizeBytes);
    }

    [Test]
    public async Task InspectRepo_DistinctQuantShardGroup_SumsSize_UnrelatedPlainFileUntouched()
    {
        const string shard1 = "model-q8_0-00001-of-00002.gguf";
        const string shard2 = "model-q8_0-00002-of-00002.gguf";
        const string plain = "model-Q4_K_M.gguf";
        var detail = $$"""
                       { "id": "{{RepoId}}", "sha": "{{Commit}}", "gated": false,
                         "siblings": [
                           { "rfilename": "{{shard1}}", "size": 5000000000, "lfs": { "sha256": "aaaa", "size": 5000000000 } },
                           { "rfilename": "{{shard2}}", "size": 4200000000, "lfs": { "sha256": "bbbb", "size": 4200000000 } },
                           { "rfilename": "{{plain}}", "size": 3000000000, "lfs": { "sha256": "cccc", "size": 3000000000 } }
                         ] }
                       """;

        using var harness = new ShardHarness(detail, new Dictionary<string, byte[]>());

        var result = await harness.Discovery.ListRepoFilesAsync(RepoId, CancellationToken.None);

        AssertEx.Equal(expected: 2, result.Files.Count);
        var group = result.Files.Single(f => f.FileName == shard1);
        AssertEx.Equal(expected: 5000000000L + 4200000000L, group.SizeBytes);
        var untouched = result.Files.Single(f => f.FileName == plain);
        AssertEx.Equal(expected: 3000000000L, untouched.SizeBytes);
    }

    [Test]
    public async Task InspectRepo_NoShards_LeavesFilesUntouched()
    {
        var detail = $$"""
                       { "id": "{{RepoId}}", "sha": "{{Commit}}", "gated": false,
                         "siblings": [
                           { "rfilename": "model-Q4_K_M.gguf", "size": 100, "lfs": { "sha256": "aaaa", "size": 100 } },
                           { "rfilename": "model-Q8_0.gguf", "size": 200, "lfs": { "sha256": "bbbb", "size": 200 } }
                         ] }
                       """;

        using var harness = new ShardHarness(detail, new Dictionary<string, byte[]>());

        var result = await harness.Discovery.ListRepoFilesAsync(RepoId, CancellationToken.None);

        AssertEx.Equal(expected: 2, result.Files.Count);
    }

    private sealed class ShardHarness : IDisposable
    {
        private readonly HttpClient _downloadHttp;
        private readonly HttpClient _hubHttp;

        public ShardHarness(string repoDetail, IReadOnlyDictionary<string, byte[]> headerBytesByFileName)
        {
            Handler = new ShardStubHandler(repoDetail, headerBytesByFileName);
            _hubHttp = new HttpClient(Handler, disposeHandler: false);
            _downloadHttp = new HttpClient(Handler, disposeHandler: false);

            var options = new HuggingFaceOptions();
            var hubClient = new HfHubClient(_hubHttp, options, NullLogger<HfHubClient>.Instance);
            var headerReader = new GgufHeaderReader(_downloadHttp, options, NullLogger<GgufHeaderReader>.Instance);
            Discovery = new HuggingFaceGgufDiscovery(hubClient, headerReader, options, NullLogger<HuggingFaceGgufDiscovery>.Instance);
        }

        public HuggingFaceGgufDiscovery Discovery { get; }

        public ShardStubHandler Handler { get; }

        public void Dispose()
        {
            _hubHttp.Dispose();
            _downloadHttp.Dispose();
            Handler.Dispose();
        }
    }

    /// <summary>Routes by URL like <c>GgufDiscoveryTests.StubHandler</c>, plus per-file canned header bytes and a log of every range-read filename.</summary>
    private sealed class ShardStubHandler(string repoDetail, IReadOnlyDictionary<string, byte[]> headerBytesByFileName) : HttpMessageHandler
    {
        public List<string> RequestedRangeFileNames { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("/resolve/", StringComparison.Ordinal))
            {
                var fileName = url[(url.LastIndexOf('/') + 1)..];
                RequestedRangeFileNames.Add(fileName);
                var bytes = headerBytesByFileName.TryGetValue(fileName, out var b) ? b : [];
                return Task.FromResult(BuildRangeResponse(request, bytes));
            }

            if (url.Contains("/api/models/", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(repoDetail));
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

            var status = length < full.Length ? HttpStatusCode.PartialContent : HttpStatusCode.OK;
            return new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(slice)
            };
        }
    }
}
