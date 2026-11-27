namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The job file the host writes for <c>nnplayground.py</c>, and the checks its two outputs must pass before anything is saved.</summary>
/// <remarks>The job, trace and summary are compared with the committed contract fixture; minimal inline documents of the same shape carry the refusal cases.</remarks>
[Category(TestCategories.Unit)]
public sealed class ModelLabPlaygroundContractTests
{
    internal const string MinimalConfig = """{"dataset":"xor","learningRate":0.5,"residual":false,"sharedBlock":true,"depth":2,"hidden":4,"steps":8,"seed":0}""";

    internal const string MinimalTrace =
        """{"formatVersion":1,"template":"tiny-mlp-v1","torchVersion":"2.13.0+cpu","device":"cpu","dtype":"float64","threads":1,"config":"""
        + MinimalConfig
        + ""","stepDefinition":{"loss":"bce_with_logits_mean"},"data":{"count":4,"probes":[0,1,2,3],"x":[[0,0],[0,1],[1,0],[1,1]],"y":[0,1,1,0]},"modules":[{"path":"block","class":"Linear","outputActivation":null}],"parameters":[{"id":"block.weight","module":"block","shape":[4,4]}],"occurrences":[{"module":"block","visit":0},{"module":"block","visit":1}],"recorded":[],"outcome":{"kind":"finished","atStep":null,"updatesApplied":8}}""";

    internal const string MinimalResult =
        """{"formatVersion":1,"template":"tiny-mlp-v1","config":"""
        + MinimalConfig
        + ""","outcome":{"kind":"finished","atStep":null,"updatesApplied":8},"finalLoss":0.125,"finalAccuracy":1}""";

    private static readonly string[] TraceMembers =
        ["torchVersion", "device", "dtype", "threads", "config", "stepDefinition", "data", "modules", "parameters", "occurrences", "recorded", "outcome"];

    private static readonly string[] ResultMembers = ["config", "outcome", "finalLoss", "finalAccuracy"];

    private static readonly string[] ConfigMembers = ["dataset", "learningRate", "residual", "sharedBlock", "depth", "hidden", "steps", "seed"];

    [Test]
    public void TheJob_SerializesToTheFixturesJob()
    {
        using var fixture = ReadFixture();
        var expected = JsonNode.Parse(fixture.RootElement.GetProperty("job").GetRawText());

        var job = new ModelLabPlaygroundJobV1
        {
            Config = new ModelLabPlaygroundConfigV1
            {
                Dataset = ModelLabPlaygroundDataset.Xor,
                LearningRate = 0.5,
                Residual = false,
                SharedBlock = true,
                Depth = 2,
                Hidden = 4,
                Steps = 8,
                Seed = 0
            },
            TracePath = "/job/trace.json",
            ResultPath = "/job/result.json"
        };
        var actual = JsonNode.Parse(JsonSerializer.SerializeToUtf8Bytes(job, TrainingJson.Options));

        AssertEx.True(JsonNode.DeepEquals(expected, actual), $"expected {expected!.ToJsonString()}\nactual   {actual!.ToJsonString()}");
    }

    /// <summary>The trace and summary <c>nnplayground.py</c> wrote for the fixture's job, as the host would read them.</summary>
    [Test]
    public void TheFixturesTraceAndSummary_PassTheOutputChecks_AndTheSummaryReadsBack()
    {
        using var fixture = ReadFixture();
        var summary = fixture.RootElement.GetProperty("summary").GetRawText();

        AssertEx.Null(ModelLabPlaygroundChecks.TraceProblemOrNull(Encoding.UTF8.GetBytes(FixtureTrace().ToJsonString())));
        AssertEx.Null(ModelLabPlaygroundChecks.ResultProblemOrNull(Encoding.UTF8.GetBytes(summary)));

        var result = AssertEx.NotNull(JsonSerializer.Deserialize<ModelLabPlaygroundResultV1>(summary, TrainingJson.Options));
        AssertEx.Equal(ModelLabPlaygroundDataset.Xor, result.Config.Dataset);
        AssertEx.True(result.Config.SharedBlock, "the fixture's job shares its block");
        AssertEx.Equal(expected: 8, result.Outcome.UpdatesApplied);
        AssertEx.Equal(fixture.RootElement.GetProperty("summary").GetProperty("finalLoss").GetDouble(), result.FinalLoss ?? double.NaN);
    }

