namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The edge-condition truth table. It decides every branch a workflow takes, and it is a comparison rather than an
///     expression language precisely so it can be enumerated.
/// </summary>
public sealed class DevWorkflowConditionTests
{
    private const string Output = """
                                  {
                                    "passed": true,
                                    "branch": "fix",
                                    "report": { "testsFailed": 3, "suite": "unit" }
                                  }
                                  """;

    [Test]
    [Arguments("passed", "eq", "true", true)]
    [Arguments("passed", "eq", "false", false)]
    [Arguments("passed", "ne", "false", true)]
    [Arguments("passed", "ne", "true", false)]
    [Arguments("branch", "eq", "\"fix\"", true)]
    [Arguments("branch", "eq", "\"ship\"", false)]
    [Arguments("branch", "ne", "\"ship\"", true)]
    [Arguments("report.testsFailed", "gt", "0", true)]
    [Arguments("report.testsFailed", "gt", "3", false)]
    [Arguments("report.testsFailed", "gte", "3", true)]
    [Arguments("report.testsFailed", "lt", "4", true)]
    [Arguments("report.testsFailed", "lt", "3", false)]
    [Arguments("report.testsFailed", "lte", "3", true)]
    [Arguments("report.suite", "eq", "\"unit\"", true)]

    // Strings order ordinally, so the relational operators work over them too — 'fix' sorts before 'zzz'.
    [Arguments("branch", "lt", "\"zzz\"", true)]
    [Arguments("branch", "gt", "\"aaa\"", true)]
    [Arguments("branch", "gt", "\"zzz\"", false)]
    [Arguments("branch", "lte", "\"fix\"", true)]
    [Arguments("passed", "exists", null, true)]
    [Arguments("passed", "notExists", null, false)]
    [Arguments("report.testsFailed", "exists", null, true)]
    public void Evaluate_AnswersTheTruthTableOverAPresentPath(string path, string op, string? value, bool expected) =>
        AssertEx.Equal(expected, Evaluate(path, op, value, Output));

    /// <summary>
    ///     Fail-closed on absence, across every operator. An edge that fired on a path the output does not carry would
    ///     route the run on evidence it never had — and a node that produced no output at all is exactly that case.
    /// </summary>
    [Test]
    [Arguments("eq")]
    [Arguments("ne")]
    [Arguments("gt")]
    [Arguments("gte")]
    [Arguments("lt")]
    [Arguments("lte")]
    [Arguments("exists")]
    public void Evaluate_OnAMissingPath_IsFalseForEveryOperatorButNotExists(string op)
    {
        AssertEx.False(Evaluate("absent", op, "1", Output), $"'{op}' must not fire on a path the output does not carry.");
        AssertEx.False(Evaluate("report.absent", op, "1", Output), "a missing leaf under a present object is just as absent.");
        AssertEx.False(Evaluate("passed.deeper", op, "1", Output), "walking into a non-object ends the walk.");
        AssertEx.False(Evaluate("absent", op, "1", output: null), "a node that produced no output at all satisfies nothing.");
    }

    [Test]
    public void Evaluate_NotExists_IsTheOneOperatorThatFiresOnAbsence()
    {
        AssertEx.True(Evaluate("absent", "notExists", value: null, Output));
        AssertEx.True(Evaluate("absent", "notExists", value: null, output: null));
        AssertEx.False(Evaluate("passed", "notExists", value: null, Output));
    }

    /// <summary>
    ///     A type mismatch is not an ordering and must not be invented into one. Without this, comparing a string count
    ///     against a number would silently pick whichever side the runtime happened to coerce.
    /// </summary>
    [Test]
    [Arguments("eq")]
    [Arguments("ne")]
    [Arguments("gt")]
    [Arguments("lt")]
    public void Evaluate_AcrossMismatchedTypes_IsFalse(string op)
    {
        AssertEx.False(Evaluate("branch", op, "1", Output), "a string against a number has no answer.");
        AssertEx.False(Evaluate("report.testsFailed", op, "\"3\"", Output), "and neither does a number against a string.");
        AssertEx.False(Evaluate("passed", op, "1", Output), "nor a boolean against a number.");
    }

