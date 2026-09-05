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

    /// <summary>
    ///     Booleans compare for equality and nothing else. Ordering against a boolean LITERAL is refused when the
    ///     condition is parsed (see the rejection table below); this is the runtime half — a boolean output against a
    ///     number is a comparison the evaluator can be asked for and answers "no" to.
    /// </summary>
    [Test]
    public void Evaluate_ComparingBooleans_AnswersEquality()
    {
        AssertEx.True(Evaluate("passed", "eq", "true", Output));
        AssertEx.False(Evaluate("passed", "eq", "false", Output));
        AssertEx.True(Evaluate("passed", "ne", "false", Output));
        AssertEx.False(Evaluate("passed", "gt", "0", Output), "a boolean has no ordering against a number either.");
    }

    /// <summary>
    ///     Through a double, 9007199254740992 and 9007199254740993 are the same value: an ordering over ids, byte
    ///     counts or timestamps past 2^53 would answer on a rounding artefact rather than on the numbers an author
    ///     wrote.
    /// </summary>
    [Test]
    public void Evaluate_OrdersAdjacentIntegersAbove2Pow53Exactly()
    {
        const string Big = """{"n":9007199254740993}""";
        const string Neighbour = "9007199254740992";

        AssertEx.True(Evaluate("n", "gt", Neighbour, Big), "9007199254740993 is greater than its neighbour, and only an exact comparison can say so.");
        AssertEx.False(Evaluate("n", "lt", Neighbour, Big));
        AssertEx.False(Evaluate("n", "eq", Neighbour, Big), "the two are distinct integers, however they round as doubles.");
        AssertEx.True(Evaluate("n", "ne", Neighbour, Big));
        AssertEx.True(Evaluate("n", "eq", "9007199254740993", Big), "and a number still equals itself.");
    }

    /// <summary>
    ///     The same argument one range further out. 30-digit ids are past <c>decimal</c> too, and a chain that ends at
    ///     double reads two of them differing in the last digit as one value — every relational operator would then
    ///     answer on the rounding rather than on the numbers.
    /// </summary>
    [Test]
    public void Evaluate_OrdersIntegerTokensBeyondDecimalRangeExactly()
    {
        const string Big = """{"n":100000000000000000000000000002}""";
        const string Neighbour = "100000000000000000000000000001";

        AssertEx.True(Evaluate("n", "gt", Neighbour, Big), "…002 is greater than …001, and only an exact comparison over the tokens can say so.");
        AssertEx.False(Evaluate("n", "lt", Neighbour, Big));
        AssertEx.False(Evaluate("n", "eq", Neighbour, Big), "the two are distinct integers, however they round as doubles.");
        AssertEx.True(Evaluate("n", "ne", Neighbour, Big));

        // The documented ceiling, not an accident: an exponent form is not an integer token, so this pair falls to the
        // double arm, where 1e29 and 1e29 + 2 are the same value. Recording the answer the double path gives is the
        // point — the day the significand/exponent upgrade in Order lands, this row is what has to change.
        AssertEx.True(Evaluate("n", "eq", "1e29", Big), "an integer token against its exponent form is answered by the double fallback.");
    }

    /// <summary>
    ///     Exponent forms are numbers like any other, and neither side has to be spelled the same way as the other for
    ///     the comparison to be exact.
    /// </summary>
    [Test]
    public void Evaluate_ComparesExponentFormNumbers()
    {
        const string Exponent = """{"n":1.5e3}""";

        AssertEx.True(Evaluate("n", "eq", "1500", Exponent));
        AssertEx.True(Evaluate("n", "gt", "1499.5", Exponent));
        AssertEx.False(Evaluate("n", "lt", "1500", Exponent));
    }

    [Test]
    public void Evaluate_WithNoCondition_IsUnconditional() =>
        AssertEx.True(DevWorkflowCondition.Evaluate(condition: null, output: null));

    [Test]
    [Arguments("""{"op":"eq","value":1}""", "non-empty 'path'")]
    [Arguments("""{"path":"","op":"eq","value":1}""", "non-empty 'path'")]
    [Arguments("""{"path":"a..b","op":"eq","value":1}""", "not a dot path")]

    // Dot paths only, per the brief: no wildcards, no indexes, no functions. Any of them saved is a property name
    // literally spelled that way, which no output document carries — a dead edge instead of a refusal.
    [Arguments("""{"path":"items[0].name","op":"eq","value":1}""", "not a dot path")]
    [Arguments("""{"path":"*.value","op":"eq","value":1}""", "not a dot path")]
    [Arguments("""{"path":"foo()","op":"eq","value":1}""", "not a dot path")]
    [Arguments("""{"path":"a. b","op":"eq","value":1}""", "not a dot path")]

    // Enum.TryParse takes a numeric token, so "3" would parse into an operator no member has and Evaluate would route
    // on it.
    [Arguments("""{"path":"a","op":"3","value":1}""", "needs an 'op'")]
    [Arguments("""{"path":"a","op":"-1","value":1}""", "needs an 'op'")]
    [Arguments("""{"path":"a","op":"between","value":1}""", "needs an 'op'")]
    [Arguments("""{"path":"a"}""", "needs an 'op'")]
    [Arguments("""{"path":"a","op":"eq"}""", "needs a 'value'")]
    [Arguments("\"a string\"", "must be an object")]

    // A composite value is not a comparison Evaluate can refuse — it is one it can never make. Left to run time it
    // would read as a dead edge, which is a hang with nothing in the log rather than an error anyone can act on. An
    // ordering against a boolean literal is dead the same way, for every output the node could ever produce.
    [Arguments("""{"path":"a","op":"eq","value":{"nested":1}}""", "must be a scalar")]
    [Arguments("""{"path":"a","op":"eq","value":[1,2]}""", "must be a scalar")]
    [Arguments("""{"path":"a","op":"gt","value":true}""", "compare for equality only")]
    [Arguments("""{"path":"a","op":"lte","value":false}""", "compare for equality only")]
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
