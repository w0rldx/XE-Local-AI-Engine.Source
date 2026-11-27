namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Tests.Testing;
using static ModelLabAttentionTests;
using static ModelLabTestSupport;

/// <summary>A trace's router choices: the cap, the routing lines beside the hook records and lens in one blob, their check, and the read route.</summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabRoutingTests
{
    private const string HookRecord = """{"s":0,"l":1,"k":"F","n":3.5,"p":[-1.0,0.0,1.0],"ti":[7,8],"tv":[0.5,-0.25]}""";

    /// <summary>bfloat16 0.310546875, 0.125, 0.5 and 1.0.</summary>
    private const int Bits031 = 0x3E9F;

    private const int Bits0125 = 0x3E00;

    private const int Bits05 = 0x3F00;

    private const int Bits1 = 0x3F80;

    /// <summary>The checkpoint's blocks; routing records blocks 1 and 3 of them.</summary>
    private const int Blocks = 4;

    [Test]
    public void TheRoutingCap_IsTheWidestLinePerStep_AndTheWidestLineFitsIt()
    {
        AssertEx.Equal(expected: 746_656, ModelLabTraceSpec.RoutingByteCapFor(16, 256));
        AssertEx.Equal(expected: 2_958_496, ModelLabTraceSpec.RoutingByteCapFor(ModelLabTraceSpec.MaxTraceLayers, ModelLabTraceSpec.MaxRequestedNewTokens));
        AssertEx.Equal((256 * (36 + (16 * ((11 * 16) + 4)))) + 160, ModelLabTraceSpec.RoutingByteCapFor(16, 256));
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.RoutingByteCapFor(0, 128));
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.RoutingByteCapFor(ModelLabTraceSpec.MaxTraceLayers + 1, 128));
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.RoutingByteCapFor(16, ModelLabTraceSpec.MaxRequestedNewTokens + 1));

        // Every field at its widest, written as the script writes it: compact separators and a newline.
        var ids = new JsonArray([.. Enumerable.Repeat(ModelLabTraceSpec.MaxRoutedExperts - 1, ModelLabTraceSpec.MaxRoutedExpertsPerToken).Select(static id => JsonValue.Create(id))]);
        var bits = new JsonArray([.. Enumerable.Repeat(Bits1, ModelLabTraceSpec.MaxRoutedExpertsPerToken).Select(static weight => JsonValue.Create(weight))]);
        var widest = Line(ModelLabTraceSpec.MaxRequestedNewTokens - 1, [.. Enumerable.Range(0, ModelLabTraceSpec.MaxTraceLayers).Select(_ => ids.DeepClone())],
            [.. Enumerable.Range(0, ModelLabTraceSpec.MaxTraceLayers).Select(_ => bits.DeepClone())]);
        var perStep = ModelLabTraceSpec.RoutingByteCapFor(ModelLabTraceSpec.MaxTraceLayers, 1) - 160;
        AssertEx.True(Encoding.UTF8.GetByteCount(widest + "\n") <= perStep, $"{Encoding.UTF8.GetByteCount(widest) + 1} bytes fit {perStep}.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public void AWellFormedRun_ReadsEveryStep_WithItsWeightsBitExact(bool normalizedTopK)
    {
        var routing = Parse(Content([Record(0), Record(1)], [RoutingFooter(records: 2)]), Summary(records: 2, normalizedTopK), generated: 2);

        AssertEx.Equal(ModelLabTraceReadStatus.Found, routing.Status);
        AssertEx.Equal(normalizedTopK, routing.Summary!.NormalizedTopK);
        AssertEx.Equal(expected: 2, routing.Records.Count);
        AssertEx.Equal("12,5", string.Join(',', routing.Records[1].Experts[0]));
        AssertEx.Equal(expected: 0.310546875f, ModelLabTraceSpec.RoutingWeight(routing.Records[1].WeightBits[0][0]));
        AssertEx.Equal(expected: 0.125f, ModelLabTraceSpec.RoutingWeight(Bits0125));
        AssertEx.Equal(expected: 0.5f, ModelLabTraceSpec.RoutingWeight(Bits05));
        AssertEx.Equal(expected: 1f, ModelLabTraceSpec.RoutingWeight(Bits1));
        AssertEx.Equal(expected: 0f, ModelLabTraceSpec.RoutingWeight(0));
    }

    [Test]
    public void ATruncatedPrefix_Reads_WhenTheRoutingFooterSaysTheCapCutIt()
    {
        var routing = Parse(Content([Record(0)], [RoutingFooter(records: 1, dropped: 2)]), Summary(records: 1, truncated: true), generated: 3);

        AssertEx.Equal(ModelLabTraceReadStatus.Found, routing.Status);
        AssertEx.True(routing.Footer!.Truncated);
        AssertEx.Equal(expected: 2, routing.Footer.Dropped);
    }

    [Test]
    [Arguments("another weight encoding")]
    [Arguments("no weight encoding")]
    [Arguments("no blocks")]
    [Arguments("a repeated block")]
    [Arguments("a block past the checkpoint")]
    [Arguments("a negative block")]
    [Arguments("no picks per token")]
    [Arguments("more than 16 picks per token")]
    [Arguments("more picks than experts")]
    [Arguments("no experts")]
    [Arguments("more than 1024 experts")]
    [Arguments("a row with a block missing")]
    [Arguments("a block with a pick missing")]
    [Arguments("a block with a weight missing")]
    [Arguments("a null block")]
    [Arguments("an id past the experts")]
    [Arguments("a negative id")]
    [Arguments("a repeated id")]
    [Arguments("a negative weight")]
    [Arguments("a weight above 1")]
    [Arguments("a repeated step")]
    [Arguments("a gap")]
    [Arguments("steps out of order")]
    [Arguments("no routing footer")]
    [Arguments("two routing footers")]
    [Arguments("kept and dropped steps that are not the tokens")]
    [Arguments("a truncated flag without dropped steps")]
    [Arguments("a summary count the footer does not give")]
    [Arguments("a summary truncated flag the footer does not give")]
    [Arguments("a null routing")]
    [Arguments("a line that does not parse")]
    public void ARoutingThatFailsItsCheck_IsUnavailable(string defect)
    {
        var first = Record(0);
        var second = Record(1);
        var summary = Summary(records: 2);
        var footers = new List<string>
        {
            RoutingFooter(records: 2)
        };
        var generated = 2;
        string? extra = null;
        switch (defect)
        {
            case "another weight encoding":
                summary = summary with
                {
                    WeightEncoding = "float32"
                };
                break;
            case "no weight encoding":
                summary = summary with
                {
                    WeightEncoding = null
                };
                break;
            case "no blocks":
                summary = summary with
                {
                    Layers = []
                };
                break;
            case "a repeated block":
                summary = summary with
                {
                    Layers = [1, 1]
                };
                break;
            case "a block past the checkpoint":
                summary = summary with
                {
                    Layers = [1, Blocks]
                };
                break;
            case "a negative block":
                summary = summary with
                {
                    Layers = [-1, 3]
                };
                break;
            case "no picks per token":
                summary = summary with
                {
                    ExpertsPerToken = 0
                };
                break;
            case "more than 16 picks per token":
                // Every row holds 17 distinct picks, so only the bound refuses it.
                summary = summary with
                {
                    ExpertsPerToken = ModelLabTraceSpec.MaxRoutedExpertsPerToken + 1
                };
                first = Record(0, picks: ModelLabTraceSpec.MaxRoutedExpertsPerToken + 1);
                second = Record(1, picks: ModelLabTraceSpec.MaxRoutedExpertsPerToken + 1);
                break;
            case "more picks than experts":
                summary = summary with
                {
                    Experts = 1
                };
                break;
            case "no experts":
                summary = summary with
                {
                    Experts = 0
                };
                break;
            case "more than 1024 experts":
                summary = summary with
                {
                    Experts = ModelLabTraceSpec.MaxRoutedExperts + 1
                };
                break;
            case "a row with a block missing":
                second["x"]!.AsArray().RemoveAt(1);
                second["w"]!.AsArray().RemoveAt(1);
                break;
            case "a block with a pick missing":
                second["x"]![1]!.AsArray().RemoveAt(1);
                break;
            case "a block with a weight missing":
                second["w"]![1]!.AsArray().RemoveAt(1);
                break;
            case "a null block":
                second["x"]![1] = null;
                break;
            case "an id past the experts":
                second["x"]![1]![0] = 64;
                break;
            case "a negative id":
                second["x"]![1]![0] = -1;
                break;
            case "a repeated id":
                second["x"]![1]![1] = 63;
                break;
            case "a negative weight":
                second["w"]![1]![0] = -1;
                break;
            case "a weight above 1":
                second["w"]![1]![0] = Bits1 + 1;
                break;
            case "a repeated step":
                second["s"] = 0;
                break;
            case "a gap":
                second["s"] = 2;
                break;
            case "steps out of order":
                first["s"] = 1;
                second["s"] = 0;
                break;
            case "no routing footer":
                footers.Clear();
                break;
            case "two routing footers":
                footers.Add(RoutingFooter(records: 2));
                break;
            case "kept and dropped steps that are not the tokens":
                generated = 3;
                break;
            case "a truncated flag without dropped steps":
                footers[0] = RoutingFooter(records: 2, truncated: true);
                summary = summary with
                {
                    Truncated = true
                };
                break;
            case "a summary count the footer does not give":
                summary = Summary(records: 3);
                break;
            case "a summary truncated flag the footer does not give":
                summary = summary with
                {
                    Truncated = true
                };
                break;
            case "a null routing":
                extra = """{"routing":null}""";
                break;
            case "a line that does not parse":
                extra = """{"routing":{"s":"zero"}}""";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect), defect, "No such defect.");
        }

        var content = Content([first, second], footers) + (extra is null ? string.Empty : extra + "\n");

        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, Parse(content, summary, generated).Status, defect);
    }

    /// <summary>A contract-8 blob carries routing lines; the hook, attention and lens readers must skip them, never read one as a default record.</summary>
    [Test]
    public void RoutingLines_NeverReadAsHookRecords_LensSteps_OrAttentionRows()
    {
        var lens = new JsonObject
        {
            ["lens"] = new JsonObject
            {
                ["s"] = 0,
                ["i"] = new JsonArray(new JsonArray(7)),
                ["p"] = new JsonArray(new JsonArray(-0.5)),
                ["c"] = new JsonArray(-0.5),
                ["r"] = new JsonArray(1),
                ["e"] = new JsonArray(0.25),
                ["t"] = new JsonObject
                {
                    ["7"] = "the"
                }
            }
        }.ToJsonString();
        var lensFooter = """{"lensFooter":{"truncated":false,"records":1,"dropped":0,"payloadBytes":10,"cap":1000}}""";
        var content = Encoding.UTF8.GetBytes(string.Join('\n', HookRecord, Footer(), lens, lensFooter, Line(Record(0)), RoutingFooter(records: 1, dropped: 0)) + "\n");

        var trace = ModelLabTraceSpec.ParseTrace(content);
        AssertEx.Equal(ModelLabTraceReadStatus.Found, trace.Status);
        AssertEx.Equal(expected: 1, trace.Records.Single().Layer, "Only the hook record; a routing line would read as a default record at layer 0.");
        AssertEx.Equal(expected: 1, trace.Footer!.Records, "The routing footer does not stand in for the hook footer.");

        var lensRead = ModelLabTraceSpec.ParseLens(content, new ModelLabLensSummary
        {
            Layers = [0],
            TopTokens = 1,
            Records = 1,
            VocabSize = 10
        }, expectedTokens: 1);
        AssertEx.Equal(ModelLabTraceReadStatus.Found, lensRead.Status);
        AssertEx.Equal(expected: 1, lensRead.Footer!.Records);

        var attention = ModelLabTraceSpec.ParseAttention(Encoding.UTF8.GetBytes("""{"s":0,"l":3,"q":2,"k":[[2,1]],"w":[[5000,4000]],"r":[1000]}""" + "\n" + Line(Record(0)) + "\n"), heads: 1);
        AssertEx.Equal(ModelLabTraceReadStatus.Found, attention.Status);
        AssertEx.Equal(expected: 3, attention.Records.Single().Layer);

        var routing = ModelLabTraceSpec.ParseRouting(content, Summary(records: 1), Blocks, generated: 1);
        AssertEx.Equal(ModelLabTraceReadStatus.Found, routing.Status, "And the routing reader skips the other sections.");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ARoutingRun_ReadsBackPerStepAndBlock_WithTheWeightsTheModelUsed(bool truncated)
    {
        var tokens = truncated ? 3 : 2;
        using var process = new ScriptedProcess
        {
            TraceContent = Content([Record(0), Record(1)], [RoutingFooter(records: 2, dropped: truncated ? 1 : 0)]),
            TraceResult = Result(tokens, Summary(records: 2, truncated: truncated))
        };
        await using var host = await LabHost.StartAsync(process);
        var id = await RunAsync(host, process, recordAttention: false);
        using var client = host.Factory.CreateClient();

        using (var body = await GetJsonAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/routing", HttpStatusCode.OK))
        {
            var root = body.RootElement;
            AssertEx.Equal("[1,3]", root.GetProperty("layers").GetRawText());
            AssertEx.Equal(expected: 64, root.GetProperty("experts").GetInt32());
            AssertEx.Equal(expected: 2, root.GetProperty("expertsPerToken").GetInt32());
            AssertEx.True(root.GetProperty("normalizedTopK").GetBoolean());
            AssertEx.Equal(truncated, root.GetProperty("truncated").GetBoolean());
            AssertEx.Equal(truncated ? 1 : 0, root.GetProperty("droppedSteps").GetInt32());
            var steps = root.GetProperty("steps");
            AssertEx.Equal(expected: 2, steps.GetArrayLength());
            AssertEx.Equal(expected: 1, steps[1].GetProperty("step").GetInt32());
            var first = steps[1].GetProperty("blocks")[0];
            AssertEx.Equal(expected: 1, first.GetProperty("layer").GetInt32());
            AssertEx.Equal("[12,5]", first.GetProperty("experts").GetRawText());
            AssertEx.Equal(expected: 0.310546875, first.GetProperty("weights")[0].GetDouble(), "The bfloat16 value itself, not a rounding of it.");
            AssertEx.Equal(expected: 0.125, first.GetProperty("weights")[1].GetDouble());
            var last = steps[1].GetProperty("blocks")[1];
            AssertEx.Equal(expected: 3, last.GetProperty("layer").GetInt32());
            AssertEx.Equal("[63,0]", last.GetProperty("experts").GetRawText());
            AssertEx.Equal("[0.5,1]", last.GetProperty("weights").GetRawText());
        }

        using (var body = await GetJsonAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/trace", HttpStatusCode.OK))
        {
            AssertEx.Equal(expected: 1, body.RootElement.GetProperty("records").GetArrayLength(), "The trace route serves the hook record only.");
        }

        using (var body = await GetJsonAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}", HttpStatusCode.OK))
        {
            var routing = body.RootElement.GetProperty("result").GetProperty("routing");
            AssertEx.Equal(expected: 2, routing.GetProperty("records").GetInt32());
            AssertEx.Equal(ModelLabTraceSpec.RoutingWeightEncoding, routing.GetProperty("weightEncoding").GetString());
        }
    }

    /// <summary>A run from before contract 8 and a dense model's run look the same to the node: the result names no routing.</summary>
    [Test]
    public async Task ARunWithoutRouting_ASteeringRow_AndAnUnknownRun_HaveNoRouting()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);
        var before = await RunAsync(host, process, recordAttention: false);
        var steering = Guid.NewGuid();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>();
            await store.CreateRunningAsync(new ModelLabExperimentCreate
            {
                Id = steering,
                Kind = ModelLabExperimentKind.Steering,
                RepoId = "allenai/OLMoE-1B-7B-0924-Instruct",
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
                ResultJson = Encoding.UTF8.GetBytes(Result(tokens: 2, Summary(records: 2))),
                TraceBlobId = Guid.NewGuid(),
                TraceSha256 = "00",
                TraceByteCount = 1
            });
        }

        using var client = host.Factory.CreateClient();
        foreach (var id in new[]
                 {
                     before,
                     steering,
                     Guid.NewGuid()
                 })
        {
            await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/routing", HttpStatusCode.NotFound);
        }

        var dense = AssertEx.NotNull(await host.Reader.GetAsync(before, CancellationToken.None));
        AssertEx.Null(AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(dense.ResultJson!.Value)).Routing);
        await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{before}/trace", HttpStatusCode.OK);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task RoutingLinesThatDoNotReadBack_Are422_WhileTheTraceStillReads(bool tamper)
    {
        var broken = Record(1);
        broken["x"]![0]![0] = 64;
        using var process = new ScriptedProcess
        {
            TraceContent = Content([Record(0), tamper ? Record(1) : broken], [RoutingFooter(records: 2)]),
            TraceResult = Result(tokens: 2, Summary(records: 2))
        };
        await using var host = await LabHost.StartAsync(process);
        var id = await RunAsync(host, process, recordAttention: false);
        if (tamper)
        {
            var record = AssertEx.NotNull(await host.Reader.GetAsync(id, CancellationToken.None));
            var blob = Path.Combine(Scope(host, id), record.TraceBlobId!.Value.ToString("N") + ".blob");
            var bytes = await File.ReadAllBytesAsync(blob);
            bytes[^1] ^= 0xFF;
            await File.WriteAllBytesAsync(blob, bytes);
        }

        using var client = host.Factory.CreateClient();
        await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/routing", HttpStatusCode.UnprocessableEntity);
        await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/trace", tamper ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK);
    }

    /// <summary>A result stored with no token list still deserializes; the lines cannot be counted against it, so the read is an integrity failure, not a crash.</summary>
    [Test]
    public async Task AStoredResultWithNullTokens_Is422_EvenWithAnIntactBlob()
    {
        var result = AssertEx.NotNull(JsonNode.Parse(Result(tokens: 2, Summary(records: 2))));
        result["tokens"] = null;
        using var process = new ScriptedProcess
        {
            TraceContent = Content([Record(0), Record(1)], [RoutingFooter(records: 2)]),
            TraceResult = result.ToJsonString()
        };
        await using var host = await LabHost.StartAsync(process);
        var id = await RunAsync(host, process, recordAttention: false);
        AssertEx.Null(AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(AssertEx.NotNull(await host.Reader.GetAsync(id, CancellationToken.None)).ResultJson!.Value)).Tokens,
            "The stored result keeps its null token list.");

        using var client = host.Factory.CreateClient();
        await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/routing", HttpStatusCode.UnprocessableEntity);
    }

    private static ModelLabRouting Parse(string content, ModelLabRoutingSummary summary, int generated) =>
        ModelLabTraceSpec.ParseRouting(Encoding.UTF8.GetBytes(content), summary, Blocks, generated);

    /// <summary>Blocks 1 and 3, two picks each from 64 experts. Step 1 is the one the route test reads in full.</summary>
    private static JsonObject Record(int step, int picks = 2) =>
        picks == 2
            ? new JsonObject
            {
                ["s"] = step,
                ["x"] = new JsonArray(new JsonArray(12, 5), new JsonArray(63, 0)),
                ["w"] = new JsonArray(new JsonArray(Bits031, Bits0125), new JsonArray(Bits05, Bits1))
            }
            : new JsonObject
            {
                ["s"] = step,
                ["x"] = new JsonArray([.. Enumerable.Range(0, 2).Select(_ => new JsonArray([.. Enumerable.Range(0, picks).Select(static id => JsonValue.Create(id))]))]),
                ["w"] = new JsonArray([.. Enumerable.Range(0, 2).Select(_ => new JsonArray([.. Enumerable.Repeat(Bits0125, picks).Select(static bits => JsonValue.Create(bits))]))])
            };

    private static string Line(JsonObject record) =>
        new JsonObject
        {
            ["routing"] = record.DeepClone()
        }.ToJsonString();

    private static string Line(int step, JsonNode?[] ids, JsonNode?[] bits) =>
        Line(new JsonObject
        {
            ["s"] = step,
            ["x"] = new JsonArray(ids),
            ["w"] = new JsonArray(bits)
        });

    private static ModelLabRoutingSummary Summary(int records, bool normalizedTopK = true, bool truncated = false) =>
        new()
        {
            Layers = [1, 3],
            Experts = 64,
            ExpertsPerToken = 2,
            NormalizedTopK = normalizedTopK,
            WeightEncoding = ModelLabTraceSpec.RoutingWeightEncoding,
            Records = records,
            Truncated = truncated
        };

    private static string RoutingFooter(int records, int dropped = 0, bool? truncated = null) =>
        new JsonObject
        {
            ["routingFooter"] = new JsonObject
            {
                ["truncated"] = truncated ?? dropped > 0,
                ["records"] = records,
                ["dropped"] = dropped,
                ["payloadBytes"] = 100,
                ["cap"] = 746_656
            }
        }.ToJsonString();

    private static string Footer() =>
        """{"footer":true,"truncated":false,"records":1,"dropped":0,"payloadBytes":10,"cap":262144}""";

    /// <summary>The hook record and footer first, then the routing section, as the script's writer lays them out.</summary>
    private static string Content(IEnumerable<JsonObject> records, IEnumerable<string> routingFooters)
    {
        var lines = new List<string>
        {
            HookRecord,
            Footer()
        };
        lines.AddRange(records.Select(Line));
        lines.AddRange(routingFooters);
        return string.Join('\n', lines) + "\n";
    }

    private static string Result(int tokens, ModelLabRoutingSummary routing) =>
        JsonSerializer.Serialize(new ModelLabTraceResult
        {
            PromptTokens = 3,
            Tokens =
            [
                .. Enumerable.Range(0, tokens).Select(static _ => new ModelLabResultToken
                {
                    Id = 7,
                    Text = "the"
                })
            ],
            Text = "the",
            BlockCount = Blocks,
            Routing = routing
        }, JsonSerializerOptions.Web);
}
