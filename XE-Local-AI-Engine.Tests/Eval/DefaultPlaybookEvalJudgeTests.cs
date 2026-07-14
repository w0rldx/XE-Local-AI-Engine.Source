namespace XE_Local_AI_Engine.Tests.Eval;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Direct unit tests for the judge's DETERMINISTIC assertion scoring path (no model call). The required/forbidden
///     phrase rules are scored in plain code with Ordinal comparison.
/// </summary>
public sealed class DefaultPlaybookEvalJudgeTests
{
    [Test]
    public async Task ScoreAsync_WhenRequiredPhrasePresentAndNoForbidden_PassesViaAssertion()
    {
        var judge = new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance);
        var goldenCase = AssertionCase(["cite"], ["maybe"]);

        var score = await judge.ScoreAsync(goldenCase, "Always cite the source.", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.True(score.Pass, "All required phrases present and no forbidden phrase → pass.");
        AssertEx.Equal("assertion", score.ScoredBy);
    }

    [Test]
    public async Task ScoreAsync_WhenRequiredPhraseMissing_FailsViaAssertion()
    {
        var judge = new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance);
        var goldenCase = AssertionCase(["cite"], []);

        var score = await judge.ScoreAsync(goldenCase, "No citation here.", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.False(score.Pass, "A missing required phrase must fail.");
        AssertEx.Equal("assertion", score.ScoredBy);
    }

    [Test]
    public async Task ScoreAsync_WhenForbiddenPhrasePresent_FailsViaAssertion()
    {
        var judge = new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance);
        var goldenCase = AssertionCase(["cite"], ["maybe"]);

        // Ordinal (case-sensitive) match: the forbidden phrase "maybe" appears verbatim in the candidate output.
        var score = await judge.ScoreAsync(goldenCase, "I will maybe cite the source.", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.False(score.Pass, "A present forbidden phrase must fail even when required phrases are present.");
        AssertEx.Equal("assertion", score.ScoredBy);
    }

    [Test]
    public async Task ScoreAsync_WhenForbiddenPhraseIsEmpty_DoesNotForceFailACleanCandidate()
    {
        var judge = new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance);
        // An empty forbidden entry is degenerate ("".Contains("") is true) — it must be ignored, not force-fail.
        var goldenCase = AssertionCase(["cite"], [""]);

        var score = await judge.ScoreAsync(goldenCase, "Always cite the source.", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.True(score.Pass, "An empty forbidden phrase entry must be ignored, not force-fail a clean candidate.");
        AssertEx.Equal("assertion", score.ScoredBy);
    }

    [Test]
    public async Task ScoreAsync_WhenRequiredPhraseIsEmpty_IsIgnored()
    {
        var judge = new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance);
        // An empty required entry would always "pass" — it must be ignored so only meaningful phrases gate the case.
        var goldenCase = AssertionCase([""], ["maybe"]);

        var score = await judge.ScoreAsync(goldenCase, "Always cite the source.", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.True(score.Pass, "An empty required phrase entry must be ignored; the forbidden phrase is absent so the case passes.");
    }