    [Test]
    public void TheMinimalTraceAndSummary_PassTheOutputChecks_AndTheSummaryReadsBack()
    {
        AssertEx.Null(ModelLabPlaygroundChecks.TraceProblemOrNull(Encoding.UTF8.GetBytes(MinimalTrace)));
        AssertEx.Null(ModelLabPlaygroundChecks.ResultProblemOrNull(Encoding.UTF8.GetBytes(MinimalResult)));

        var result = AssertEx.NotNull(JsonSerializer.Deserialize<ModelLabPlaygroundResultV1>(MinimalResult, TrainingJson.Options));
        AssertEx.Equal(ModelLabPlaygroundDataset.Xor, result.Config.Dataset);
        AssertEx.Equal("finished", result.Outcome.Kind);
        AssertEx.Null(result.Outcome.AtStep);
        AssertEx.Equal(expected: 0.125, result.FinalLoss ?? double.NaN);
    }

    /// <summary>A divergence is a finding: a null loss and accuracy, and the step it happened at, pass.</summary>
    [Test]
    public void ADivergedSummary_WithANonFiniteLoss_PassesTheChecks()
    {
        var diverged = Mutate(MinimalResult, static root =>
        {
            root["outcome"] = JsonNode.Parse("""{"kind":"diverged","atStep":0,"updatesApplied":0}""");
            root["finalLoss"] = null;
            root["finalAccuracy"] = null;
        });

        AssertEx.Null(ModelLabPlaygroundChecks.ResultProblemOrNull(diverged));
    }

    [Test]
    public void AWrongFormatVersion_OrAForeignTemplate_IsRefused()
    {
        foreach (var document in new[]
                 {
                     MinimalTrace,
                     MinimalResult
                 })
        {
            AssertEx.Contains(Check(document, Mutate(document, static root => root["formatVersion"] = 2)), "format");
            AssertEx.Contains(Check(document, Mutate(document, static root => root["formatVersion"] = "1")), "format");
            AssertEx.Contains(Check(document, Mutate(document, static root => root.Remove("formatVersion"))), "format");
            AssertEx.Contains(Check(document, Mutate(document, static root => root["template"] = "another-v1")), "another template");
        }
    }

    [Test]
    public void EveryMissingOrMistypedStructuralMember_IsRefused_AndNamed()
    {
        foreach (var member in TraceMembers)
        {
            AssertEx.Contains(Check(MinimalTrace, Mutate(MinimalTrace, root => root.Remove(member))), $"'{member}'");
            AssertEx.Contains(Check(MinimalTrace, Mutate(MinimalTrace, root => root[member] = true)), $"'{member}'");
        }

        foreach (var member in ResultMembers)
        {
            AssertEx.Contains(Check(MinimalResult, Mutate(MinimalResult, root => root.Remove(member))), $"'{member}'");
            AssertEx.Contains(Check(MinimalResult, Mutate(MinimalResult, root => root[member] = "x")), $"'{member}'");
        }

        foreach (var member in ConfigMembers)
        {
            AssertEx.Contains(Check(MinimalResult, Mutate(MinimalResult, root => root["config"]!.AsObject().Remove(member))), $"'config.{member}'");
            AssertEx.Contains(Check(MinimalResult, Mutate(MinimalResult, root => root["config"]![member] = null)), $"'config.{member}'");
            AssertEx.Contains(Check(MinimalResult, Mutate(MinimalResult, root => root["config"]![member] = new JsonArray())), $"'config.{member}'");
        }

        foreach (var document in new[]
                 {
                     MinimalTrace,
                     MinimalResult
                 })
        {
            AssertEx.Contains(Check(document, Mutate(document, static root => root["outcome"]!["kind"] = "paused")), "'outcome.kind'");
            AssertEx.Contains(Check(document, Mutate(document, static root => root["outcome"]!.AsObject().Remove("atStep"))), "'outcome.atStep'");
            AssertEx.Contains(Check(document, Mutate(document, static root => root["outcome"]!["updatesApplied"] = null)), "'outcome.updatesApplied'");
        }
    }

