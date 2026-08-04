namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Image-model discovery over the Hugging Face Hub. The rules that matter here are the ones that DIFFER from the
///     GGUF lane, because copying that lane wholesale would return almost nothing: a diffusion file-set legitimately
///     mixes <c>.gguf</c> weights with <c>.safetensors</c> VAEs/encoders, and none of a VAE, a CLIP encoder or a
///     <c>.safetensors</c> diffusion model carries a parseable llama.cpp quant token. Sharded weights are excluded on
///     purpose (a part is one file end-to-end), and an untrusted repo file name can never reach a picker.
///     No network — a stubbed <see cref="HttpMessageHandler" /> returns canned Hub JSON.
/// </summary>
public sealed class ImageModelDiscoveryTests
{
    [Test]
    public async Task ImageDiscovery_ListUrl_IsPinnedToTheTextToImageQueryShape()
    {
        // pipeline_tag=text-to-image is what makes this a diffusion search at all; without it the endpoint returns the
        // Hub's entire model list. Pinned as a whole string so the facet cannot silently change or disappear.
        using var harness = BuildHarness("[]");

        await harness.Discovery.SearchAsync(new ImageModelSearchQuery(), CancellationToken.None);

        AssertEx.Equal("https://huggingface.co/api/models?pipeline_tag=text-to-image&full=true&direction=-1&sort=trendingScore&limit=20",
            harness.Handler.LastListUrl);
    }

    [Test]
    public async Task ImageDiscovery_GgufOnly_AddsTheTagFilter_WithoutDroppingThePipelineTag()
    {
        using var harness = BuildHarness("[]");

        await harness.Discovery.SearchAsync(new ImageModelSearchQuery
            {
                GgufOnly = true,
                Limit = 5,
                Sort = ImageModelSearchSort.Downloads
            },
            CancellationToken.None);

        // filter=<tag>, NOT library=<tag>: community packagers report library_name "None" and would be under-matched.
        AssertEx.Equal("https://huggingface.co/api/models?filter=gguf&pipeline_tag=text-to-image&full=true&direction=-1&sort=downloads&limit=5",
            harness.Handler.LastListUrl);
    }

    [Test]
    public async Task ImageDiscovery_KeepsSafetensorsOnlyRepos_AndDropsReposWithNoWeights()
    {
        // The GGUF lane would drop BOTH of these: the first ships no .gguf, and neither file name parses to a quant.
        // A .safetensors-only repo is exactly where a VAE and a text encoder live, so it must survive.
        var listing = """
                      [
                        {
                          "id": "Comfy-Org/Qwen-Image_ComfyUI",
                          "gated": false,
                          "downloads": 100,
                          "likes": 5,
                          "lastModified": "2026-01-01T00:00:00.000Z",
                          "siblings": [
                            { "rfilename": "split_files/vae/qwen_image_vae.safetensors" },
                            { "rfilename": "README.md" }
                          ]
                        },
                        {
                          "id": "owner/Docs-Only",
                          "downloads": 999,
                          "siblings": [ { "rfilename": "README.md" }, { "rfilename": "config.json" } ]
                        }
                      ]
                      """;

        using var harness = BuildHarness(listing);

        var results = await harness.Discovery.SearchAsync(new ImageModelSearchQuery(), CancellationToken.None);

        AssertEx.Equal(expected: 1, results.Count);
        AssertEx.Equal("Comfy-Org/Qwen-Image_ComfyUI", results[0].RepoId);
        AssertEx.True(results[0].HasUsableWeights);
    }

