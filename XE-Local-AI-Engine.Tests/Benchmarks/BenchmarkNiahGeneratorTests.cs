namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The haystack generator. Everything here is a property of a PURE function of (parent id, configuration,
///     corpus): a case that generated differently on a second machine would carry a different input hash for the same
///     configuration, and every answer ever given to it would read as an answer to a question that no longer exists.
/// </summary>
public sealed class BenchmarkNiahGeneratorTests
{
    private static readonly Guid ParentId = new("aaaaaaaa-1111-2222-3333-444444444444");

    private static BenchmarkNiahConfigV1 Config(int[] sizes, int[] depths, int seed = 0) =>
        new(sizes, depths, Seed: seed);

    [Test]
    public void Expand_ProducesOneCasePerSizeDepthPair()
    {
        var cases = BenchmarkNiahGenerator.Expand(ParentId, Config([2048, 4096], [10, 50, 90]), projectContextTokens: 8192);

        AssertEx.Equal(6, cases.Count, "Two lengths and three depths are six probes, and each one is its own leaf item.");
        AssertEx.Equal(6, cases.Select(entry => (entry.Case.ContextTokens, entry.Case.DepthPercent)).Distinct().Count());
        AssertEx.Equal(6, cases.Select(entry => entry.ExpectedAnswer).Distinct(StringComparer.Ordinal).Count(),
            "Each case hides its own passcode, or one case's answer would score another case's run.");
    }

    /// <summary>
    ///     The determinism the whole design rests on. The same inputs must produce the same prompt BYTES, because the
    ///     bytes are what the item's input hash is taken over.
    /// </summary>
    [Test]
    public void Expand_IsDeterministicUnderTheSameSeed()
    {
        var first = BenchmarkNiahGenerator.Expand(ParentId, Config([2048], [50], seed: 7), projectContextTokens: 8192);
        var second = BenchmarkNiahGenerator.Expand(ParentId, Config([2048], [50], seed: 7), projectContextTokens: 8192);

        AssertEx.Equal(first[0].Prompt, second[0].Prompt, "The same parent, configuration and corpus produce the same probe.");
        AssertEx.Equal(first[0].ExpectedAnswer, second[0].ExpectedAnswer);
    }

    [Test]
    public void Expand_DifferentSeedsAndParentsDrawDifferentText()
    {
        var baseline = BenchmarkNiahGenerator.Expand(ParentId, Config([2048], [50], seed: 7), projectContextTokens: 8192)[0];
        var otherSeed = BenchmarkNiahGenerator.Expand(ParentId, Config([2048], [50], seed: 8), projectContextTokens: 8192)[0];
        var otherParent = BenchmarkNiahGenerator.Expand(Guid.NewGuid(), Config([2048], [50], seed: 7), projectContextTokens: 8192)[0];

        AssertEx.NotEqual(baseline.Prompt, otherSeed.Prompt, "The operator's seed varies the haystack.");
        AssertEx.NotEqual(baseline.Prompt, otherParent.Prompt, "So does the probe's own identity, so two probes of one project differ.");
    }

    /// <summary>
    ///     Depth is a position in the TEXT, not an index into a sentence list: the sentences are of wildly different
    ///     lengths, so "the 50th of 100 sentences" and "halfway through" are not the same place.
    /// </summary>
    [Test]
    [Arguments(10)]
    [Arguments(50)]
    [Arguments(90)]
    public void Expand_PlacesTheNeedleAtTheRequestedDepth(int depth)
    {
        var generated = BenchmarkNiahGenerator.Expand(ParentId, Config([4096], [depth]), projectContextTokens: 8192)[0];
        var document = Document(generated.Prompt);
        var needleAt = document.IndexOf(generated.ExpectedAnswer, StringComparison.Ordinal);

        AssertEx.True(needleAt >= 0, "The needle is in the document the model is shown.");
        var actualDepth = 100.0 * needleAt / document.Length;
        AssertEx.True(Math.Abs(actualDepth - depth) <= 5.0,
            $"The needle sits at {actualDepth:F1}% of the document, which is not within 5 points of the requested {depth}%.");
    }

    /// <summary>
    ///     R-6. The token count is an approximation that under-counts prose, so the generator aims BELOW the requested
    ///     length rather than at it — and the label says so, because a probe that silently ran at 26k instead of 32k
    ///     is worse than one labelled approximate.
    /// </summary>
    [Test]
    public void Expand_TargetsBelowTheRequestedLengthAndLabelsTheProbeApproximate()
    {
        var generated = BenchmarkNiahGenerator.Expand(ParentId, Config([8192], [50]), projectContextTokens: 16384)[0];

        AssertEx.Equal(8192, generated.Case.ContextTokens, "The REQUESTED length is what the refusal compares, so it is stored as requested.");
        AssertEx.True(generated.Case.ApproximateTokens <= 8192,
            $"The probe estimates to {generated.Case.ApproximateTokens} tokens, which is above the {8192} it asked for.");
        AssertEx.True(generated.Case.ApproximateTokens >= (int)(8192 * 0.80),
            $"The probe estimates to {generated.Case.ApproximateTokens} tokens, far short of the 90% the generator targets.");
        AssertEx.Contains(generated.Case.Label, "≈8k", message: "The label hedges the length it could not measure exactly.");
        AssertEx.Contains(generated.Case.Label, "50%");
    }

    /// <summary>Refused at expansion, while the operator is still looking at the form — with both numbers named.</summary>
    [Test]
    public void Expand_RefusesAProbeLongerThanTheProjectWindow()
    {
        var failure = AssertEx.Throws<BenchmarkValidationException>(
            () => BenchmarkNiahGenerator.Expand(ParentId, Config([32768], [50]), projectContextTokens: 8192));

        AssertEx.Contains(failure.Message, "32768", message: "The refusal names what was asked for.");
        AssertEx.Contains(failure.Message, "8192", message: "And what the project can hold.");
    }

    [Test]
    public void Expand_RefusesAProbeShorterThanTheFloor()
    {
        var failure = AssertEx.Throws<BenchmarkValidationException>(
            () => BenchmarkNiahGenerator.Expand(ParentId, Config([256], [50]), projectContextTokens: 8192));

        AssertEx.Contains(failure.Message, "512");
    }

    [Test]
    public void Expand_RefusesMoreCasesThanTheMaximum()
    {
        var sizes = Enumerable.Range(1, 7).Select(static step => 1024 * step).ToArray();
        var failure = AssertEx.Throws<BenchmarkValidationException>(
            () => BenchmarkNiahGenerator.Expand(ParentId, Config(sizes, [10, 50, 90]), projectContextTokens: 65536));

        AssertEx.Contains(failure.Message, "21", message: "The refusal names the count it computed, not just the cap.");
    }

    [Test]
    public void Expand_RefusesADepthOutsideTheRange()
    {
        _ = AssertEx.Throws<BenchmarkValidationException>(
            () => BenchmarkNiahGenerator.Expand(ParentId, Config([2048], [140]), projectContextTokens: 8192));
    }

    [Test]
    public void Expand_RefusesATemplateWithoutItsPlaceholders()
    {
        _ = AssertEx.Throws<BenchmarkValidationException>(() => BenchmarkNiahGenerator.Expand(ParentId,
            new BenchmarkNiahConfigV1([2048], [50], NeedleTemplate: "The passcode is {code}."),
            projectContextTokens: 8192));
    }

    /// <summary>
    ///     The generator and the verifier have to agree, or a probe measures nothing: the case hides a passcode, and
    ///     the exact criterion it writes onto itself is what recovers it. Neither half is useful without the other.
    /// </summary>
    [Test]
    [Arguments(10)]
    [Arguments(50)]
    [Arguments(90)]
    public void Expand_TheNeedleIsRecoverableByTheExactCriterionTheCaseCarries(int depth)
    {
        var generated = BenchmarkNiahGenerator.Expand(ParentId, Config([2048], [depth]), projectContextTokens: 8192)[0];
        var criterion = ExactCriterionFrom(generated.ExpectedAnswer);

        var recalled = BenchmarkJudgeVerifiers.Verify(criterion, new string([.. generated.ExpectedAnswer.Select(char.ToLowerInvariant)]));
        var missed = BenchmarkJudgeVerifiers.Verify(criterion, "I could not find a passcode.");

        AssertEx.True(recalled.Passed, "The passcode the haystack hides is the passcode the criterion expects, case aside.");
        AssertEx.False(missed.Passed, "And anything else is a miss, with no judge model consulted either way.");
        AssertEx.Equal(BenchmarkJudgeCriterionKinds.Exact, recalled.Kind);
    }

    /// <summary>The probe must not be gradeable by pattern-matching the question — the answer appears exactly once.</summary>
    [Test]
    public void Expand_ThePasscodeAppearsOnlyInTheNeedle()
    {
        var generated = BenchmarkNiahGenerator.Expand(ParentId, Config([4096], [50]), projectContextTokens: 8192)[0];
        var occurrences = generated.Prompt.Split(generated.ExpectedAnswer, StringSplitOptions.None).Length - 1;

        AssertEx.Equal(1, occurrences, "The passcode is stated once, in the needle, and nowhere else in the prompt.");
    }

    [Test]
    public void Expand_AttributesTheCorpusItDrewFrom()
    {
        var generated = BenchmarkNiahGenerator.Expand(ParentId, Config([2048], [50]), projectContextTokens: 8192)[0];

        AssertEx.Contains(generated.Prompt, "CC BY-SA 3.0", message: "The haystack is shipped third-party text and says so.");
        AssertEx.Contains(generated.Case.Corpus, "wikitext2-raw-test@");
    }

    /// <summary>
    ///     A case describes itself, so the freeze can re-check its length against the project window without parsing a
    ///     haystack back out of a prompt.
    /// </summary>
    [Test]
    public void TryRead_RecoversACasesParametersFromTheItemThatCarriesThem()
    {
        var generated = BenchmarkNiahGenerator.Expand(ParentId, Config([2048], [50]), projectContextTokens: 8192)[0];
        var item = ItemCarrying(BenchmarkTaskItemKinds.NiahCase, JsonSerializer.SerializeToUtf8Bytes(generated.Case, BenchmarkNiahGenerator.SerializerOptions));

        var read = AssertEx.NotNull(BenchmarkNiahCase.TryRead(item));

        AssertEx.Equal(2048, read.ContextTokens);
        AssertEx.Equal(50, read.DepthPercent);
        AssertEx.Null(BenchmarkNiahCase.TryRead(ItemCarrying(BenchmarkTaskItemKinds.Prompt, null)), "An authored prompt is not a probe case.");
    }

    /// <summary>Fail closed: a case nothing can vouch for must not slip past the length check by being unreadable.</summary>
    [Test]
    public void TryRead_RefusesACaseWhoseParametersCannotBeRead()
    {
        _ = AssertEx.Throws<BenchmarkValidationException>(() => BenchmarkNiahCase.TryRead(ItemCarrying(BenchmarkTaskItemKinds.NiahCase, null)));
    }

    private static BenchmarkJudgeRubricCriterionV1 ExactCriterionFrom(string expectedAnswer)
    {
        var config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            BenchmarkNiahGenerator.VerifierConfigJson(BenchmarkNiahGenerator.DefaultCriterionId, expectedAnswer))!;
        return new BenchmarkJudgeRubricCriterionV1(BenchmarkNiahGenerator.DefaultCriterionId,
            "Recall",
            "Did the model find the needle?",
            Weight: 1,
            BenchmarkJudgeCriterionKinds.Exact,
            config[BenchmarkNiahGenerator.DefaultCriterionId].GetRawText());
    }

    private static BenchmarkTaskItemRecord ItemCarrying(string kind, byte[]? generatorConfigJson) =>
        new(Guid.NewGuid(), Guid.NewGuid(), ParentId, Index: 1, kind, Revision: 1, "v1:hash", CountsTowardScore: false,
            PromptJson: "{}"u8.ToArray(), ReferenceAnswerJson: null, VerifierConfigJson: null,
            GeneratorConfigJson: generatorConfigJson, Version: 1, CreatedAtUtc: 0, UpdatedAtUtc: 0);

    /// <summary>The haystack as the model sees it, without the framing the depth assertion is not about.</summary>
    private static string Document(string prompt)
    {
        var start = prompt.IndexOf("<document>", StringComparison.Ordinal) + "<document>".Length;
        var end = prompt.IndexOf("</document>", StringComparison.Ordinal);
        return prompt[start..end];
    }
}
