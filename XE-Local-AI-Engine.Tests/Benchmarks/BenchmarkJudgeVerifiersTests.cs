namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Diagnostics;
using System.Globalization;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The execution half of C4. Table-driven per kind, with the adversarial inputs a verifier meets in the wild:
///     a catastrophic pattern, an oversized answer, unicode, and a boxed fraction that must equal its decimal.
/// </summary>
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
        // The classic (a+)+$ against a long non-matching input is the ReDoS demo. NonBacktracking runs it in time
        // linear in the input, so this is a fast assertion rather than a hang — which is the whole reason the policy
        // boundary refuses anything NonBacktracking cannot compile.
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
        // R5: 0 is a real score an answer can earn, so "unmeasurable" must never be spelled that way.
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

    private static string Expected(double value) =>
        $$"""{"expected":{{value.ToString(CultureInfo.InvariantCulture)}}}""";

    private static BenchmarkJudgeVerifierResultV1 Verify(string kind, string config, string answer) =>
        BenchmarkJudgeVerifiers.Verify(new BenchmarkJudgeRubricCriterionV1("c0", "Title", "Description", 10, kind, config), answer);
}
