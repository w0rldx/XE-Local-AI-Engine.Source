namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Default <see cref="IPlaybookEvalJudge" /> for golden-conversation scoring. The deterministic assertion path scores in plain
///     code (no model call), keeping gate-critical cases stable; the judge path forces a structured JSON verdict from
///     the SUPPLIED node-local client (mirrors <c>DefaultPlaybookAnalysisAgent</c>: cached <see cref="JsonSerializerOptions" />,
///     positional-record DTO, system + user messages, <c>Temperature = 0</c>). Golden text never leaves the node — the
///     service resolves and owns the node-local client and passes it in.
/// </summary>
internal sealed class DefaultPlaybookEvalJudge(ILogger<DefaultPlaybookEvalJudge> logger) : IPlaybookEvalJudge
{
    internal const string AssertionScoredBy = "assertion";
    internal const string JudgeScoredBy = "judge";

    private const string JudgeSystemPrompt = """
                                             You judge whether an AI agent's answer satisfies a rubric. You are given a JSON object with a "rubric"
                                             (the criteria the answer must meet) and a "candidateText" (the agent's answer). Decide whether the answer
                                             meets the rubric.

                                             Return ONLY a JSON object of the form: { "pass": boolean, "reason": string }
                                             - "pass" is true only when the candidateText satisfies the rubric.
                                             - "reason" is a short justification.
                                             """;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger<DefaultPlaybookEvalJudge> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<EvalScore> ScoreAsync(GoldenConversationRecord goldenCase,
        string candidateText,
        IChatClient nodeLocalClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goldenCase);
        ArgumentNullException.ThrowIfNull(nodeLocalClient);

        var hasRubric = !string.IsNullOrWhiteSpace(goldenCase.Rubric);

        // Deterministic path: an assertion carrying at least one meaningful (non-blank) required/forbidden phrase scores
        // in plain code, no model call → the gate is deterministic for it.
        if (!string.IsNullOrWhiteSpace(goldenCase.Assertion))
        {
            // An assertion string that is malformed or carries no meaningful signal proves nothing on its own. Treat it
            // as ABSENT and fall through to the rubric when the author supplied one — a rubric-backed case must not be
            // deterministically failed just because its assertion is an empty-phrase placeholder. (Create/update
            // validation already requires a real signal OR a rubric; this reaches the same case for legacy rows.)
            var assertion = GoldenAssertion.TryParse(goldenCase.Assertion);
            if (assertion is { HasMeaningfulSignal: true })
            {
                return new EvalScore(ScoreByAssertion(assertion, candidateText ?? string.Empty), AssertionScoredBy);
            }

            // No usable assertion and no rubric to fall back on → fail closed on the assertion path rather than wave the
            // case through (an empty-phrase assertion would otherwise pass any output).
            if (!hasRubric)
            {
                _logger.LogWarning("Golden case {GoldenCaseId} has an assertion with no meaningful phrase and no rubric; scoring as a fail.", goldenCase.Id);
                return new EvalScore(Pass: false, AssertionScoredBy);
            }
        }

        // Judge path: a rubric scores the case when there is no usable assertion. Defend against an invalid case (neither
        // a meaningful assertion nor a rubric present).
        if (!hasRubric)
        {
            _logger.LogWarning("Golden case {GoldenCaseId} has neither a meaningful assertion nor a rubric; scoring as a fail.", goldenCase.Id);
            return new EvalScore(Pass: false, JudgeScoredBy);
        }

        return new EvalScore(await ScoreByJudgeAsync(goldenCase, candidateText ?? string.Empty, nodeLocalClient, cancellationToken).ConfigureAwait(false),
            JudgeScoredBy);
    }

    // The caller has already parsed the assertion and confirmed HasMeaningfulSignal, so an empty required-check
    // (vacuously true) and an empty forbidden-check (trivially absent) cannot reach here to pass any output.
    private static bool ScoreByAssertion(GoldenAssertion assertion, string candidateText)
    {
        var requiredPresent = assertion.RequiredPhrases.All(phrase => candidateText.Contains(phrase, StringComparison.Ordinal));
        var forbiddenAbsent = !assertion.ForbiddenPhrases.Any(phrase => candidateText.Contains(phrase, StringComparison.Ordinal));

        return requiredPresent && forbiddenAbsent;
    }

    private async Task<bool> ScoreByJudgeAsync(GoldenConversationRecord goldenCase,
        string candidateText,
        IChatClient nodeLocalClient,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, JudgeSystemPrompt),
            new(ChatRole.User, JsonSerializer.Serialize(new
            {
                goldenCase.Rubric,
                CandidateText = candidateText
            }, SerializerOptions))
        ];

        var chatOptions = new ChatOptions
        {
            Temperature = 0f
        };

        var response = await nodeLocalClient
                             .GetResponseAsync<JudgeVerdict>(messages, chatOptions, cancellationToken: cancellationToken)
                             .ConfigureAwait(false);

        if (!response.TryGetResult(out var verdict) || verdict is null)
        {
            _logger.LogWarning("Eval judge returned no parseable verdict for golden case {GoldenCaseId}; scoring as a fail.", goldenCase.Id);
            return false;
        }

        return verdict.Pass;
    }

    // Positional record: System.Text.Json binds JSON properties to the constructor parameters by name (Web defaults),
    // and the constructor counts as the assignment so the unassigned-auto-property analyzer stays quiet.
    private sealed record JudgeVerdict(bool Pass, string? Reason);
}
