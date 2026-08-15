namespace XE_Local_AI_Engine.Tests.Providers.Training;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;
using XE_Local_AI_Engine.Providers.HuggingFace.Implementation;
using XE_Local_AI_Engine.Providers.HuggingFace.Options;
using XE_Local_AI_Engine.Tests.Providers.HuggingFace;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the base-checkpoint enumeration and multi-file download against a stubbed Hub and download surface.
/// </summary>
public sealed class BaseCheckpointStoreTests : IDisposable
{
    private const string BaseRepo = "unsloth/Llama-3.2-1B-Instruct";
    private const string QuantRepo = "bartowski/Llama-3.2-1B-Instruct-GGUF";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-base-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task BaseArtifact_MultiFileDownload_Resumes_VerifiesSha()
    {
        var shardOne = Encoding.UTF8.GetBytes("first shard bytes");
        var shardTwo = Encoding.UTF8.GetBytes("second shard bytes");
        var config = Encoding.UTF8.GetBytes("{\"model_type\":\"llama\"}");

        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config.json"] = config,
            ["model-00001-of-00002.safetensors"] = shardOne,
            ["model-00002-of-00002.safetensors"] = shardTwo
        };

        var destination = Path.Combine(_root, "artifact");
        _ = Directory.CreateDirectory(destination);

        // An earlier attempt already finished the first shard. Reuse is what stops a resumed 30 GB checkpoint from
        // refetching every completed file, so the served response for it must never be requested.
        await File.WriteAllBytesAsync(Path.Combine(destination, "model-00001-of-00002.safetensors"), shardOne);

        using var handler = FileHandler(payloads);
        var store = Store(handler, HubDetail(BaseRepo, payloads, license: "llama3.2"));

        var manifest = await store.ResolveAsync(BaseRepo, revision: null, CancellationToken.None);
        var completed = await store.DownloadAsync(manifest, destination, progress: null, CancellationToken.None);

        AssertEx.Equal(3, completed.Files.Count);
        foreach (var file in completed.Files)
        {
            AssertEx.True(File.Exists(file.LocalPath), $"{file.FileName} must exist after the download.");
            AssertEx.Equal(payloads[file.FileName].Length, (int)file.SizeBytes);
        }

        var requested = handler.Requests.Count;
        AssertEx.Equal(2, requested, "The already-complete shard must be reused rather than re-requested.");

        // Every fetched file was verified against the digest the Hub advertised: a mismatch is a hard failure, proven
        // by the negative case below.
        AssertEx.True(completed.Files.All(static file => file.LocalPath.Length > 0));
    }

    [Test]
    public async Task BaseArtifact_WhenAServedShardDoesNotMatchItsDigest_TheDownloadFails()
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config.json"] = Encoding.UTF8.GetBytes("{}"),
            ["model.safetensors"] = Encoding.UTF8.GetBytes("real bytes")
        };

        // Serve altered bytes while still advertising the digest of the real ones — a CDN or mirror handing back
        // something other than what the Hub described. Verification is the only thing standing between that and a
        // corrupt checkpoint that trains for hours before failing.
        using var handler = FileHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config.json"] = payloads["config.json"],
            ["model.safetensors"] = Encoding.UTF8.GetBytes("tampered bytes!")
        },
            payloads);
        var detail = HubDetail(BaseRepo, payloads, license: "apache-2.0");

        var store = Store(handler, detail);
        var manifest = await store.ResolveAsync(BaseRepo, revision: null, CancellationToken.None);

        _ = await AssertEx.ThrowsAsync<HuggingFaceDownloadException>(
            () => store.DownloadAsync(manifest, Path.Combine(_root, "tampered"), progress: null, CancellationToken.None));
    }

    [Test]
    public async Task LicenseGate_KeyedOnBaseRepo_NotGgufQuantRepo()
    {
        var basePayloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config.json"] = Encoding.UTF8.GetBytes("{}"),
            ["model.safetensors"] = Encoding.UTF8.GetBytes("weights")
        };

        // The quant repo carries a different license tag and no safetensors at all — exactly the confusion locked
        // decision 8 exists to prevent.
        var quantPayloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config.json"] = Encoding.UTF8.GetBytes("{}"),
            ["model-Q4_K_M.gguf"] = Encoding.UTF8.GetBytes("quantized")
        };

        using var handler = FileHandler(basePayloads);
        var store = Store(handler,
            HubDetail(BaseRepo, basePayloads, license: "llama3.2"),
            HubDetail(QuantRepo, quantPayloads, license: "mit"));

        var manifest = await store.ResolveAsync(BaseRepo, revision: null, CancellationToken.None);

        AssertEx.Equal(BaseRepo, manifest.RepoId);
        AssertEx.Equal("llama3.2", manifest.License);

        // The quant repo is not merely a different license — it is not trainable at all.
        var rejection = await AssertEx.ThrowsAsync<BaseCheckpointNotTrainableException>(
            () => store.ResolveAsync(QuantRepo, revision: null, CancellationToken.None));
        AssertEx.Contains(rejection.Message, "safetensors");
    }

    [Test]
    public async Task Resolve_RecordsTheGatedFlagAndTheResolvedCommit()
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config.json"] = Encoding.UTF8.GetBytes("{}"),
            ["model.safetensors"] = Encoding.UTF8.GetBytes("weights")
        };

        using var handler = FileHandler(payloads);
        var store = Store(handler, HubDetail(BaseRepo, payloads, license: "llama3.2", gated: "manual", revision: "abc123"));

        var manifest = await store.ResolveAsync(BaseRepo, revision: null, CancellationToken.None);

        AssertEx.True(manifest.IsGated, "HF reports gating as \"auto\"/\"manual\"; anything but literal false is gated.");
        AssertEx.Equal("abc123", manifest.Revision);
    }

    [Test]
    public async Task Resolve_PinsAnExplicitRevisionOverTheRepoDefault()
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["config.json"] = Encoding.UTF8.GetBytes("{}"),
            ["model.safetensors"] = Encoding.UTF8.GetBytes("weights")
        };

        using var handler = FileHandler(payloads);
        var store = Store(handler, HubDetail(BaseRepo, payloads, license: null, revision: "default-sha"));

        var manifest = await store.ResolveAsync(BaseRepo, "pinned-sha", CancellationToken.None);

        AssertEx.Equal("pinned-sha", manifest.Revision);
    }

    [Test]
    public void ClassifyFile_SelectsOnlyWhatFineTuningNeeds()
    {
        AssertEx.Equal(BaseCheckpointFileRole.Weights, HuggingFaceBaseCheckpointStore.ClassifyFile("model.safetensors"));
        AssertEx.Equal(BaseCheckpointFileRole.Config, HuggingFaceBaseCheckpointStore.ClassifyFile("config.json"));
        AssertEx.Equal(BaseCheckpointFileRole.Tokenizer, HuggingFaceBaseCheckpointStore.ClassifyFile("tokenizer.json"));

        // A base repo commonly also ships these; downloading them would multiply the transfer for nothing.
        AssertEx.Null(HuggingFaceBaseCheckpointStore.ClassifyFile("pytorch_model.bin"));
        AssertEx.Null(HuggingFaceBaseCheckpointStore.ClassifyFile("model-Q4_K_M.gguf"));
        AssertEx.Null(HuggingFaceBaseCheckpointStore.ClassifyFile("README.md"));
        AssertEx.Null(HuggingFaceBaseCheckpointStore.ClassifyFile("onnx/model.safetensors"));
    }

    private HuggingFaceBaseCheckpointStore Store(GgufStoreTestInfrastructure.ScriptedHandler fileHandler, params string[] repoDetails)
    {
        var options = new HuggingFaceOptions
        {
            ModelsDirectory = _root,
            DiskMarginBytes = 0
        };

#pragma warning disable CA2000 // In-memory fakes with no unmanaged resource; they live for the test's duration.
        var hubHandler = new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            var match = Array.Find(repoDetails, detail => url.Contains(RepoIdOf(detail), StringComparison.Ordinal));
            return match is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(match)
                };
        });
        var hubHttp = new HttpClient(hubHandler);
        var fileHttp = new HttpClient(fileHandler, disposeHandler: false);
