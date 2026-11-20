namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Tests.Testing;
using static ModelLabExperimentServiceTests;

/// <summary>A trace that records attention: the job's capture fields, the second blob and its cleanup, the read route, and the refusals.</summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabAttentionTests
{
    private const int Heads = 8;

    /// <summary>The full blocks of <see cref="LabHost.Config" />'s 24.</summary>
    private const string FullBlocks = "[3,7,11,15,19,23]";

    private const string Footer = """{"footer":true,"truncated":true,"records":2,"dropped":3,"payloadBytes":10,"cap":3932320}""";

    private const string CapturedResult =
        """{"promptTokens":3,"tokens":[{"id":9,"text":"hi"}],"text":"hi","seconds":0.5,"peakVramBytes":10,"blockCount":24,"promptPieces":[{"id":1,"text":"a"},{"id":2,"text":"b"},{"id":3,"text":"c"}],"layerKinds":"LLLFLLLFLLLFLLLFLLLFLLLF","attnImplementation":"sdpa","attention":{"layers":[3,7,11,15,19,23],"heads":8,"kvHeads":2,"headDim":256,"topKeys":32,"weightScale":10000,"records":2,"truncated":true}}""";

    [Test]
    public async Task RecordingAttention_SendsEveryFullBlock_AndACapForTheCheckpointsOwnShape()
    {
        using var process = Captured();
        await using var host = await LabHost.StartAsync(process);

        var start = await host.Service.StartExperimentAsync(host.Request() with
        {
            RecordAttention = true
        }, CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        using (var job = JsonDocument.Parse(process.ReadJobFile()))
        {
            var root = job.RootElement;
            AssertEx.Equal(FullBlocks, root.GetProperty("attentionLayers").GetRawText());
            AssertEx.Equal(ModelLabTraceSpec.AttentionTopKeys, root.GetProperty("attentionTopKeys").GetInt32());
            AssertEx.Equal(expected: 3_932_320, root.GetProperty("attentionByteCap").GetInt32(), "Six full blocks of eight heads, from the config, not a default shape.");
            AssertEx.Equal(host.WorkDirectory(start.ExperimentId!.Value), Path.GetDirectoryName(root.GetProperty("attentionPath").GetString()), "It is plaintext derived from the prompt.");
            AssertEx.Equal("[6,12,18]", root.GetProperty("layers").GetRawText(), "The residual trace keeps its own layers.");
        }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Service.GetAsync(start.ExperimentId.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, record.Status, record.ErrorMessage ?? "no error");
        AssertEx.True(record.AttentionBlobId.HasValue);
        AssertEx.Equal((long)Encoding.UTF8.GetByteCount(process.AttentionContent!), record.AttentionByteCount!.Value);
        var blobs = Directory.GetFiles(Scope(host, record.Id));
        AssertEx.Equal(expected: 2, blobs.Length, "The trace and the attention rows, in the row's one scope.");
        foreach (var blob in blobs)
        {
            AssertEx.False(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(blob)).Contains("\"footer\"", StringComparison.Ordinal), "Each blob on disk is ciphertext.");
        }
    }

    [Test]
    public async Task ACapturedBlock_ReadsBackOnItsOwn_WithWeightsAsFractions()
    {
        using var process = Captured();
        await using var host = await LabHost.StartAsync(process);
        var id = await RunAsync(host, process);
        using var client = host.Factory.CreateClient();

        using (var body = await GetJsonAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/attention/7", HttpStatusCode.OK))
        {
            var root = body.RootElement;
            AssertEx.Equal(expected: 7, root.GetProperty("layer").GetInt32());
            AssertEx.Equal(Heads, root.GetProperty("heads").GetInt32());
            AssertEx.Equal(expected: 2, root.GetProperty("kvHeads").GetInt32());
            AssertEx.Equal(expected: 32, root.GetProperty("topKeys").GetInt32());
            AssertEx.True(root.GetProperty("truncated").GetBoolean());
            AssertEx.Equal(expected: 3, root.GetProperty("droppedRecords").GetInt32());
            var row = root.GetProperty("rows").EnumerateArray().Single();
            AssertEx.Equal(expected: 1, row.GetProperty("step").GetInt32(), "Only block 7's row; block 3's stays out.");
            AssertEx.Equal(expected: 3, row.GetProperty("queryPosition").GetInt32());
            var head = row.GetProperty("heads")[Heads - 1];
            AssertEx.Equal("[3,0]", head.GetProperty("keys").GetRawText());
            AssertEx.Equal("[0.6,0.25]", head.GetProperty("weights").GetRawText());
            AssertEx.Equal(expected: 0.1499, head.GetProperty("rest").GetDouble());
        }

        using (var body = await GetJsonAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}", HttpStatusCode.OK))
        {
            var result = body.RootElement.GetProperty("result");
            AssertEx.Equal("sdpa", result.GetProperty("attnImplementation").GetString());
            AssertEx.Equal(expected: 24, result.GetProperty("layerKinds").GetString()!.Length);
            AssertEx.Equal("c", result.GetProperty("promptPieces")[2].GetProperty("text").GetString());
            AssertEx.Equal(FullBlocks, result.GetProperty("attention").GetProperty("layers").GetRawText());
        }

        await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/attention/4", HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ARunWithoutAttention_ASteeringRow_AndAnUnknownRun_HaveNone()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var plain = await RunAsync(host, process, recordAttention: false);
        var steering = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>();
            await store.CreateRunningAsync(new ModelLabExperimentCreate
            {
                Id = steering,
                Kind = ModelLabExperimentKind.Steering,
                RepoId = "Qwen/Qwen3.5-0.8B",
                Revision = "rev",
                BaseArtifactId = host.Request().BaseArtifactId,
                InstalledModelName = Gguf,
                Prompt = "seed",
                LicenseConfirmationJson = new byte[]
                {
                    1
                }
            });
            await store.CompleteAsync(steering, new ModelLabExperimentCompletion
            {
                Status = ModelLabExperimentStatus.Succeeded,
                ResultJson = Encoding.UTF8.GetBytes(CapturedResult),
                AttentionBlobId = Guid.NewGuid(),
                AttentionSha256 = "00",
                AttentionByteCount = 1
            });
        }

        using var client = host.Factory.CreateClient();
        foreach (var id in new[]
                 {
                     plain,
                     steering,
                     Guid.NewGuid()
                 })
        {
            await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/attention/3", HttpStatusCode.NotFound);
        }

        AssertEx.Null((await host.Service.GetAsync(plain, CancellationToken.None))!.AttentionBlobId, "A trace without capture stores one blob.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AttentionRowsThatDoNotReadBack_Are422_WhileTheTraceStillReads(bool tamper)
    {
        using var process = tamper
            ? Captured()
            : new ScriptedProcess
            {
                AttentionContent = Rows(),
                TraceResult = """{"promptTokens":3,"tokens":[],"text":"","seconds":0.5,"peakVramBytes":10,"blockCount":24}"""
            };
        await using var host = await LabHost.StartAsync(process);
        var id = await RunAsync(host, process);
        if (tamper)
        {
            var record = AssertEx.NotNull(await host.Service.GetAsync(id, CancellationToken.None));
            var blob = Path.Combine(Scope(host, id), record.AttentionBlobId!.Value.ToString("N") + ".blob");
            var bytes = await File.ReadAllBytesAsync(blob);
            bytes[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(blob, bytes);
        }

        using var client = host.Factory.CreateClient();
        await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/attention/7", HttpStatusCode.UnprocessableEntity);
        await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/trace", HttpStatusCode.OK);
    }

    [Test]
    [Arguments(1, "byte cap")]
    [Arguments(-1, "without writing its results")]
    public async Task AnAttentionFileOverItsCap_OrMissing_KeepsNothing(int overCap, string expected)
    {
        using var process = new ScriptedProcess
        {
            AttentionContent = overCap < 0 ? null : new string('x', ModelLabTraceSpec.AttentionByteCapFor(6, Heads) + overCap),
            TraceResult = CapturedResult
        };
        await using var host = await LabHost.StartAsync(process);

        var id = await RunAsync(host, process);

        var record = AssertEx.NotNull(await host.Service.GetAsync(id, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Contains(record.ErrorMessage!, expected, StringComparison.Ordinal);
        AssertEx.Null(record.TraceBlobId);
        AssertEx.Null(record.AttentionBlobId);
        AssertEx.False(Directory.Exists(Scope(host, id)), "The size check runs before any blob is written, so no scope exists.");
    }

    [Test]
    public async Task ASecondBlobThatFailsToWrite_TakesTheFirstWithIt_AndFailsTheRun()
    {
        using var process = Captured();
        await using var host = await LabHost.StartAsync(process);
        var blobsAtTheSecondWrite = -1;
        host.Service.BeforeAttentionBlobWrite = () =>
        {
            blobsAtTheSecondWrite = Directory.GetFiles(Path.Combine(host.DataRoot, "model-lab", "artifacts"), "*.blob", SearchOption.AllDirectories).Length;
            throw new IOException("No space left on device");
        };

        var id = await RunAsync(host, process);

        AssertEx.Equal(expected: 1, blobsAtTheSecondWrite, "The trace blob had landed when the second write failed.");
        var record = AssertEx.NotNull(await host.Service.GetAsync(id, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Null(record.TraceBlobId);
        AssertEx.False(Directory.Exists(Scope(host, id)), "No blob outlives a run that names none.");
    }

    [Test]
    public async Task ACompletionThatCannotBeSaved_LeavesNoBlobBehind()
    {
        using var process = Captured();
        ModelLabExperimentCompletion? handed = null;
        await using var host = await LabHost.StartAsync(process, configure: services => WrapStore(services, store => new FaultingStore(store)
        {
            OnComplete = completion => handed = completion,
            CompleteFailure = new InvalidOperationException("database is locked")
        }));

        var id = await RunAsync(host, process);

        AssertEx.True(handed?.AttentionBlobId is not null && handed.TraceBlobId is not null, "Both blobs were written and handed to the store.");
        AssertEx.False(Directory.Exists(Scope(host, id)), "The row that would have named them was never saved.");
    }

    [Test]
    [Arguments("""{"model_type":"qwen3_5","text_config":{"hidden_size":1024,"num_hidden_layers":24}}""", "does not state")]
    [Arguments("""{"model_type":"qwen3_5","text_config":{"hidden_size":1024,"num_hidden_layers":24,"num_attention_heads":8,"full_attention_interval":25}}""", "no full-attention block")]
    [Arguments("""{"model_type":"qwen3_5","text_config":{"hidden_size":1024,"num_hidden_layers":24,"num_attention_heads":64}}""", "64 attention heads")]
    public async Task ACheckpointThatCannotRecordAttention_IsRefusedBeforeTheGate(string config, string expected)
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process, config: config);

        var start = await host.Service.StartExperimentAsync(host.Request() with
        {
            RecordAttention = true
        }, CancellationToken.None);

        AssertEx.Equal(ModelLabStartRefusal.AttentionUnavailable, start.Refusal!.Value);
        AssertEx.Contains(start.RefusalMessage!, expected, StringComparison.Ordinal);
        AssertEx.Null(process.Request);
        AssertEx.Null(host.Gate.ExclusiveKind);
        AssertEx.Empty(await host.Service.ListAsync(CancellationToken.None));
    }

    [Test]
    public async Task TheStartRoute_RefusesAttentionOnASteeringJob()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        using var client = host.Factory.CreateClient();
        using var request = host.Authorized(HttpMethod.Post, "/api/local/v1/model-lab/experiments");
        request.Content = JsonContent.Create(new
        {
            baseArtifactId = host.Request().BaseArtifactId,
            installedModelName = Gguf,
            prompt = "p",
            licenseConfirmed = true,
            recordAttention = true,
            steering = new
            {
                positivePrompts = new[]
                {
                    "a"
                },
                negativePrompts = new[]
                {
                    "b"
                }
            }
        });

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "does not record attention", StringComparison.Ordinal);
        AssertEx.Null(process.Request);
    }

    [Test]
    public void TheAttentionCap_IsTwiceEveryHeadsTopKeys_AndBoundsTheLargestShape()
    {
        AssertEx.Equal((6 * 8 * 128 * 32 * 10 * 2) + 160, ModelLabTraceSpec.AttentionByteCapFor(6, 8));
        AssertEx.Equal(expected: 20_971_680, ModelLabTraceSpec.MaxAttentionByteCap);
        AssertEx.Equal(ModelLabTraceSpec.AttentionByteCapFor(ModelLabTraceSpec.MaxAttentionLayers, ModelLabTraceSpec.MaxAttentionHeads), ModelLabTraceSpec.MaxAttentionByteCap);
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.AttentionByteCapFor(0, 8));
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.AttentionByteCapFor(ModelLabTraceSpec.MaxAttentionLayers + 1, 8));
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.AttentionByteCapFor(6, ModelLabTraceSpec.MaxAttentionHeads + 1));
    }

    [Test]
    public void TheCapturedBlocks_AreEveryFullBlock_OrEightSpreadFromFirstToLast()
    {
        AssertEx.Equal("3,7,11,15,19,23", string.Join(',', ModelLabTraceSpec.AttentionLayersFor(Kinds(24, interval: 4))));
        AssertEx.Equal("1,3,5,7,9,11,13,15", string.Join(',', ModelLabTraceSpec.AttentionLayersFor(Kinds(16, interval: 2))));
        // Twelve full blocks (3, 7, ... 47): picks 0, 1, 3, 4, 6, 7, 9 and 11 of them.
        AssertEx.Equal("3,7,15,19,27,31,39,47", string.Join(',', ModelLabTraceSpec.AttentionLayersFor(Kinds(48, interval: 4))));
        AssertEx.Empty(ModelLabTraceSpec.AttentionLayersFor(Kinds(3, interval: 4)));
    }

    [Test]
    public void AttentionRows_ThatDoNotMatchTheHeadCount_OrDoNotParse_AreUnavailable()
    {
        var rows = Encoding.UTF8.GetBytes(Rows());

        AssertEx.Equal(ModelLabTraceReadStatus.Found, ModelLabTraceSpec.ParseAttention(rows, Heads).Status);
        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, ModelLabTraceSpec.ParseAttention(rows, Heads + 1).Status);
        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, ModelLabTraceSpec.ParseAttention("""{"s":0,"l":3,"q":2,"k":[[2,1]],"w":[[5000]],"r":[0]}"""u8.ToArray(), heads: 1).Status);
        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, ModelLabTraceSpec.ParseAttention("{not json}\n"u8.ToArray(), Heads).Status);
        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, ModelLabTraceSpec.ParseAttention("""{"s":0,"l":3,"q":2,"k":null,"w":[[5000]],"r":[0]}"""u8.ToArray(), heads: 1).Status);
        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, ModelLabTraceSpec.ParseAttention("""{"s":0,"l":3,"q":2,"k":[null],"w":[[5000]],"r":[0]}"""u8.ToArray(), heads: 1).Status);
    }

    [Test]
    public void AResultFromBeforeContractFour_ReadsWithTheNewFieldsNull_AndAFourResultWithThemSet()
    {
        var before = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull("""{"promptTokens":3,"tokens":[],"text":"hi","layers":[6,12,18]}"""u8.ToArray()));
        AssertEx.Null(before.PromptPieces);
        AssertEx.Null(before.LayerKinds);
        AssertEx.Null(before.AttnImplementation);
        AssertEx.Null(before.Attention);

        var after = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(Encoding.UTF8.GetBytes(CapturedResult)));
        AssertEx.Equal(expected: 3, after.PromptPieces!.Count);
        AssertEx.Equal("LLLF", after.LayerKinds![..4]);
        AssertEx.Equal(new ModelLabAttentionSummary
        {
            Layers = after.Attention!.Layers,
            Heads = 8,
            KvHeads = 2,
            HeadDim = 256,
            TopKeys = 32,
            WeightScale = 10_000,
            Records = 2,
            Truncated = true
        }, after.Attention);
        AssertEx.Equal(FullBlocks, JsonSerializer.Serialize(after.Attention.Layers));
        var roundTrip = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(JsonSerializer.SerializeToUtf8Bytes(after, JsonSerializerOptions.Web)));
        AssertEx.Equal("sdpa", roundTrip.AttnImplementation);
        AssertEx.Equal(expected: 8, roundTrip.Attention!.Heads);
    }

    private static ScriptedProcess Captured() =>
        new()
        {
            AttentionContent = Rows(),
            TraceResult = CapturedResult
        };

    /// <summary>Step 0 at block 3 and step 1 at block 7, each head keeping two keys, then the footer.</summary>
    private static string Rows() =>
        string.Join('\n', Row(step: 0, layer: 3, query: 2), Row(step: 1, layer: 7, query: 3), Footer) + "\n";

    private static string Row(int step, int layer, int query) =>
        JsonSerializer.Serialize(new
        {
            s = step,
            l = layer,
            q = query,
            k = Enumerable.Repeat(new[]
            {
                query,
                0
            }, Heads),
            w = Enumerable.Repeat(new[]
            {
                6000,
                2500
            }, Heads),
            r = Enumerable.Repeat(1499, Heads)
        });

    private static string[] Kinds(int blocks, int interval) =>
        [.. Enumerable.Range(0, blocks).Select(layer => (layer + 1) % interval == 0 ? "full_attention" : "linear_attention")];

    internal static string Scope(LabHost host, Guid id) =>
        Path.Combine(host.DataRoot, "model-lab", "artifacts", id.ToString("N"));

    internal static async Task<Guid> RunAsync(LabHost host, ScriptedProcess process, bool recordAttention = true)
    {
        var start = await host.Service.StartExperimentAsync(host.Request() with
        {
            RecordAttention = recordAttention
        }, CancellationToken.None);
        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The job should have started.");
        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        return start.ExperimentId!.Value;
    }

    internal static async Task<string> ExpectStatusAsync(LabHost host, HttpClient client, string path, HttpStatusCode expected)
    {
        using var request = host.Authorized(HttpMethod.Get, path);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        AssertEx.Equal(expected, response.StatusCode, body);
        return body;
    }

    internal static async Task<JsonDocument> GetJsonAsync(LabHost host, HttpClient client, string path, HttpStatusCode expected) =>
        JsonDocument.Parse(await ExpectStatusAsync(host, client, path, expected));
}