    /// <summary>
    ///     A JSON null is PRESENT, so it exists and it is not absent. Treating an explicit null as a missing path would
    ///     make a node that reported "no report" indistinguishable from one that reported nothing at all.
    /// </summary>
    [Test]
    public void Evaluate_OverAnExplicitJsonNull_TreatsItAsPresent()
    {
        const string WithNull = """{"report":null}""";

        AssertEx.True(Evaluate("report", "exists", value: null, WithNull));
        AssertEx.False(Evaluate("report", "notExists", value: null, WithNull));
        AssertEx.True(Evaluate("report", "eq", "null", WithNull));
        AssertEx.False(Evaluate("report", "gt", "0", WithNull), "null has no ordering against a number.");
    }

    [Test]
    [Arguments("gte")]
    [Arguments("lte")]
    public void Evaluate_ForTheInclusiveOperatorsAcrossMismatchedTypes_IsFalse(string op)
    {
        AssertEx.False(Evaluate("branch", op, "1", Output), "a string against a number is not an ordering, inclusive or not.");
        AssertEx.False(Evaluate("passed", op, "\"true\"", Output), "nor a boolean against a string.");
    }

    [Test]
    public void Evaluate_ComparingBooleans_AnswersEqualityAndRefusesOrdering()
    {
        AssertEx.True(Evaluate("passed", "eq", "true", Output));
        AssertEx.False(Evaluate("passed", "gt", "false", Output), "asking whether true is greater than false has no answer worth inventing.");
        AssertEx.False(Evaluate("passed", "lte", "true", Output));
    }

    [Test]
    public void Evaluate_WithNoCondition_IsUnconditional() =>
        AssertEx.True(DevWorkflowCondition.Evaluate(condition: null, output: null));

    [Test]
    [Arguments("""{"op":"eq","value":1}""", "non-empty 'path'")]
    [Arguments("""{"path":"","op":"eq","value":1}""", "non-empty 'path'")]
    [Arguments("""{"path":"a..b","op":"eq","value":1}""", "empty segment")]
    [Arguments("""{"path":"a","op":"between","value":1}""", "needs an 'op'")]
    [Arguments("""{"path":"a"}""", "needs an 'op'")]
    [Arguments("""{"path":"a","op":"eq"}""", "needs a 'value'")]
    [Arguments("\"a string\"", "must be an object")]
    public void Parse_RejectsAConditionNobodyCouldPredictTheRoutingOf(string json, string expectedMessage)
    {
        using var document = JsonDocument.Parse(json);
        var rejection = AssertEx.Throws<DevWorkflowValidationException>(() => DevWorkflowCondition.Parse(document.RootElement, "'a' → 'b'"));

        AssertEx.Contains(rejection.Message, expectedMessage);
    }

    [Test]
    public void Parse_AcceptsExistsWithoutAValue()
    {
        using var document = JsonDocument.Parse("""{"path":"passed","op":"exists"}""");
        var condition = DevWorkflowCondition.Parse(document.RootElement, "'a' → 'b'");

        AssertEx.Equal(DevWorkflowConditionOperator.Exists, condition.Operator);
        AssertEx.Equal("passed", condition.Path);
    }

    /// <summary>
    ///     The parsed value must outlive the document it came from: the graph is parsed once and evaluated on every later
    ///     tick, long after that <c>JsonDocument</c> has been disposed.
    /// </summary>
    [Test]
    public void Parse_ClonesItsValue_SoTheConditionOutlivesTheGraphDocument()
    {
        DevWorkflowCondition condition;
        using (var document = JsonDocument.Parse("""{"path":"branch","op":"eq","value":"fix"}"""))
        {
            condition = DevWorkflowCondition.Parse(document.RootElement, "'a' → 'b'");
        }

        using var output = JsonDocument.Parse(Output);
        AssertEx.True(DevWorkflowCondition.Evaluate(condition, output.RootElement));
    }

    private static bool Evaluate(string path, string op, string? value, string? output)
    {
        var conditionJson = value is null
            ? $$"""{"path":{{JsonSerializer.Serialize(path)}},"op":{{JsonSerializer.Serialize(op)}}}"""
            : $$"""{"path":{{JsonSerializer.Serialize(path)}},"op":{{JsonSerializer.Serialize(op)}},"value":{{value}}}""";

        using var conditionDocument = JsonDocument.Parse(conditionJson);
        var condition = DevWorkflowCondition.Parse(conditionDocument.RootElement, "'a' → 'b'");

        if (output is null)
        {
            return DevWorkflowCondition.Evaluate(condition, output: null);
        }

        using var outputDocument = JsonDocument.Parse(output);
        return DevWorkflowCondition.Evaluate(condition, outputDocument.RootElement);
    }
}
