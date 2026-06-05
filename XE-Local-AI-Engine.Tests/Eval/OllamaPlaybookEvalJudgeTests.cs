namespace XE_Local_AI_Engine.Tests.Eval;

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Direct unit tests for the judge's DETERMINISTIC assertion scoring path (no model call). The required/forbidden
///     phrase rules are scored in plain code with Ordinal comparison.
/// </summary>
public sealed class OllamaPlaybookEvalJudgeTests
{
    [Test]
    public async Task ScoreAsync_WhenRequiredPhrasePresentAndNoForbidden_PassesViaAssertion()
    {
        var judge = new OllamaPlaybookEvalJudge(NullLogger<OllamaPlaybookEvalJudge>.Instance);
        var goldenCase = AssertionCase(required: ["cite"], forbidden: ["maybe"]);

        var score = await judge.ScoreAsync(goldenCase, "Always cite the source.", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.True(score.Pass, "All required phrases present and no forbidden phrase → pass.");
        AssertEx.Equal("assertion", score.ScoredBy);
    }

    [Test]
    public async Task ScoreAsync_WhenRequiredPhraseMissing_FailsViaAssertion()
    {
        var judge = new OllamaPlaybookEvalJudge(NullLogger<OllamaPlaybookEvalJudge>.Instance);
        var goldenCase = AssertionCase(required: ["cite"], forbidden: []);

        var score = await judge.ScoreAsync(goldenCase, "No citation here.", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.False(score.Pass, "A missing required phrase must fail.");
        AssertEx.Equal("assertion", score.ScoredBy);
    }

    [Test]
    public async Task ScoreAsync_WhenForbiddenPhrasePresent_FailsViaAssertion()
    {
        var judge = new OllamaPlaybookEvalJudge(NullLogger<OllamaPlaybookEvalJudge>.Instance);
        var goldenCase = AssertionCase(required: ["cite"], forbidden: ["maybe"]);

        // Ordinal (case-sensitive) match: the forbidden phrase "maybe" appears verbatim in the candidate output.
        var score = await judge.ScoreAsync(goldenCase, "I will maybe cite the source.", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.False(score.Pass, "A present forbidden phrase must fail even when required phrases are present.");
        AssertEx.Equal("assertion", score.ScoredBy);
    }

    [Test]
    public async Task ScoreAsync_WhenForbiddenPhraseIsEmpty_DoesNotForceFailACleanCandidate()
    {
        var judge = new OllamaPlaybookEvalJudge(NullLogger<OllamaPlaybookEvalJudge>.Instance);
        // An empty forbidden entry is degenerate ("".Contains("") is true) — it must be ignored, not force-fail.
        var goldenCase = AssertionCase(required: ["cite"], forbidden: [""]);

        var score = await judge.ScoreAsync(goldenCase, "Always cite the source.", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.True(score.Pass, "An empty forbidden phrase entry must be ignored, not force-fail a clean candidate.");
        AssertEx.Equal("assertion", score.ScoredBy);
    }

    [Test]
    public async Task ScoreAsync_WhenRequiredPhraseIsEmpty_IsIgnored()
    {
        var judge = new OllamaPlaybookEvalJudge(NullLogger<OllamaPlaybookEvalJudge>.Instance);
        // An empty required entry would always "pass" — it must be ignored so only meaningful phrases gate the case.
        var goldenCase = AssertionCase(required: [""], forbidden: ["maybe"]);

        var score = await judge.ScoreAsync(goldenCase, "Always cite the source.", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.True(score.Pass, "An empty required phrase entry must be ignored; the forbidden phrase is absent so the case passes.");
    }

    [Test]
    public async Task ScoreAsync_WhenNeitherAssertionNorRubric_FailsClosed()
    {
        var judge = new OllamaPlaybookEvalJudge(NullLogger<OllamaPlaybookEvalJudge>.Instance);
        var goldenCase = new GoldenConversationRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            "Invalid case",
            InputTurns: "[]",
            Assertion: null,
            Rubric: null,
            Enabled: true,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);

        var score = await judge.ScoreAsync(goldenCase, "anything", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.False(score.Pass, "A case with neither assertion nor rubric is invalid and must score as a fail.");
    }

    private static GoldenConversationRecord AssertionCase(string[] required, string[] forbidden)
    {
        var assertion = JsonSerializer.Serialize(new
        {
            requiredPhrases = required,
            forbiddenPhrases = forbidden
        });
        return new GoldenConversationRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            "Assertion case",
            InputTurns: """[{"role":"user","text":"hello"}]""",
            Assertion: assertion,
            Rubric: null,
            Enabled: true,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }
}
