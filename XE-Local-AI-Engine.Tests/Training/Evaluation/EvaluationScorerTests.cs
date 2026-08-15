namespace XE_Local_AI_Engine.Tests.Training.Evaluation;

using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Evaluation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The scorer matrix. Every verdict here is reproducible from the persisted sample and the persisted response,
///     which is what lets a comparison report be recomputed from storage rather than trusted.
/// </summary>
public sealed class EvaluationScorerTests
{
    private const string WeatherSchema =
        """{"type":"object","properties":{"city":{"type":"string"},"days":{"type":"integer"}},"required":["city"]}""";

    [Test]
    public void Score_WhenTheCallMatches_Passes()
    {
        var entry = EvaluationScorer.Score(SampleId, "tool-call", Expected("""{"city":"Berlin","days":3}"""),
            [new EvaluationToolCall("get_weather", """{"city":"Berlin","days":3}""")]);

        AssertEx.True(entry.Passed, "A matching call is the passing case.");
        AssertEx.Equal("deterministic", entry.ScoredBy, "v1 writes only the deterministic provenance; 'judge' is reserved.");
        AssertEx.Null(entry.Reason);
    }

    [Test]
    public void Score_IgnoresArgumentOrder()
    {
        // Property order is formatting, not meaning: the same call with its keys the other way round must still pass.
        var entry = EvaluationScorer.Score(SampleId, "tool-call", Expected("""{"city":"Berlin","days":3}"""),
            [new EvaluationToolCall("get_weather", """{"days":3,"city":"Berlin"}""")]);

        AssertEx.True(entry.Passed, "Argument order is not meaning.");
    }

    [Test]
    public void ArgumentsMatch_ComparesNumbersByValueAndArraysByOrder()
    {
        // 3 and 3.0 are the same value; a reordered array is a different call. (Whether an `integer`-typed property
        // may be written as 3.0 at all is the schema layer's question, not this one's.)
        AssertEx.True(EvaluationScorer.ArgumentsMatch("""{"days":3}""", """{"days":3.0}"""), "Numeric formatting is not meaning.");
        AssertEx.False(EvaluationScorer.ArgumentsMatch("""{"cities":["a","b"]}""", """{"cities":["b","a"]}"""), "Array order is meaning.");
        AssertEx.False(EvaluationScorer.ArgumentsMatch("""{"city":"Berlin"}""", """{"city":"Berlin","days":3}"""),
            "An extra argument makes it a different call.");
        AssertEx.False(EvaluationScorer.ArgumentsMatch("not json", "{}"), "An unreadable expectation cannot match anything.");
    }

    [Test]
    public void Score_WhenTheToolNameDiffers_Fails()
    {
        var entry = EvaluationScorer.Score(SampleId, "tool-call", Expected("""{"city":"Berlin"}"""),
            [new EvaluationToolCall("get_time", """{"city":"Berlin"}""")]);

        AssertEx.False(entry.Passed);
        AssertEx.True(entry.Reason!.Contains("get_time", StringComparison.Ordinal), "The verdict names what the model actually called.");
    }

    [Test]
    public void Score_WhenTheArgumentValuesDiffer_Fails()
    {
        var entry = EvaluationScorer.Score(SampleId, "tool-call", Expected("""{"city":"Berlin"}"""),
            [new EvaluationToolCall("get_weather", """{"city":"Paris"}""")]);

        AssertEx.False(entry.Passed, "Same tool, different argument value, is a different call.");
    }

    [Test]
    public void Score_WhenTheArgumentsViolateTheSnapshottedSchema_Fails()
    {
        // The required property is missing — caught by the same validator the generation pipeline's argument layer
        // uses, so "valid arguments" means one thing across the module.
        var entry = EvaluationScorer.Score(SampleId, "tool-call", Expected("""{"city":"Berlin"}"""),
            [new EvaluationToolCall("get_weather", """{"days":3}""")]);

        AssertEx.False(entry.Passed);
    }

    [Test]
    public void Score_WhenTheModelMakesNoCall_Fails()
    {
        var entry = EvaluationScorer.Score(SampleId, "tool-call", Expected("""{"city":"Berlin"}"""), []);

        AssertEx.False(entry.Passed);
        AssertEx.True(entry.Reason!.Contains("no tool call", StringComparison.Ordinal));
    }

    [Test]
    public void Score_OnANoToolSample_PassesOnlyWhenNothingIsCalled()
    {
        var expectation = new EvaluationExpectation(ToolName: null, ArgumentsJson: null, ParameterSchema: null);

        AssertEx.True(EvaluationScorer.Score(SampleId, "no-tool", expectation, []).Passed,
            "A no-tool sample passes by the model NOT calling anything.");

        var extra = EvaluationScorer.Score(SampleId, "no-tool", expectation, [new EvaluationToolCall("get_weather", "{}")]);
        AssertEx.False(extra.Passed, "An unnecessary tool call on a no-tool sample is the failure that kind exists to catch.");
        AssertEx.True(extra.Reason!.Contains("get_weather", StringComparison.Ordinal));
    }

    [Test]
    public void Score_WhenTheModelMakesExtraCalls_Fails()
    {
        var entry = EvaluationScorer.Score(SampleId, "tool-call", Expected("""{"city":"Berlin"}"""),
        [
            new EvaluationToolCall("get_weather", """{"city":"Berlin"}"""),
            new EvaluationToolCall("get_weather", """{"city":"Paris"}""")
        ]);

        AssertEx.False(entry.Passed, "One expected call means exactly one call.");
    }

    [Test]
    public void ReadExpectation_TakesTheToolPartAndItsSnapshottedSchema()
    {
        var content = new TrainingSampleContentV1
        {
            SystemInstructions = "be helpful",
            Parts =
            [
                new TrainingSamplePartV1("user", 0, "weather in Berlin?"),
                new TrainingSamplePartV1("tool", 1, ToolCallId: "generated-1", ToolName: "get_weather", Arguments: """{"city":"Berlin"}""")
            ]
        };

        var expectation = EvaluationScorer.ReadExpectation(content, [Snapshot()]);

        AssertEx.Equal("get_weather", expectation.ToolName!);
        AssertEx.Equal(WeatherSchema, expectation.ParameterSchema!);
        AssertEx.Equal("weather in Berlin?", EvaluationScorer.ReadUserPrompt(content)!);
    }

    [Test]
    public void ReadExpectation_WithNoToolPart_ExpectsNoCall()
    {
        // Decided structurally, not from the kind label: an operator is free to name a no-tool kind anything, but the
        // frozen trajectory cannot lie about what it demonstrates.
        var content = new TrainingSampleContentV1
        {
            Parts =
            [
                new TrainingSamplePartV1("user", 0, "hello"),
                new TrainingSamplePartV1("text", 1, "hi there")
            ]
        };

        AssertEx.Null(EvaluationScorer.ReadExpectation(content, [Snapshot()]).ToolName);
    }

    private static Guid SampleId => new("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    private static EvaluationExpectation Expected(string argumentsJson) =>
        new("get_weather", argumentsJson, WeatherSchema);

    private static DatasetToolSnapshotV1 Snapshot() =>
        new("get_weather", "Looks up the weather.", WeatherSchema, RequiresApproval: false, ToolCategory.ReadLocal);
}