    [Test]
    public async Task ScoreAsync_WhenNeitherAssertionNorRubric_FailsClosed()
    {
        var judge = new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance);
        var goldenCase = new GoldenConversationRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            "Invalid case",
            "[]",
            Assertion: null,
            Rubric: null,
            Enabled: true,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);

        var score = await judge.ScoreAsync(goldenCase, "anything", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.False(score.Pass, "A case with neither assertion nor rubric is invalid and must score as a fail.");
    }

    [Test]
    public async Task ScoreAsync_WhenAssertionHasNoMeaningfulPhrase_FailsClosed()
    {
        var judge = new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance);
        // Both arrays empty (or all-blank) → the assertion gates nothing and would otherwise pass ANY output. It must
        // fail closed instead — this closes the empty-array bypass for legacy golden rows.
        var goldenCase = AssertionCase([], []);

        var score = await judge.ScoreAsync(goldenCase, "literally anything", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.False(score.Pass, "An assertion with no meaningful phrase proves nothing and must not auto-pass.");
        AssertEx.Equal("assertion", score.ScoredBy);
    }

    [Test]
    public async Task ScoreAsync_WhenAssertionHasNoMeaningfulPhraseButRubricPresent_ScoresByRubric()
    {
        var judge = new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance);
        // An empty-phrase assertion carries no deterministic signal, but the author backed the case with a rubric
        // (create/update validation explicitly allows this). The judge must treat the empty assertion as ABSENT and
        // score via the rubric (model) path — not deterministically fail on the assertion path, which would make the
        // author-valid rubric case impossible to pass.
        var goldenCase = new GoldenConversationRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            "Empty assertion with rubric",
            InputTurns: """[{"role":"user","text":"hello"}]""",
            Assertion: """{"requiredPhrases":[],"forbiddenPhrases":[]}""",
            Rubric: "The answer must be helpful.",
            Enabled: true,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
        using var chatClient = new VerdictChatClient("""{"pass":true,"reason":"meets the rubric"}""");

        var score = await judge.ScoreAsync(goldenCase, "A genuinely helpful answer.", chatClient).ConfigureAwait(false);

        AssertEx.True(score.Pass, "The empty assertion is treated as absent; the rubric (judge) path scores the case.");
        AssertEx.Equal("judge", score.ScoredBy);
        AssertEx.True(chatClient.WasCalled, "The rubric path must invoke the model, not short-circuit to a deterministic fail.");
    }

    [Test]
    public async Task ScoreAsync_WhenAssertionIsMalformedAndRubricPresent_FailsClosedWithoutRubricFallback()
    {
        var judge = new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance);
        // A non-blank assertion string that is not valid JSON — a corrupt/legacy stored scoring constraint. A rubric is
        // present, but a malformed assertion must FAIL the case outright, never silently fall back to the rubric (which
        // would drop the intended deterministic gate). This differs from an empty-phrase assertion, which IS absent.
        var goldenCase = new GoldenConversationRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            "Malformed assertion with rubric",
            InputTurns: """[{"role":"user","text":"hello"}]""",
            Assertion: "{ not valid json",
            Rubric: "The answer must be helpful.",
            Enabled: true,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
        // If the judge wrongly fell back to the rubric, this client would be invoked and pass the case.
        using var chatClient = new VerdictChatClient("""{"pass":true,"reason":"meets the rubric"}""");

        var score = await judge.ScoreAsync(goldenCase, "A genuinely helpful answer.", chatClient).ConfigureAwait(false);

        AssertEx.False(score.Pass, "A malformed assertion must fail the case outright, never silently pass on the rubric.");
        AssertEx.Equal(DefaultPlaybookEvalJudge.MalformedAssertionScoredBy, score.ScoredBy);
        AssertEx.False(chatClient.WasCalled, "A malformed assertion must NOT trigger a rubric (model) call.");
    }

    [Test]
    public async Task ScoreAsync_WhenAssertionIsMalformedAndNoRubric_FailsClosed()
    {
        var judge = new DefaultPlaybookEvalJudge(NullLogger<DefaultPlaybookEvalJudge>.Instance);
        var goldenCase = new GoldenConversationRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            "Malformed assertion, no rubric",
            InputTurns: """[{"role":"user","text":"hello"}]""",
            Assertion: "{ not valid json",
            Rubric: null,
            Enabled: true,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);

        var score = await judge.ScoreAsync(goldenCase, "anything", Substitute.For<IChatClient>()).ConfigureAwait(false);

        AssertEx.False(score.Pass, "A malformed assertion with no rubric must fail closed.");
        AssertEx.Equal(DefaultPlaybookEvalJudge.MalformedAssertionScoredBy, score.ScoredBy);
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
            assertion,
            Rubric: null,
            Enabled: true,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 10);
    }

    /// <summary>
    ///     Minimal node-local <see cref="IChatClient" /> stand-in returning a fixed JSON verdict so the judge's
    ///     <c>GetResponseAsync&lt;JudgeVerdict&gt;</c> parses a structured result without a live model.
    /// </summary>
    private sealed class VerdictChatClient(string verdictJson) : IChatClient
    {
        public bool WasCalled { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, verdictJson)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            // The judge uses the non-streaming GetResponseAsync; an empty stream suffices.
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