    [Test]
    public async Task ImageDiscovery_Search_TagsGatingAndPublisherTrust_WithoutFilteringEitherOut()
    {
        var listing = """
                      [
                        {
                          "id": "Qwen/Qwen-Image",
                          "gated": "manual",
                          "downloads": 1234,
                          "likes": 56,
                          "lastModified": "2026-05-10T12:34:56.000Z",
                          "cardData": { "license": "apache-2.0" },
                          "siblings": [ { "rfilename": "transformer.safetensors" } ]
                        },
                        {
                          "id": "randomuser/my-diffusion",
                          "downloads": 7,
                          "siblings": [ { "rfilename": "model-Q4_0.gguf" } ]
                        }
                      ]
                      """;

        using var harness = BuildHarness(listing);

        var results = await harness.Discovery.SearchAsync(new ImageModelSearchQuery(), CancellationToken.None);

        AssertEx.Equal(expected: 2, results.Count, "Gating and publisher trust are badges, never exclusion gates.");
        var qwen = results.Single(static r => r.RepoId == "Qwen/Qwen-Image");
        AssertEx.True(qwen.IsGated, "gated:\"manual\" means a one-click install would 401 without a token — the UI must be able to say so.");
        AssertEx.True(qwen.IsTrustedPublisher);
        AssertEx.Equal("apache-2.0", qwen.License);
        AssertEx.False(results.Single(static r => r.RepoId == "randomuser/my-diffusion").IsTrustedPublisher);
    }

    [Test]
    public async Task ImageDiscovery_InspectRepo_ReturnsBothFormatsWithSizes_AndSuggestsRoles()
    {
        var detail = """
                     {
                       "id": "second-state/FLUX.1-schnell-GGUF",
                       "gated": false,
                       "sha": "abc123",
                       "cardData": { "license": "apache-2.0" },
                       "siblings": [
                         { "rfilename": "flux1-schnell-Q4_0.gguf", "lfs": { "size": 6688845536, "sha256": "aa" } },
                         { "rfilename": "ae.safetensors", "lfs": { "size": 335304388 } },
                         { "rfilename": "clip_l.safetensors", "lfs": { "size": 246144152 } },
                         { "rfilename": "t5xxl-Q4_K.gguf", "lfs": { "size": 2752844256 } },
                         { "rfilename": "README.md", "size": 1024 }
                       ]
                     }
                     """;

        using var harness = BuildHarness(repoDetail: detail);

        var result = await harness.Discovery.InspectRepoAsync("second-state/FLUX.1-schnell-GGUF", CancellationToken.None);

        AssertEx.Equal(expected: 4, result.Files.Count, "The non-weight README must be dropped; every weight file must survive.");
        AssertEx.Equal("apache-2.0", result.License);

        var diffusion = result.Files.Single(static f => f.FileName == "flux1-schnell-Q4_0.gguf");
        AssertEx.Equal(ImageWeightFormat.Gguf, diffusion.Format);
        AssertEx.Equal(expected: 6_688_845_536L, diffusion.SizeBytes);
        AssertEx.Equal("aa", diffusion.Sha256);
        AssertEx.Equal(ImageModelPartRole.Diffusion, diffusion.SuggestedRole);

        // The three companion parts are what a picker has to pre-fill; guessing them wrong means the operator hand-maps
        // four dropdowns, which is the workflow this whole stage replaces.
        AssertEx.Equal(ImageModelPartRole.Vae, result.Files.Single(static f => f.FileName == "ae.safetensors").SuggestedRole);
        AssertEx.Equal(ImageModelPartRole.ClipL, result.Files.Single(static f => f.FileName == "clip_l.safetensors").SuggestedRole);
        AssertEx.Equal(ImageModelPartRole.T5, result.Files.Single(static f => f.FileName == "t5xxl-Q4_K.gguf").SuggestedRole);
    }

    [Test]
    public async Task ImageDiscovery_InspectRepo_KeepsAQuantlessSafetensorsDiffusionFile()
    {
        // The GGUF lane requires a parseable quant token and would drop this file outright. A .safetensors diffusion
        // model has no quant token and is still perfectly installable.
        var detail = """
                     {
                       "id": "Comfy-Org/flux1-schnell",
                       "sha": "def456",
                       "siblings": [
                         { "rfilename": "flux1-schnell-fp8.safetensors", "lfs": { "size": 17236328832 } }
                       ]
                     }
                     """;

        using var harness = BuildHarness(repoDetail: detail);

        var result = await harness.Discovery.InspectRepoAsync("Comfy-Org/flux1-schnell", CancellationToken.None);

        AssertEx.Equal(expected: 1, result.Files.Count);
        AssertEx.Equal(ImageWeightFormat.Safetensors, result.Files[0].Format);
        AssertEx.Equal(ImageModelPartRole.Diffusion, result.Files[0].SuggestedRole);
    }

