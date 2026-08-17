namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <c>unsloth/gemma-4-12b-it-GGUF</c> ships speculative-decoding drafters under
///     <c>MTP/</c> whose file names parse to the SAME quant tokens as the root weights. Flattening them into one list
///     put three 0.4-0.8 GB drafters at the TOP of the ladder (it sorts ascending by size), each labelled with the base
///     model's quant — so <c>Q8_0</c> meant either 0.4 GB or 11.8 GB depending on which row was clicked, and both mapped
///     to the same <c>{repoId}:{quant}</c> registry key. The file list below is the repo's real layout and sizes.
/// </summary>
public sealed class GgufDiscoveryDraftModelTests
{
    private const string RepoId = "unsloth/gemma-4-12b-it-GGUF";
    private const string Commit = "dfaf700000000000000000000000000000000000";

    // The real repo: three root quants and three MTP drafters, two of which collide with a root quant by token.
    private const string RepoDetailJson = $$"""
                                            { "id": "{{RepoId}}", "sha": "{{Commit}}", "gated": false,
                                              "siblings": [
                                                { "rfilename": "gemma-4-12b-it-Q8_0.gguf", "size": 11800000000, "lfs": { "sha256": "a1", "size": 11800000000 } },
                                                { "rfilename": "gemma-4-12b-it-BF16.gguf", "size": 22200000000, "lfs": { "sha256": "a2", "size": 22200000000 } },
                                                { "rfilename": "gemma-4-12b-it-UD-Q4_K_XL.gguf", "size": 7800000000, "lfs": { "sha256": "a3", "size": 7800000000 } },
                                                { "rfilename": "MTP/mtp-gemma-4-12b-it-Q8_0.gguf", "size": 400000000, "lfs": { "sha256": "b1", "size": 400000000 } },
                                                { "rfilename": "MTP/mtp-gemma-4-12b-it-BF16.gguf", "size": 800000000, "lfs": { "sha256": "b2", "size": 800000000 } },
                                                { "rfilename": "MTP/mtp-gemma-4-12b-it-F16.gguf", "size": 800000000, "lfs": { "sha256": "b3", "size": 800000000 } }
                                              ] }
                                            """;

    [Test]
    public async Task ListRepoFiles_GivesEachMtpDrafterADistinctQuantLabel_SoNoTwoRowsShareOne()
    {
        using var harness = new DraftHarness(RepoDetailJson);

        var result = await harness.Discovery.ListRepoFilesAsync(RepoId, CancellationToken.None);

        // Every file is still offered — a drafter is downloadable, the draft-* speculative modes need one.
        AssertEx.Equal(expected: 6, result.Files.Count);

        // THE defect: before the fix, "Q8_0" and "BF16" each named two files of wildly different size.
        var duplicateLabels = result.Files
                                    .GroupBy(static file => file.Quant, StringComparer.OrdinalIgnoreCase)
                                    .Where(static group => group.Count() > 1)
                                    .Select(static group => group.Key)
                                    .ToList();
        AssertEx.Equal(expected: 0,
            duplicateLabels.Count,
            $"No quant label may name two different files; duplicated: {string.Join(", ", duplicateLabels)}.");
    }

    [Test]
    public async Task ListRepoFiles_MarksOnlyTheDrafters_LeavingTheBaseQuantsUntouched()
    {
        using var harness = new DraftHarness(RepoDetailJson);

        var result = await harness.Discovery.ListRepoFilesAsync(RepoId, CancellationToken.None);

        AssertEx.Equal("MTP-Q8_0", QuantOf(result, "MTP/mtp-gemma-4-12b-it-Q8_0.gguf"));
        AssertEx.Equal("MTP-BF16", QuantOf(result, "MTP/mtp-gemma-4-12b-it-BF16.gguf"));
        AssertEx.Equal("MTP-F16", QuantOf(result, "MTP/mtp-gemma-4-12b-it-F16.gguf"));

        AssertEx.Equal("Q8_0", QuantOf(result, "gemma-4-12b-it-Q8_0.gguf"));
        AssertEx.Equal("BF16", QuantOf(result, "gemma-4-12b-it-BF16.gguf"));
        // The Unsloth Dynamic marker still survives untouched alongside the new draft marker.
        AssertEx.Equal("UD-Q4_K_XL", QuantOf(result, "gemma-4-12b-it-UD-Q4_K_XL.gguf"));

        foreach (var file in result.Files)
        {
            var expectedDraft = file.FileName.StartsWith("MTP/", StringComparison.Ordinal);
            AssertEx.Equal(expectedDraft,
                GgufDraftModel.IsDraftQuant(file.Quant),
                $"'{file.FileName}' draft classification is wrong.");
        }
    }