    /// <summary>What playback allows passes: a step with no backward pass or update, a residual stream, null cells, and a bias row as a tensor.</summary>
    [Test]
    public void ATraceWithNullPhasesStreamsAndCells_PassesTheChecks()
    {
        var trace = FixtureTrace();
        var step = trace["recorded"]![0]!.AsObject();
        step["backward"] = null;
        step["update"] = null;
        step["loss"]!["value"] = null;
        step["forward"]![0]!["stream"] = JsonNode.Parse("[[1,null]]");
        trace["recorded"]![1]!["update"] = JsonNode.Parse("""[{"p":0,"before":[1,null],"after":[[1]]}]""");
        trace["modules"]![0]!["outputActivation"] = "tanh";

        AssertEx.Null(ModelLabPlaygroundChecks.TraceProblemOrNull(Encoding.UTF8.GetBytes(trace.ToJsonString())));
    }

    /// <summary>A trace the stepper could not play is refused before it is saved, so its save never trims a playable run; the problem's path is named.</summary>
    [Test]
    public void ATraceThePlaybackCannotRead_IsRefused_AndNamed()
    {
        var cases = new Dictionary<string, Action<JsonObject>>(StringComparer.Ordinal)
        {
            ["'data.count'"] = static root => root["data"] = new JsonObject(),
            ["'data.x'"] = static root => root["data"]!["x"] = JsonNode.Parse("""[["a"]]"""),
            ["'config.seed'"] = static root => root["config"]!.AsObject().Remove("seed"),
            ["'modules[0].outputActivation'"] = static root => root["modules"]![0]!.AsObject().Remove("outputActivation"),
            ["'modules[1].outputActivation'"] = static root => root["modules"]![1]!["outputActivation"] = 1,
            ["'modules[0].path'"] = static root => root["modules"]![0] = "embed",
            ["'parameters[0].shape'"] = static root => root["parameters"]![0]!["shape"] = "4x2",
            ["'occurrences[0].visit'"] = static root => root["occurrences"]![0]!["visit"] = null,
            ["'recorded[0].step'"] = static root => root["recorded"]![0]!["step"] = "0",
            ["'recorded[0].forward'"] = static root => root["recorded"]![0]!["forward"]![0]!.AsObject().Remove("output"),
            ["'recorded[1].forward'"] = static root => root["recorded"]![1]!["forward"]![0]!["stream"] = 1,
            ["'recorded[0].loss'"] = static root => root["recorded"]![0]!["loss"] = null,
            ["'recorded[0].backward'"] = static root => root["recorded"]![0]!["backward"] = new JsonObject(),
            ["'recorded[2].backward'"] = static root => root["recorded"]![2]!["backward"]!["parameterGrads"]![0]!["grad"] = JsonNode.Parse("[[[1]]]"),
            ["'recorded[0].update'"] = static root => root["recorded"]![0]!.AsObject().Remove("update")
        };

        foreach (var (expected, change) in cases)
        {
            var trace = FixtureTrace();
            change(trace);
            AssertEx.Contains(ModelLabPlaygroundChecks.TraceProblemOrNull(Encoding.UTF8.GetBytes(trace.ToJsonString())), expected);
        }
    }

