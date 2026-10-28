namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Diagnostics;
using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifier execution is table-driven per kind, with the adversarial inputs a verifier meets in the wild:
///     a catastrophic pattern, an oversized answer, unicode, and a boxed fraction that must equal its decimal.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BenchmarkJudgeVerifiersTests
{
    [Test]
    public void Exact_ComparesTheNormalizedAnswer()
    {
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Exact, """{"expected":"42"}""", "  42\n").Passed, "Trim is on by default.");
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.Exact, """{"expected":"42"}""", "the answer is 42").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Exact,
            """{"expected":"Yes","normalize":{"trim":true,"caseInsensitive":true}}""",
            "yes").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Exact,
            """{"expected":"a b c","normalize":{"trim":true,"collapseWhitespace":true}}""",
            "a   b\n\tc ").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Exact,
            """{"expected":"answer","normalize":{"trim":true,"stripMarkdown":true}}""",
            "**answer**").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Exact, """{"expected":"café — naïve 日本"}""", "café — naïve 日本").Passed,
            "Comparison is ordinal over the real characters, not a folded ASCII shadow of them.");
    }

    [Test]
    public void Regex_HonoursMustMatchInBothDirections()
    {
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Regex, """{"pattern":"^SELECT ","mustMatch":true}""", "SELECT * FROM t").Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.Regex, """{"pattern":"^SELECT ","mustMatch":true}""", "DELETE FROM t").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Regex, """{"pattern":"DROP","mustMatch":false}""", "SELECT 1").Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.Regex, """{"pattern":"DROP","mustMatch":false}""", "DROP TABLE t").Passed);
    }

    [Test]
    public void Regex_CatastrophicPatternOnAHugeAnswer_Returns()
    {
        // (a+)+$ on a long non-matching input is the classic ReDoS demo. NonBacktracking runs it in linear time, so
        // this asserts fast instead of hanging; that is why the policy refuses what NonBacktracking cannot compile.
        var answer = new string('a', 100_000) + "b";
        var stopwatch = Stopwatch.StartNew();

        var result = Verify(BenchmarkJudgeCriterionKinds.Regex, """{"pattern":"(a+)+$","mustMatch":true}""", answer);

        stopwatch.Stop();
        AssertEx.False(result.Passed);
        AssertEx.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Matching took {stopwatch.Elapsed}.");
    }

    [Test]
    public void Verifiers_BoundTheAnswerTheyRead()
    {
        var huge = new string('x', BenchmarkJudgeVerifiers.MaximumAnswerChars * 2);

        var result = Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"maxWords":1}""", huge);

        AssertEx.True(result.Passed, "One enormous token is still one word once the read is bounded.");
        AssertEx.Equal(BenchmarkJudgeVerifiers.MaximumAnswerChars,
            BenchmarkJudgeVerifiers.AnswerText([new BenchmarkOutputPart("output", Content: huge)]).Length);
    }

    [Test]
    public void AnswerText_ReadsOnlyTheVisibleOutputParts()
    {
        var graded = BenchmarkOutputParts.ForJudge([
                new BenchmarkOutputPart("reasoning", Content: "hidden scratchpad"),
                new BenchmarkOutputPart("output", Content: "the answer is 7")
            ],
            judgeContextTokens: 4096);

        AssertEx.Equal("the answer is 7", BenchmarkJudgeVerifiers.AnswerText(graded));
    }

    [Test]
    public void JsonSchema_ValidatesTheFencedBlockAgainstTheEnforcedSubset()
    {
        const string Schema =
            """
            {"schema":{"type":"object","additionalProperties":false,"required":["name","tags"],"properties":{"name":{"type":"string"},"tags":{"type":"array","items":{"type":"string"}},"status":{"enum":["ok","bad"]}}}}
            """;

        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.JsonSchema, Schema,
            "Here you go:\n```json\n{\"name\":\"x\",\"tags\":[\"a\"]}\n```\nHope that helps.").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.JsonSchema, Schema, """{"name":"x","tags":[]}""").Passed,
            "An unfenced answer is parsed whole.");
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.JsonSchema, Schema, "not json at all").Passed);
        AssertEx.Contains(Verify(BenchmarkJudgeCriterionKinds.JsonSchema, Schema, """{"name":"x"}""").Detail, "tags");
        AssertEx.Contains(Verify(BenchmarkJudgeCriterionKinds.JsonSchema, Schema, """{"name":1,"tags":[]}""").Detail, "$.name");
        AssertEx.Contains(Verify(BenchmarkJudgeCriterionKinds.JsonSchema, Schema, """{"name":"x","tags":[7]}""").Detail, "$.tags[0]");
        AssertEx.Contains(Verify(BenchmarkJudgeCriterionKinds.JsonSchema, Schema, """{"name":"x","tags":[],"extra":1}""").Detail, "extra");
        AssertEx.Contains(Verify(BenchmarkJudgeCriterionKinds.JsonSchema, Schema, """{"name":"x","tags":[],"status":"maybe"}""").Detail,
            "permitted values");
    }

    [Test]
    public void MathAnswer_ExtractionOrderIsBoxedThenHashThenPhraseThenLastNumber()
    {
        // Each input carries a decoy that a later rule would have picked, so the assertion is about the ORDER and not
        // merely about each rule working.
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, Expected(7), @"Working: 3 and 4. \boxed{7} #### 99 the answer is 99. 99").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, Expected(7), "Working: 3 and 4.\n#### 7\nthe answer is 99. 99").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, Expected(7), "Some 99 working. The answer is 7.").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, Expected(7), "Adding 3 and 4 gives 7").Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, Expected(7), "I could not work it out.").Passed);
        AssertEx.Contains(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, Expected(7), "I could not work it out.").Detail,
            BenchmarkMathAnswer.ExtractionOrder);
    }

    [Test]
    public void MathAnswer_ReadsFractionsThousandsCurrencyAndScientificNotation()
    {
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":0.5}""", @"so \boxed{1/2}").Passed,
            "A boxed fraction must equal its decimal.");
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":0.5}""", @"so \boxed{\frac{1}{2}}").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":1234567}""", "The answer is $1,234,567.").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":1500}""", @"\boxed{1.5e3}").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":-40}""", "#### -40").Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":0.5}""", @"\boxed{1/0}").Passed,
            "A division by zero is not a number, so nothing is read and the criterion fails.");
    }

    [Test]
    public void MathAnswer_TolerancesAreAppliedRelativeAndAbsolute()
    {
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":3.14159265}""", @"\boxed{3.14159265358}").Passed,
            "The default 1e-6 relative tolerance absorbs a rounding difference.");
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":3.14159265}""", @"\boxed{3.15}").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":100,"absoluteTolerance":1}""", @"\boxed{100.7}").Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":100,"relativeTolerance":0,"absoluteTolerance":0}""",
            @"\boxed{100.7}").Passed);
    }

    [Test]
    public void Constraint_ChecksWordsSubstringsAndFormat()
    {
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"minWords":2,"maxWords":4}""", "one two three").Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"minWords":5}""", "one two three").Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"maxWords":2}""", "one two three").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"mustContain":["Paris"]}""", "the capital is paris").Passed,
            "Substring checks are case-insensitive.");
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"mustNotContain":["sorry"]}""", "Sorry, I cannot.").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"format":"json"}""", """{"a":1}""").Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"format":"json"}""", "nope").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"format":"markdownList"}""", "- one\n- two\n").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"format":"markdownList"}""", "1. one\n2. two").Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"format":"markdownList"}""", "one\ntwo").Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"format":"noMarkdown"}""", "Just plain prose.").Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"format":"noMarkdown"}""", "Some **bold** prose.").Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.Constraint, """{"format":"noMarkdown"}""", "# Heading\nprose").Passed);
    }

    [Test]
    public void Verify_ForAnLlmCriterionOrABrokenConfig_ThrowsRatherThanScoringZero()
    {
        // Zero is a real score an answer can earn, so "unmeasurable" must never be represented that way.
        var llm = AssertEx.Throws<BenchmarkExecutionException>(() =>
            BenchmarkJudgeVerifiers.Verify(new BenchmarkJudgeRubricCriterionV1("c0", "T", "D", 10), "anything"));
        var broken = AssertEx.Throws<BenchmarkExecutionException>(() => Verify(BenchmarkJudgeCriterionKinds.Exact, """{"expected":""}""", "x"));

        AssertEx.Contains(llm.Message, "judge model");
        AssertEx.Contains(broken.Message, "cannot be verified");
    }

    [Test]
    public void Verify_IsDeterministic()
    {
        var first = Verify(BenchmarkJudgeCriterionKinds.MathAnswer, Expected(7), @"work work \boxed{7}");
        var second = Verify(BenchmarkJudgeCriterionKinds.MathAnswer, Expected(7), @"work work \boxed{7}");

        AssertEx.Equal(first.Passed, second.Passed);
        AssertEx.Equal(first.Detail, second.Detail);
        AssertEx.Equal(first.Kind, second.Kind);
    }

    private const string SceneAnswer =
        """
        Here is the scene:
        ```json
        {"version":1,"objects":[
          {"id":"floor","primitive":"plane","position":[0,0,0],"scale":[10,1,10]},
          {"id":"ball","primitive":"sphere","position":[0,0.5,0],"color":"#ff0000"},
          {"id":"crate","primitive":"box","position":[-2,0.5,0]},
          {"id":"room","primitive":"box","position":[0,2,0],"scale":[20,4,20]}
        ]}
        ```
        """;

    [Test]
    public void SceneSpec_PassesAFencedValidScene()
    {
        var result = Verify(BenchmarkJudgeCriterionKinds.SceneSpec, "{}", SceneAnswer);

        AssertEx.True(result.Passed, result.Detail);
        AssertEx.Equal("scene: 4 objects", result.Detail);
        AssertEx.Equal(BenchmarkJudgeCriterionKinds.SceneSpec, result.Kind);
    }

    [Test]
    public void SceneSpec_FailsAMalformedSceneWithTheReason()
    {
        var notJson = Verify(BenchmarkJudgeCriterionKinds.SceneSpec, "{}", "```json\n{\"version\":1,\n```");
        var unknown = Verify(BenchmarkJudgeCriterionKinds.SceneSpec, "{}",
            """{"version":1,"objects":[{"id":"a","primitive":"box","position":[0,0,0]},{"id":"b","primitive":"box","position":[0,0,0],"texture":"x.png"}]}""");

        AssertEx.False(notJson.Passed);
        AssertEx.Equal("The answer is not valid JSON.", notJson.Detail);
        AssertEx.False(unknown.Passed);
        AssertEx.Equal("object 2: unknown member 'texture'.", unknown.Detail);
    }

    [Test]
    public void SceneSpec_ChecksObjectCountAndRequiredPrimitives()
    {
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.SceneSpec, """{"objectCount":{"min":4,"max":4}}""", SceneAnswer).Passed);
        AssertEx.Contains(Verify(BenchmarkJudgeCriterionKinds.SceneSpec, """{"objectCount":{"min":5}}""", SceneAnswer).Detail, "fewer than the required 5");
        AssertEx.Contains(Verify(BenchmarkJudgeCriterionKinds.SceneSpec, """{"objectCount":{"max":3}}""", SceneAnswer).Detail, "more than the permitted 3");
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.SceneSpec, """{"requiresPrimitive":["box","sphere","plane"]}""", SceneAnswer).Passed);
        var missing = Verify(BenchmarkJudgeCriterionKinds.SceneSpec, """{"requiresPrimitive":["sphere","cone"]}""", SceneAnswer);
        AssertEx.False(missing.Passed);
        AssertEx.Equal("The scene has no cone.", missing.Detail);
    }

    [Test]
    public void SceneSpec_DecidesEachRelation_AndNamesAnIdTheSceneLacks()
    {
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.SceneSpec, Relation("above", "ball", "floor"), SceneAnswer).Passed);
        AssertEx.Equal("'floor' is not above 'ball'.", Verify(BenchmarkJudgeCriterionKinds.SceneSpec, Relation("above", "floor", "ball"), SceneAnswer).Detail);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.SceneSpec, Relation("leftOf", "crate", "ball"), SceneAnswer).Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.SceneSpec, Relation("leftOf", "ball", "crate"), SceneAnswer).Passed);
        AssertEx.True(Verify(BenchmarkJudgeCriterionKinds.SceneSpec, Relation("inside", "ball", "room"), SceneAnswer).Passed);
        AssertEx.False(Verify(BenchmarkJudgeCriterionKinds.SceneSpec, Relation("inside", "room", "ball"), SceneAnswer).Passed);
        AssertEx.Equal("The relation above names 'lamp', which is not in the scene.",
            Verify(BenchmarkJudgeCriterionKinds.SceneSpec, Relation("above", "lamp", "floor"), SceneAnswer).Detail);
    }

    [Test]
    public void SceneSpec_IsDeterministic()
    {
        var first = Verify(BenchmarkJudgeCriterionKinds.SceneSpec, Relation("above", "floor", "ball"), SceneAnswer);
        var second = Verify(BenchmarkJudgeCriterionKinds.SceneSpec, Relation("above", "floor", "ball"), SceneAnswer);

        AssertEx.Equal(first, second);
    }

    [Test]
    public void SceneSpec_WithABrokenConfig_ThrowsRatherThanScoringZero()
    {
        var broken = AssertEx.Throws<BenchmarkExecutionException>(() =>
            Verify(BenchmarkJudgeCriterionKinds.SceneSpec, """{"objectCount":{"min":3,"max":1}}""", SceneAnswer));

        AssertEx.Contains(broken.Message, "cannot be verified");
    }

    private static string Relation(string kind, string a, string b) =>
        $$"""{"relations":[{"kind":"{{kind}}","a":"{{a}}","b":"{{b}}"}]}""";

    private static string Expected(double value) =>
        $$"""{"expected":{{value.ToString(CultureInfo.InvariantCulture)}}}""";

    private static BenchmarkJudgeVerifierResultV1 Verify(string kind, string config, string answer) =>
        BenchmarkJudgeVerifiers.Verify(new BenchmarkJudgeRubricCriterionV1("c0", "Title", "Description", 10, kind, config), answer);
}