    [Test]
    public async Task ImageDiscovery_InspectRepo_ExcludesShardMembers_SoNoPickerCanOfferAFragment()
    {
        // DECISION: sharded sets are excluded, not grouped. ImageModelPartRequest.FileName is a single string and the
        // store downloads exactly one blob per role, so installing a "-00001-of-00003" member would put an unloadable
        // fragment on disk under a name that looks complete. Supporting shards means making a part a LIST of files
        // through the request, store, registry and argument builder — far beyond discovery.
        var detail = """
                     {
                       "id": "owner/big-diffusion",
                       "sha": "aaa",
                       "siblings": [
                         { "rfilename": "model-00001-of-00003.gguf", "lfs": { "size": 5000000000 } },
                         { "rfilename": "model-00002-of-00003.gguf", "lfs": { "size": 5000000000 } },
                         { "rfilename": "model-00003-of-00003.safetensors", "lfs": { "size": 5000000000 } },
                         { "rfilename": "model-single.gguf", "lfs": { "size": 9000000000 } }
                       ]
                     }
                     """;

        using var harness = BuildHarness(repoDetail: detail);

        var result = await harness.Discovery.InspectRepoAsync("owner/big-diffusion", CancellationToken.None);

        AssertEx.Equal(expected: 1, result.Files.Count, "Only the single-file variant is installable.");
        AssertEx.Equal("model-single.gguf", result.Files[0].FileName);
    }

    [Test]
    public async Task ImageDiscovery_InspectRepo_DropsATraversalFileName_SoItNeverReachesAnInstallForm()
    {
        // A repo listing is untrusted input. The store re-checks containment before writing, but a name that could
        // escape the models directory must not even be OFFERED — the browse → pick → install path pre-fills the
        // download form from exactly these rows, so a rejected-at-write name would still be one click away.
        var detail = """
                     {
                       "id": "attacker/evil",
                       "sha": "bbb",
                       "siblings": [
                         { "rfilename": "../../../../etc/cron.d/pwned.safetensors", "lfs": { "size": 10 } },
                         { "rfilename": "/etc/absolute.gguf", "lfs": { "size": 10 } },
                         { "rfilename": "nested/./sneaky.gguf", "lfs": { "size": 10 } },
                         { "rfilename": "legit.gguf", "lfs": { "size": 10 } }
                       ]
                     }
                     """;

        using var harness = BuildHarness(repoDetail: detail);

        var result = await harness.Discovery.InspectRepoAsync("attacker/evil", CancellationToken.None);

        AssertEx.Equal(expected: 1, result.Files.Count);
        AssertEx.Equal("legit.gguf", result.Files[0].FileName);
    }

    [Test]
    public async Task ImageDiscovery_Search_DropsAReposOnlyWeightWhenItIsATraversalName()
    {
        var listing = """
                      [
                        {
                          "id": "attacker/evil",
                          "siblings": [ { "rfilename": "../escape.safetensors" } ]
                        }
                      ]
                      """;

        using var harness = BuildHarness(listing);

        var results = await harness.Discovery.SearchAsync(new ImageModelSearchQuery(), CancellationToken.None);

        AssertEx.Empty(results, "A repo whose only weight file has an unsafe path has nothing installable in it.");
    }

    [Test]
    public async Task ImageDiscovery_InspectRepo_WhenTheRepoIsUnknown_ReturnsAnEmptyFileList()
    {
        using var harness = BuildHarness(repoDetail: null, repoStatusCode: HttpStatusCode.NotFound);

        var result = await harness.Discovery.InspectRepoAsync("owner/missing", CancellationToken.None);

        AssertEx.Equal("owner/missing", result.RepoId);
        AssertEx.Empty(result.Files);
    }