    [Test]
    public async Task ListRepoFiles_GivesTheDrafterAndTheRealModelDistinctRegistryKeys()
    {
        using var harness = new DraftHarness(RepoDetailJson);

        var result = await harness.Discovery.ListRepoFilesAsync(RepoId, CancellationToken.None);

        var realKey = GgufModelName.Format(RepoId, QuantOf(result, "gemma-4-12b-it-Q8_0.gguf"));
        var draftKey = GgufModelName.Format(RepoId, QuantOf(result, "MTP/mtp-gemma-4-12b-it-Q8_0.gguf"));

        AssertEx.Equal("unsloth/gemma-4-12b-it-GGUF:Q8_0", realKey);
        AssertEx.Equal("unsloth/gemma-4-12b-it-GGUF:MTP-Q8_0", draftKey);

        // A key round-trips through the store's own parser, so an MTP- request still resolves to the drafter.
        AssertEx.Equal("MTP-Q8_0", GgufModelName.Parse(draftKey).Quant!);
    }

    [Test]
    public async Task ListRepoFiles_WhenAnMtpNamedRepoShipsOnlyBaseWeights_MarksNothing()
    {
        // unsloth/Qwen3.6-27B-MTP-GGUF: the repo NAME advertises MTP layers, but every file is the real model. It ran
        // the live matrix at 21.3 GB / 26.7 GB VRAM — a name-based rule would have deleted it from the picker.
        const string mtpNamedRepo = "unsloth/Qwen3.6-27B-MTP-GGUF";
        var detail = $$"""
                       { "id": "{{mtpNamedRepo}}", "sha": "{{Commit}}", "gated": false,
                         "siblings": [
                           { "rfilename": "Qwen3.6-27B-MTP-Q6_K.gguf", "size": 21300000000, "lfs": { "sha256": "c1", "size": 21300000000 } },
                           { "rfilename": "Qwen3.6-27B-MTP-Q4_K_M.gguf", "size": 15000000000, "lfs": { "sha256": "c2", "size": 15000000000 } }
                         ] }
                       """;

        using var harness = new DraftHarness(detail);

        var result = await harness.Discovery.ListRepoFilesAsync(mtpNamedRepo, CancellationToken.None);

        AssertEx.Equal(expected: 2, result.Files.Count);
        foreach (var file in result.Files)
        {
            AssertEx.False(GgufDraftModel.IsDraftQuant(file.Quant), $"'{file.FileName}' is a base quant, not a drafter.");
        }
    }

    private static string QuantOf(GgufRepoDetail detail, string fileName)
    {
        return detail.Files.Single(file => string.Equals(file.FileName, fileName, StringComparison.Ordinal)).Quant;
    }

    private sealed class DraftHarness : IDisposable
    {
        private readonly StubHandler _handler;
        private readonly HttpClient _hubHttp;

        public DraftHarness(string repoDetail)
        {
            _handler = new StubHandler(repoDetail);
            _hubHttp = new HttpClient(_handler, disposeHandler: false);

            var options = new HuggingFaceOptions();
            var hubClient = new HfHubClient(_hubHttp, options, NullLogger<HfHubClient>.Instance);
            var headerReader = new GgufHeaderReader(_hubHttp, options, NullLogger<GgufHeaderReader>.Instance);
            Discovery = new HuggingFaceGgufDiscovery(hubClient, headerReader, options, NullLogger<HuggingFaceGgufDiscovery>.Instance);
        }

        public HuggingFaceGgufDiscovery Discovery { get; }

        public void Dispose()
        {
            _hubHttp.Dispose();
            _handler.Dispose();
        }
    }

    // Serves only the repo-detail endpoint: every assertion here uses the header-free ListRepoFilesAsync path.
    private sealed class StubHandler(string repoDetail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.ToString().Contains("/api/models/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(repoDetail, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