    /// <summary>The stepper's schema takes an index only as a non-negative integer, so a negative, fractional or unsafe one is refused where it sits.</summary>
    [Test]
    public void AnIndexThatIsNegativeFractionalOrUnsafe_IsRefused_AndNamed()
    {
        var cases = new Dictionary<string, Action<JsonObject>>(StringComparer.Ordinal)
        {
            ["'data.count'"] = static root => root["data"]!["count"] = 1.5,
            ["'data.probes'"] = static root => root["data"]!["probes"]![0] = -1,
            ["'parameters[0].shape'"] = static root => root["parameters"]![0]!["shape"]![1] = -2,
            ["'occurrences[0].visit'"] = static root => root["occurrences"]![0]!["visit"] = 0.5,
            ["'recorded[0].step'"] = static root => root["recorded"]![0]!["step"] = -1,
            ["'recorded[1].forward'"] = static root => root["recorded"]![1]!["forward"]![0]!["o"] = -1,
            ["'recorded[2].backward'"] = static root => root["recorded"]![2]!["backward"]!["outputGrads"]![0]!["o"] = 1.5,
            ["'recorded[3].backward'"] = static root => root["recorded"]![3]!["backward"]!["parameterGrads"]![0]!["p"] = -1,
            ["'recorded[4].backward'"] = static root => root["recorded"]![4]!["backward"]!["visitGrads"]![0]!["p"] = 9007199254740992,
            ["'recorded[5].update'"] = static root => root["recorded"]![5]!["update"]![0]!["p"] = -1,
            ["'outcome.atStep'"] = static root => root["outcome"]!["atStep"] = -1,
            ["'outcome.updatesApplied'"] = static root => root["outcome"]!["updatesApplied"] = 7.5,
            ["'config.depth'"] = static root => root["config"]!["depth"] = 2.5
        };

        foreach (var (expected, change) in cases)
        {
            var trace = FixtureTrace();
            change(trace);
            AssertEx.Contains(ModelLabPlaygroundChecks.TraceProblemOrNull(Encoding.UTF8.GetBytes(trace.ToJsonString())), expected);
        }

        AssertEx.Contains(Check(MinimalResult, Mutate(MinimalResult, static root => root["outcome"]!["updatesApplied"] = -1)), "'outcome.updatesApplied'");
        AssertEx.Null(ModelLabPlaygroundChecks.TraceProblemOrNull(Encoding.UTF8.GetBytes(MinimalTrace.Replace("\"visit\":1", "\"visit\":1.0", StringComparison.Ordinal))));
    }

    [Test]
    public void ATruncatedOrNonObjectDocument_IsRefused()
    {
        foreach (var document in new[]
                 {
                     MinimalTrace,
                     MinimalResult
                 })
        {
            var bytes = Encoding.UTF8.GetBytes(document);
            AssertEx.Contains(Check(document, bytes[..(bytes.Length / 2)]), "not valid JSON");
            AssertEx.Contains(Check(document, "[]"u8.ToArray()), "format");
        }
    }

    [Test]
    public void AMissingOrOversizeFile_IsRefused_AndOneAtTheCapIsNot()
    {
        using var work = new TempDirectory("playground-contract");
        var path = Path.Combine(work.Path, "trace.json");

        AssertEx.Contains(ModelLabPlaygroundChecks.SizeProblemOrNull(new FileInfo(path), maxBytes: 10, "trace"), "without writing its trace");
        File.WriteAllBytes(path, new byte[10]);
        AssertEx.Null(ModelLabPlaygroundChecks.SizeProblemOrNull(new FileInfo(path), maxBytes: 10, "trace"));
        File.WriteAllBytes(path, new byte[11]);
        AssertEx.Contains(ModelLabPlaygroundChecks.SizeProblemOrNull(new FileInfo(path), maxBytes: 10, "trace"), "larger than its 10-byte cap");
    }

    /// <summary>The training environment resolves for Linux x64 only, so an ARM64 Linux host must not advertise the Model Lab jobs.</summary>
    [Test]
    public void TheScriptsPlatform_IsLinuxX64Only()
    {
        AssertEx.Equal(OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64, TrainingScripts.IsPlatformSupported);
    }

    /// <summary>Runs the trace's checks on a mutated <see cref="MinimalTrace" />, else the summary's.</summary>
    private static string? Check(string original, byte[] document) =>
        string.Equals(original, MinimalTrace, StringComparison.Ordinal)
            ? ModelLabPlaygroundChecks.TraceProblemOrNull(document)
            : ModelLabPlaygroundChecks.ResultProblemOrNull(document);

    private static JsonDocument ReadFixture() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "model-lab-playground-contract.json")));

    /// <summary>The fixture's trace, with each module's <c>outputActivation</c> set to null where the fixture predates it.</summary>
    private static JsonObject FixtureTrace()
    {
        using var fixture = ReadFixture();
        var trace = JsonNode.Parse(fixture.RootElement.GetProperty("trace").GetRawText())!.AsObject();
        foreach (var module in trace["modules"]!.AsArray())
        {
            _ = module!.AsObject().TryAdd("outputActivation", null);
        }

        return trace;
    }

    private static byte[] Mutate(string document, Action<JsonObject> change)
    {
        var root = JsonNode.Parse(document)!.AsObject();
        change(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }
}
