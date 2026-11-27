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

/// <summary>A trace's logit lens: the job's fields and cap, the lens lines beside the hook records in one blob, their check, and the read route.</summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabLensTests
{
    private const string HookRecord = """{"s":0,"l":6,"k":"F","n":3.5,"p":[-1.0,0.0,1.0],"ti":[7,8],"tv":[0.5,-0.25]}""";

    [Test]
    public void TheLensCap_IsTheWidestLinePerStep_AndRaisesTheTraceFilesBound()
    {
        AssertEx.Equal(expected: 1_547_424, ModelLabTraceSpec.LensByteCapFor(24, 128));
        AssertEx.Equal(expected: 2_060_448, ModelLabTraceSpec.LensByteCapFor(32, 128));
        AssertEx.Equal(expected: 4_112_544, ModelLabTraceSpec.LensByteCapFor(64, 128));
        AssertEx.Equal(expected: 8_224_928, ModelLabTraceSpec.LensByteCapFor(64, 256));
        AssertEx.Equal((128 * (64 + (24 * (121 + (5 * 76))))) + 160, ModelLabTraceSpec.LensByteCapFor(24, 128));
        AssertEx.Equal(expected: 21_800_416, ModelLabTraceSpec.MaxTraceByteCap);
        AssertEx.Equal(ModelLabTraceSpec.TraceByteCapFor(ModelLabTraceSpec.MaxTraceLayers, ModelLabTraceSpec.MaxRequestedNewTokens)
                       + ModelLabTraceSpec.LensByteCapFor(ModelLabTraceSpec.MaxTraceLayers, ModelLabTraceSpec.MaxRequestedNewTokens)
                       + ModelLabTraceSpec.RoutingByteCapFor(ModelLabTraceSpec.MaxTraceLayers, ModelLabTraceSpec.MaxRequestedNewTokens), ModelLabTraceSpec.MaxTraceByteCap);
        AssertEx.True(ModelLabTraceSpec.MaxTraceByteCap < ModelLabTraceSpec.MaxAttentionByteCap, "The blob store's bound stays the attention cap.");
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.LensByteCapFor(0, 128));
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.LensByteCapFor(ModelLabTraceSpec.MaxTraceLayers + 1, 128));
        AssertEx.Throws<ArgumentOutOfRangeException>(static () => ModelLabTraceSpec.LensByteCapFor(24, ModelLabTraceSpec.MaxRequestedNewTokens + 1));
    }

    [Test]
    public async Task ATrace_AsksForALensAtEveryBlock_WithItsOwnCap()
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process);

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        await process.Spawned.WaitAsync(TestBudgets.Contended);

        using (var job = JsonDocument.Parse(process.ReadJobFile()))
        {
            AssertEx.Equal(ModelLabTraceSpec.LensTopTokens, job.RootElement.GetProperty("lensTopTokens").GetInt32());
            AssertEx.Equal(ModelLabTraceSpec.LensByteCapFor(24, ModelLabTraceSpec.MaxNewTokens), job.RootElement.GetProperty("lensByteCap").GetInt32(), "All 24 blocks, not the three traced layers.");
            AssertEx.Equal(ModelLabTraceSpec.TraceByteCapFor(3, ModelLabTraceSpec.MaxNewTokens), job.RootElement.GetProperty("traceByteCap").GetInt32(), "The hook records keep their own cap.");
        }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);
        AssertEx.True(start.ExperimentId.HasValue);
    }

    /// <summary>Deeper than the cap allows, or a single block with no earlier block to read.</summary>
    [Test]
    [Arguments(65, "[16,32,48]")]
    [Arguments(1, "[0]")]
    public async Task ACheckpointOutsideTheLensRange_TracesAsBefore_WithoutALens(int blocks, string layers)
    {
        using var process = new ScriptedProcess();
        await using var host = await LabHost.StartAsync(process,
            config: $$$"""{"model_type":"qwen3_5","text_config":{"hidden_size":1024,"num_hidden_layers":{{{blocks}}}}}""",
            ggufBlockCount: blocks);

        var start = await host.Service.StartExperimentAsync(host.Request(), CancellationToken.None);
        AssertEx.True(start.ExperimentId.HasValue, start.RefusalMessage ?? "The checkpoint still traces.");
        await process.Spawned.WaitAsync(TestBudgets.Contended);
        using (var job = JsonDocument.Parse(process.ReadJobFile()))
        {
            AssertEx.False(job.RootElement.TryGetProperty("lensTopTokens", out _), "The script sees both lens fields absent.");
            AssertEx.False(job.RootElement.TryGetProperty("lensByteCap", out _));
            AssertEx.Equal(layers, job.RootElement.GetProperty("layers").GetRawText());
            // Routing needs no earlier block: a single block still gets its cap, a checkpoint past the cap none.
            AssertEx.Equal(blocks <= ModelLabTraceSpec.MaxTraceLayers ? ModelLabTraceSpec.RoutingByteCapFor(blocks, ModelLabTraceSpec.MaxNewTokens) : (int?)null,
                job.RootElement.TryGetProperty("routingByteCap", out var routingCap) ? routingCap.GetInt32() : (int?)null);
        }

        process.Finish(CompleteRun, exitCode: 0);
        await host.Service.InFlight!.WaitAsync(TestBudgets.Contended);

        var record = AssertEx.NotNull(await host.Reader.GetAsync(start.ExperimentId!.Value, CancellationToken.None));
        AssertEx.Equal(ModelLabExperimentStatus.Succeeded, record.Status, record.ErrorMessage ?? "no error");
        AssertEx.Equal(ModelLabTraceReadStatus.NotFound, (await host.Reader.ReadLensAsync(record.Id, CancellationToken.None)).Status);
    }

    [Test]
    public void LensLines_NeverReadAsHookRecords_OrAttentionRows()
    {
        // The lens section overflowed; the hook section did not, and its footer says so.
        var content = Encoding.UTF8.GetBytes(Content([Record(0)], LensFooter(records: 1, truncated: true), hookTruncated: false));

        var trace = ModelLabTraceSpec.ParseTrace(content);

        AssertEx.Equal(ModelLabTraceReadStatus.Found, trace.Status);
        AssertEx.Equal(expected: 6, trace.Records.Single().Layer, "Only the hook record; a lens line would read as a default record at layer 0.");
        AssertEx.False(trace.Footer!.Truncated, "The lens footer does not stand in for the hook footer.");
        AssertEx.Equal(expected: 1, trace.Footer.Records);

        var attention = ModelLabTraceSpec.ParseAttention(Encoding.UTF8.GetBytes("""{"s":0,"l":3,"q":2,"k":[[2,1]],"w":[[5000,4000]],"r":[1000]}""" + "\n" + new JsonObject
        {
            ["lens"] = Record(0)
        }.ToJsonString() + "\n"), heads: 1);
        AssertEx.Equal(ModelLabTraceReadStatus.Found, attention.Status);
        AssertEx.Equal(expected: 3, attention.Records.Single().Layer);
    }

    [Test]
    public void AWellFormedLens_ReadsEveryStep_WithANullEntropyKept_AndANullRankBesideANullLogprob()
    {
        var nanRow = Record(1);
        nanRow["c"]![0] = null;
        nanRow["r"]![0] = null;
        var lens = Parse(Content([Record(0), nanRow], LensFooter(records: 2, truncated: false)), Summary(records: 2), expectedTokens: 2);

        AssertEx.Equal(ModelLabTraceReadStatus.Found, lens.Status);
        AssertEx.Equal(expected: 2, lens.Records.Count);
        AssertEx.Equal(expected: 12, lens.Records[0].ChosenRanks[0]);
        AssertEx.Null(lens.Records[1].ChosenRanks[0], "A row whose chosen logprob is not finite has no rank.");
        AssertEx.Null(lens.Records[1].Entropies[1], "A non-finite entropy is stored as null and served as null.");
        AssertEx.Equal("猫", lens.Records[0].Texts["9"]);
    }

    [Test]
    public void ATruncatedPrefix_Reads_WhenTheLensFooterSaysTheCapCutIt()
    {
        var lens = Parse(Content([Record(0)], LensFooter(records: 1, truncated: true)), Summary(records: 1, truncated: true), expectedTokens: 3);

        AssertEx.Equal(ModelLabTraceReadStatus.Found, lens.Status);
        AssertEx.True(lens.Footer!.Truncated);
        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, Parse(Content([Record(0)], LensFooter(records: 1, truncated: false)), Summary(records: 1), expectedTokens: 3).Status,
            "Fewer steps than tokens without the footer saying so is a broken file, not a short one.");
    }

    [Test]
    [Arguments("a repeated step")]
    [Arguments("a gap")]
    [Arguments("steps out of order")]
    [Arguments("a count the summary does not give")]
    [Arguments("a count the lens footer does not give")]
    [Arguments("no lens footer")]
    [Arguments("more steps than tokens")]
    [Arguments("a rank of 0")]
    [Arguments("a null rank beside a chosen logprob")]
    [Arguments("a rank beside a null chosen logprob")]
    [Arguments("summary blocks that are not 0 to B-1")]
    [Arguments("a summary truncated flag the lens footer does not give")]
    [Arguments("a negative entropy")]
    [Arguments("a top id without a text")]
    [Arguments("a top id with a null text")]
    [Arguments("a negative id")]
    [Arguments("a null lens")]
    [Arguments("a line that does not parse")]
    public void ALensThatFailsItsCheck_IsUnavailable(string defect)
    {
        var first = Record(0);
        var second = Record(1);
        var summary = Summary(records: 2);
        var footer = LensFooter(records: 2, truncated: false);
        var expectedTokens = 2;
        string? extra = null;
        switch (defect)
        {
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
            case "a count the summary does not give":
                summary = Summary(records: 3);
                break;
            case "a count the lens footer does not give":
                footer = LensFooter(records: 3, truncated: false);
                break;
            case "no lens footer":
                footer = null;
                break;
            case "more steps than tokens":
                expectedTokens = 1;
                break;
            case "a rank of 0":
                second["r"]![1] = 0;
                break;
            case "a null rank beside a chosen logprob":
                second["r"]![0] = null;
                break;
            case "a rank beside a null chosen logprob":
                second["c"]![0] = null;
                break;
            case "summary blocks that are not 0 to B-1":
                summary = summary with
                {
                    Layers = [0, 2]
                };
                break;
            case "a summary truncated flag the lens footer does not give":
                summary = summary with
                {
                    Truncated = true
                };
                break;
            case "a negative entropy":
                second["e"]![0] = -0.001;
                break;
            case "a top id without a text":
                second["t"]!.AsObject().Remove("8");
                break;
            case "a top id with a null text":
                second["t"]!["8"] = null;
                break;
            case "a negative id":
                second["i"]![1]![0] = -1;
                second["t"]!["-1"] = "x";
                break;
            case "a null lens":
                extra = """{"lens":null}""";
                break;
            case "a line that does not parse":
                extra = """{"lens":{"s":"zero"}}""";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect), defect, "No such defect.");
        }

        var content = Content([first, second], footer) + (extra is null ? string.Empty : extra + "\n");

        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, Parse(content, summary, expectedTokens).Status, defect);
    }

    /// <summary>Every per-block list must have one entry per block, and both top lists one per top token.</summary>
    [Test]
    [Arguments("i", false)]
    [Arguments("p", false)]
    [Arguments("c", false)]
    [Arguments("r", false)]
    [Arguments("e", false)]
    [Arguments("i", true)]
    [Arguments("p", true)]
    public void AListOfTheWrongLength_IsUnavailable(string field, bool insideTheFirstBlock)
    {
        var second = Record(1);
        var list = insideTheFirstBlock ? second[field]![0]!.AsArray() : second[field]!.AsArray();
        list.RemoveAt(list.Count - 1);

        var lens = Parse(Content([Record(0), second], LensFooter(records: 2, truncated: false)), Summary(records: 2), expectedTokens: 2);

        AssertEx.Equal(ModelLabTraceReadStatus.Unavailable, lens.Status, $"{field}, inside a block: {insideTheFirstBlock}");
    }

    /// <summary>Each section has its own footer: an overflow in one never marks the other truncated, on either route.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ALensRun_ReadsBackPerStepAndBlock_AndEachSectionReportsItsOwnTruncation(bool lensOverflowed)
    {
        // The lens kept two of three steps, or the hook records overflowed and the lens kept all of two.
        var tokens = lensOverflowed ? 3 : 2;
        using var process = new ScriptedProcess
        {
            TraceContent = Content([Record(0), Record(1)], LensFooter(records: 2, truncated: lensOverflowed, dropped: lensOverflowed ? 1 : 0), hookTruncated: !lensOverflowed),
            TraceResult = Result(tokens, Summary(records: 2, truncated: lensOverflowed))
        };
        await using var host = await LabHost.StartAsync(process);
        var id = await RunAsync(host, process, recordAttention: false);
        using var client = host.Factory.CreateClient();

        using (var body = await GetJsonAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/lens", HttpStatusCode.OK))
        {
            var root = body.RootElement;
            AssertEx.Equal("[0,1]", root.GetProperty("layers").GetRawText());
            AssertEx.Equal(expected: 2, root.GetProperty("topTokens").GetInt32());
            AssertEx.True(root.GetProperty("tiedEmbeddings").GetBoolean());
            AssertEx.Equal(expected: 248_320, root.GetProperty("vocabSize").GetInt32());
            AssertEx.Equal(lensOverflowed, root.GetProperty("truncated").GetBoolean());
            AssertEx.Equal(lensOverflowed ? 1 : 0, root.GetProperty("droppedSteps").GetInt32());
            var steps = root.GetProperty("steps");
            AssertEx.Equal(expected: 2, steps.GetArrayLength());
            AssertEx.Equal(expected: 1, steps[1].GetProperty("step").GetInt32());
            var first = steps[0].GetProperty("blocks")[0];
            AssertEx.Equal(expected: 0, first.GetProperty("layer").GetInt32());
            AssertEx.Equal("[7,8]", first.GetProperty("ids").GetRawText());
            AssertEx.Equal("[-0.5,-1.25]", first.GetProperty("logprobs").GetRawText());
            AssertEx.Equal(expected: -3.0, first.GetProperty("chosenLogprob").GetDouble());
            AssertEx.Equal(expected: 12, first.GetProperty("chosenRank").GetInt32());
            AssertEx.Equal(expected: 4.25, first.GetProperty("entropy").GetDouble());
            var last = steps[0].GetProperty("blocks")[1];
            AssertEx.Equal(expected: 1, last.GetProperty("layer").GetInt32());
            AssertEx.Equal(JsonValueKind.Null, last.GetProperty("entropy").ValueKind);
            var texts = root.GetProperty("texts").EnumerateArray().ToDictionary(static text => text.GetProperty("id").GetInt32(), static text => text.GetProperty("text").GetString());
            AssertEx.Equal(expected: 3, texts.Count, "Ids 7, 8 and 9 once each, though both steps name them.");
            AssertEx.Equal("猫", texts[9]);
        }

        using (var body = await GetJsonAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/trace", HttpStatusCode.OK))
        {
            AssertEx.Equal(expected: 1, body.RootElement.GetProperty("records").GetArrayLength(), "The trace route serves the hook record only.");
            AssertEx.Equal(!lensOverflowed, body.RootElement.GetProperty("truncated").GetBoolean());
        }

        using (var body = await GetJsonAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}", HttpStatusCode.OK))
        {
            AssertEx.Equal(expected: 2, body.RootElement.GetProperty("result").GetProperty("lens").GetProperty("records").GetInt32());
        }
    }

    [Test]
    public async Task ARunFromBeforeContractFive_ASteeringRow_AndAnUnknownRun_HaveNoLens()
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
            await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/lens", HttpStatusCode.NotFound);
        }

        await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{before}/trace", HttpStatusCode.OK);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task LensLinesThatDoNotReadBack_Are422_WhileTheTraceStillReads(bool tamper)
    {
        var broken = Record(1);
        broken["r"]![0] = 0;
        using var process = new ScriptedProcess
        {
            TraceContent = Content([Record(0), tamper ? Record(1) : broken], LensFooter(records: 2, truncated: false)),
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
        await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/lens", HttpStatusCode.UnprocessableEntity);
        await ExpectStatusAsync(host, client, $"/api/local/v1/model-lab/experiments/{id}/trace", tamper ? HttpStatusCode.UnprocessableEntity : HttpStatusCode.OK);
    }

    [Test]
    public void AResultFromBeforeContractFive_ReadsWithNoLens_AndAFiveResultRoundTrips()
    {
        var before = AssertEx.NotNull(
            ModelLabTraceSpec.ReadResultOrNull("""{"promptTokens":3,"tokens":[],"text":"hi","layers":[6,12,18],"layerKinds":"LLLF","attnImplementation":"sdpa"}"""u8.ToArray()));
        AssertEx.Null(before.Lens);
        AssertEx.Equal("sdpa", before.AttnImplementation);

        var after = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(Encoding.UTF8.GetBytes(Result(tokens: 2, Summary(records: 2, truncated: true)))));
        var roundTrip = AssertEx.NotNull(ModelLabTraceSpec.ReadResultOrNull(JsonSerializer.SerializeToUtf8Bytes(after, JsonSerializerOptions.Web)));
        foreach (var lens in new[]
                 {
                     after.Lens,
                     roundTrip.Lens
                 })
        {
            var read = AssertEx.NotNull(lens);
            AssertEx.Equal("0,1", string.Join(',', read.Layers));
            AssertEx.Equal(expected: 2, read.TopTokens);
            AssertEx.Equal(expected: 2, read.Records);
            AssertEx.True(read.Truncated);
            AssertEx.True(read.TiedEmbeddings);
            AssertEx.Equal(expected: 248_320, read.VocabSize);
        }
    }

    private static ModelLabLens Parse(string content, ModelLabLensSummary summary, int expectedTokens) =>
        ModelLabTraceSpec.ParseLens(Encoding.UTF8.GetBytes(content), summary, expectedTokens);

    /// <summary>Two blocks, two top tokens each. Block 0 ranks the chosen token 12th; block 1's entropy was not finite.</summary>
    private static JsonObject Record(int step) =>
        new()
        {
            ["s"] = step,
            ["i"] = new JsonArray(new JsonArray(7, 8), new JsonArray(9, 7)),
            ["p"] = new JsonArray(new JsonArray(-0.5, -1.25), new JsonArray(-0.1, -2.5)),
            ["c"] = new JsonArray(-3.0, -0.1),
            ["r"] = new JsonArray(12, 1),
            ["e"] = new JsonArray(4.25, null),
            ["t"] = new JsonObject
            {
                ["7"] = "the",
                ["8"] = " a",
                ["9"] = "猫"
            }
        };

    private static ModelLabLensSummary Summary(int records, bool truncated = false) =>
        new()
        {
            Layers = [0, 1],
            TopTokens = 2,
            Records = records,
            Truncated = truncated,
            TiedEmbeddings = true,
            VocabSize = 248_320
        };

    private static string LensFooter(int records, bool truncated, int dropped = 0) =>
        new JsonObject
        {
            ["lensFooter"] = new JsonObject
            {
                ["truncated"] = truncated,
                ["records"] = records,
                ["dropped"] = dropped,
                ["payloadBytes"] = 100,
                ["cap"] = 1_547_424
            }
        }.ToJsonString();

    /// <summary>The hook record and footer first, then the lens section, as the script's writer lays them out.</summary>
    private static string Content(IEnumerable<JsonObject> records, string? lensFooter, bool hookTruncated = false)
    {
        var lines = new List<string>
        {
            HookRecord,
            $$"""{"footer":true,"truncated":{{(hookTruncated ? "true" : "false")}},"records":1,"dropped":{{(hookTruncated ? 4 : 0)}},"payloadBytes":10,"cap":262144}"""
        };
        lines.AddRange(records.Select(static record => new JsonObject
        {
            ["lens"] = record.DeepClone()
        }.ToJsonString()));
        if (lensFooter is not null)
        {
            lines.Add(lensFooter);
        }

        return string.Join('\n', lines) + "\n";
    }

    private static string Result(int tokens, ModelLabLensSummary lens) =>
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
            BlockCount = 24,
            Lens = lens
        }, JsonSerializerOptions.Web);
}