    [Test]
    [Arguments("split_files/vae/qwen_image_vae.safetensors", ImageModelPartRole.Vae)]
    [Arguments("VAE/Qwen_Image-VAE.safetensors", ImageModelPartRole.Vae)]
    [Arguments("ae.safetensors", ImageModelPartRole.Vae)]
    // Live-observed on second-state/FLUX.1-schnell-GGUF: the FLUX autoencoder also ships as "ae-f16.gguf", which an
    // "ae."-prefix rule labelled Diffusion — the operator would then have installed a 0.17 GB VAE as the diffusion part.
    [Arguments("ae-f16.gguf", ImageModelPartRole.Vae)]
    [Arguments("flux1-schnell-Q4_0.gguf", ImageModelPartRole.Diffusion)]
    [Arguments("text_encoders/clip_g.safetensors", ImageModelPartRole.ClipG)]
    [Arguments("clip_l-Q8_0.gguf", ImageModelPartRole.ClipL)]
    [Arguments("t5xxl_fp16.safetensors", ImageModelPartRole.T5)]
    [Arguments("split_files/text_encoders/qwen_2.5_vl_7b.safetensors", ImageModelPartRole.Llm)]
    [Arguments("Qwen2.5-VL-7B-Instruct.Q4_K_M.gguf", ImageModelPartRole.Llm)]
    [Arguments("Qwen2.5-VL-7B-Instruct.mmproj-f16.gguf", ImageModelPartRole.LlmVision)]
    [Arguments("Qwen_Image-Q4_K_M.gguf", ImageModelPartRole.Diffusion)]
    [Arguments("stable-diffusion-v1-5-pruned-emaonly-Q8_0.gguf", ImageModelPartRole.Diffusion)]
    public void SuggestRole_ReadsTheWholeRelativePath_NotJustTheLeaf(string fileName, ImageModelPartRole expected)
    {
        // Repos routinely encode the role in a DIRECTORY ("split_files/vae/…", "text_encoders/…") rather than the file
        // name, so matching the leaf only would mis-label most of a real multi-part set.
        AssertEx.Equal(expected, HuggingFaceImageModelDiscovery.SuggestRole(fileName));
    }

    private static DiscoveryHarness BuildHarness(string? listing = null, string? repoDetail = null, HttpStatusCode repoStatusCode = HttpStatusCode.OK)
    {
        return new DiscoveryHarness(listing, repoDetail, repoStatusCode);
    }

    /// <summary>Owns the stubbed handler + HTTP client + wired discovery so each test disposes them deterministically.</summary>
    private sealed class DiscoveryHarness : IDisposable
    {
        private readonly HttpClient _hubHttp;

        public DiscoveryHarness(string? listing, string? repoDetail, HttpStatusCode repoStatusCode)
        {
            Handler = new StubHandler(listing, repoDetail, repoStatusCode);
            _hubHttp = new HttpClient(Handler, disposeHandler: false);

            var options = new HuggingFaceOptions();
            var hubClient = new HfHubClient(_hubHttp, options, NullLogger<HfHubClient>.Instance);
            Discovery = new HuggingFaceImageModelDiscovery(hubClient, NullLogger<HuggingFaceImageModelDiscovery>.Instance);
        }

        public HuggingFaceImageModelDiscovery Discovery { get; }

        public StubHandler Handler { get; }

        public void Dispose()
        {
            _hubHttp.Dispose();
            Handler.Dispose();
        }
    }

    /// <summary>Routes by URL: <c>/api/models/{repo}</c> → repo detail JSON; <c>/api/models?</c> → listing JSON.</summary>
    private sealed class StubHandler(string? listing, string? repoDetail, HttpStatusCode repoStatusCode) : HttpMessageHandler
    {
        public string LastListUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // OriginalString, not ToString(): Uri.ToString() UNESCAPES a %20 back to a literal space, which would make
            // a URL-shape pin unable to tell "we escaped the search term" from "we did not".
            var url = request.RequestUri!.OriginalString;

            if (url.Contains("/api/models/", StringComparison.Ordinal))
            {
                return Task.FromResult(repoStatusCode == HttpStatusCode.OK
                    ? Json(repoDetail ?? "{}")
                    : new HttpResponseMessage(repoStatusCode));
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
    }
}
