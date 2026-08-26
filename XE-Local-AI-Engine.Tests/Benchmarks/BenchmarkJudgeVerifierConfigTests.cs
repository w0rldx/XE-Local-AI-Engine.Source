namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Benchmarks.PythonTests;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The activation-time half of C4: what a policy may carry. The execution half is
///     <see cref="BenchmarkJudgeVerifiersTests" />; both go through the same parser, which is the point.
/// </summary>
public sealed class BenchmarkJudgeVerifierConfigTests
{
    [Test]
    public void Parse_ForLlm_ReturnsNoSpecAndRefusesAConfig()
    {
        AssertEx.Null(BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Llm, configJson: null));
        AssertEx.Null(BenchmarkJudgeVerifierConfig.Parse(kind: null, configJson: null), "An absent kind is the pre-P2 default.");

        var withConfig = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Llm, """{"expected":"x"}"""));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, withConfig.Code);
    }

    [Test]
    public void Parse_ForAnUnknownKind_IsRefusedWithItsOwnCode()
    {
        var unknown = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse("telepathy", "{}"));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionKindUnsupported, unknown.Code);
    }

    [Test]
    public void Parse_ForPythonTests_ValidatesTestCodeExportsTimeoutAndExtract()
    {
        // pythonTests is no longer a reserved name, so the interesting refusals are about its CONFIG. Each one is
        // raised at activation rather than at judging, because an operator who learns an hour into a batch learns it
        // from a failed run instead of from the form they were filling in.
        var missingTests = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.PythonTests, """{"exports":["solve"]}"""));
        var oversized = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.PythonTests,
                JsonSerializer.Serialize(new { testCode = new string('x', BenchmarkPythonTestsHarness.TestCodeMaxChars + 1) })));
        var badExport = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.PythonTests,
                """{"testCode":"assert True","exports":["not an identifier"]}"""));
        var badTimeout = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.PythonTests,
                """{"testCode":"assert True","timeoutSeconds":0}"""));
        var badExtract = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.PythonTests,
                """{"testCode":"assert True","extract":"telepathy"}"""));

        foreach (var refusal in new[] { missingTests, oversized, badExport, badTimeout, badExtract })
        {
            AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, refusal.Code);
        }

        var accepted = AssertEx.NotNull(AssertEx.NotNull(BenchmarkJudgeVerifierConfig.Parse(
            BenchmarkJudgeCriterionKinds.PythonTests,
            """{"testCode":"assert solve(1) == 2","exports":["solve"],"timeoutSeconds":20}""")).PythonTests);

        AssertEx.Equal("assert solve(1) == 2", accepted.TestCode);
        AssertEx.Equal(expected: 20, accepted.TimeoutSeconds);
    }

    [Test]
    public void PythonTests_IsVerifiable_ButNotByThePureVerifier()
    {
        // The split that keeps a routing mistake a failed judging rather than a criterion decided by the wrong code.
        AssertEx.True(BenchmarkJudgeCriterionKinds.IsVerifiable(BenchmarkJudgeCriterionKinds.PythonTests));
        AssertEx.True(BenchmarkJudgeCriterionKinds.IsExecutionVerified(BenchmarkJudgeCriterionKinds.PythonTests));
        AssertEx.False(BenchmarkJudgeCriterionKinds.IsExecutionVerified(BenchmarkJudgeCriterionKinds.Exact));

        _ = AssertEx.Throws<BenchmarkExecutionException>(() => BenchmarkJudgeVerifiers.Verify(
            new BenchmarkJudgeRubricCriterionV1("solution", "Solution", "Runs.", 100,
                BenchmarkJudgeCriterionKinds.PythonTests, """{"testCode":"assert True"}"""),
            "answer"));
    }

    [Test]
    public void Parse_ForAVerifiableKindWithNoConfig_IsRefused()
    {
        foreach (var kind in new[]
                 {
                     BenchmarkJudgeCriterionKinds.Exact, BenchmarkJudgeCriterionKinds.Regex,
                     BenchmarkJudgeCriterionKinds.JsonSchema, BenchmarkJudgeCriterionKinds.MathAnswer,
                     BenchmarkJudgeCriterionKinds.Constraint
                 })
        {
            var missing = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() => BenchmarkJudgeVerifierConfig.Parse(kind, configJson: null));
            AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, missing.Code);
        }
    }

    [Test]
    public void Parse_Regex_RejectsABacktrackingPatternAtActivation()
    {
        // The whole ReDoS surface, closed at the policy boundary: NonBacktracking refuses to COMPILE the constructs
        // that make a match explode, and we surface the refusal rather than falling back to a backtracking engine.
        var lookahead = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Regex, """{"pattern":"(?=a)b","mustMatch":true}"""));
        var backreference = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Regex, """{"pattern":"(a)\\1","mustMatch":true}"""));
        var unbalanced = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Regex, """{"pattern":"(unclosed","mustMatch":true}"""));
        var tooLong = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Regex,
                JsonSerializer.Serialize(new
                {
                    pattern = new string('a', BenchmarkJudgeVerifierConfig.MaximumPatternLength + 1),
                    mustMatch = true
                })));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, lookahead.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, backreference.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, unbalanced.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, tooLong.Code);

        // The classic catastrophic pattern compiles fine under NonBacktracking — it is linear there — so it is
        // ACCEPTED, and BenchmarkJudgeVerifiersTests proves it returns instead of hanging.
        AssertEx.NotNull(BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Regex, """{"pattern":"(a+)+$","mustMatch":true}"""));
    }

    [Test]
    public void Parse_JsonSchema_RefusesAKeywordThisBuildDoesNotEnforce()
    {
        var unsupported = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.JsonSchema,
                """{"schema":{"type":"string","minLength":3}}"""));
        var nested = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.JsonSchema,
                """{"schema":{"type":"object","properties":{"a":{"type":"number","multipleOf":2}}}}"""));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, unsupported.Code);
        AssertEx.Contains(unsupported.Message, "minLength");
        AssertEx.Contains(nested.Message, "multipleOf");
        AssertEx.NotNull(BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.JsonSchema,
            """{"schema":{"type":"object","additionalProperties":false,"required":["a"],"properties":{"a":{"enum":["x","y"]}}}}"""));
    }

    [Test]
    public void Parse_MathAnswer_TakesANumberAFractionOrANumericString()
    {
        AssertEx.Equal(0.5, BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":0.5}""")!.ExpectedNumber);
        AssertEx.Equal(0.5, BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":"1/2"}""")!.ExpectedNumber);
        AssertEx.Equal(1234.0, BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":"1,234"}""")!.ExpectedNumber);

        var textual = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":"forty two"}"""));
        var negativeTolerance = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.MathAnswer, """{"expected":1,"relativeTolerance":-1}"""));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, textual.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, negativeTolerance.Code);
    }

    [Test]
    public void Parse_Constraint_RequiresAtLeastOneConstraintAndConsistentBounds()
    {
        var empty = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Constraint, "{}"));
        var inverted = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Constraint, """{"minWords":10,"maxWords":5}"""));
        var badFormat = AssertEx.Throws<BenchmarkJudgePolicyValidationException>(() =>
            BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Constraint, """{"format":"yaml"}"""));

        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, empty.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, inverted.Code);
        AssertEx.Equal(BenchmarkJudgePolicyValidationCodes.CriterionConfigInvalid, badFormat.Code);
        AssertEx.NotNull(BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.Constraint,
            """{"minWords":5,"maxWords":50,"mustContain":["ok"],"format":"noMarkdown"}"""));
    }

    [Test]
    public void Canonicalize_OrdersKeysSoTwoTypingsOfOneConfigHashAlike()
    {
        var forward = BenchmarkJudgeVerifierConfig.Canonicalize("""{"expected":"4","normalize":{"trim":true,"caseInsensitive":false}}""");
        var reversed = BenchmarkJudgeVerifierConfig.Canonicalize("""{ "normalize" : { "caseInsensitive" : false , "trim" : true } , "expected" : "4" }""");

        AssertEx.Equal(AssertEx.NotNull(forward), reversed);
        AssertEx.Null(BenchmarkJudgeVerifierConfig.Canonicalize(null));
    }
}