#pragma warning restore CA2000

        var hubClient = new HfHubClient(hubHttp, options, NullLogger<HfHubClient>.Instance);
        var downloadClient = GgufStoreTestInfrastructure.DownloadClient(fileHttp,
            GgufStoreTestInfrastructure.NoTokenStore(),
            GgufStoreTestInfrastructure.AbundantSpace(),
            options);

        return new HuggingFaceBaseCheckpointStore(hubClient, downloadClient, NullLogger<HuggingFaceBaseCheckpointStore>.Instance);
    }

    /// <summary>
    ///     Serves each repo file at its resolve URL, carrying the <c>X-Linked-Etag</c> digest header the download client
    ///     verifies against. <paramref name="advertised" /> lets a test serve one payload while advertising another's
    ///     digest; by default the header describes exactly what is served.
    /// </summary>
    private static GgufStoreTestInfrastructure.ScriptedHandler FileHandler(IReadOnlyDictionary<string, byte[]> payloads,
        IReadOnlyDictionary<string, byte[]>? advertised = null)
    {
        return new GgufStoreTestInfrastructure.ScriptedHandler((request, _) =>
        {
            var name = request.RequestUri!.Segments[^1].Trim('/');
            if (!payloads.TryGetValue(name, out var bytes))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var digestSource = advertised is not null && advertised.TryGetValue(name, out var declared) ? declared : bytes;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Headers.TryAddWithoutValidation("X-Linked-Etag", $"\"{Sha256Of(digestSource)}\"");
            return response;
        });
    }

    private static string HubDetail(string repoId,
        IReadOnlyDictionary<string, byte[]> payloads,
        string? license,
        string gated = "false",
        string revision = "main")
    {
        var siblings = payloads.Select(entry =>
        {
            var lfs = "{\"size\":" + entry.Value.Length + ",\"sha256\":\"" + Sha256Of(entry.Value) + "\"}";
            return "{\"rfilename\":\"" + entry.Key + "\",\"size\":" + entry.Value.Length + ",\"lfs\":" + lfs + "}";
        });

        var cardData = license is null ? "{}" : "{\"license\":\"" + license + "\"}";
        var gatedJson = gated == "false" ? "false" : "\"" + gated + "\"";
        return "{\"id\":\"" + repoId + "\",\"sha\":\"" + revision + "\",\"gated\":" + gatedJson
               + ",\"cardData\":" + cardData + ",\"siblings\":[" + string.Join(',', siblings) + "]}";
    }

    private static string RepoIdOf(string detailJson)
    {
        var start = detailJson.IndexOf("\"id\":\"", StringComparison.Ordinal) + 6;
        var end = detailJson.IndexOf('"', start);
        return detailJson[start..end];
    }

    private static string Sha256Of(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
