namespace XE_Local_AI_Engine.Client.Services.Eval.Implementation;

using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Default <see cref="IPlaybookEvalJudge" /> for golden-conversation scoring. The deterministic assertion path scores in plain
///     code (no model call), keeping gate-critical cases stable; the judge path forces a structured JSON verdict from
///     the SUPPLIED node-local client (mirrors <c>OllamaPlaybookAnalysisAgent</c>: cached <see cref="JsonSerializerOptions" />,
///     positional-record DTO, system + user messages, <c>Temperature = 0</c>). Golden text never leaves the node — the
///     service resolves and owns the node-local client and passes it in.
/// </summary>
internal sealed class OllamaPlaybookEvalJudge(ILogger<OllamaPlaybookEvalJudge> logger) : IPlaybookEvalJudge
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

    private readonly ILogger<OllamaPlaybookEvalJudge> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<EvalScore> ScoreAsync(GoldenConversationRecord goldenCase,
        string candidateText,
        IChatClient nodeLocalClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(goldenCase);
        ArgumentNullException.ThrowIfNull(nodeLocalClient);

        // Deterministic path: an assertion scores in plain code, no model call → the gate is deterministic for it.
        if (!string.IsNullOrWhiteSpace(goldenCase.Assertion))
        {
            return new EvalScore(ScoreByAssertion(goldenCase.Assertion, candidateText ?? string.Empty), AssertionScoredBy);
        }

        // Judge path: a rubric is required when there is no assertion. Defend against an invalid case (neither present).
        if (string.IsNullOrWhiteSpace(goldenCase.Rubric))
        {
            _logger.LogWarning("Golden case {GoldenCaseId} has neither assertion nor rubric; scoring as a fail.", goldenCase.Id);
            return new EvalScore(Pass: false, JudgeScoredBy);
        }

        return new EvalScore(await ScoreByJudgeAsync(goldenCase, candidateText ?? string.Empty, nodeLocalClient, cancellationToken).ConfigureAwait(false),
            JudgeScoredBy);
    }

    private bool ScoreByAssertion(string assertionJson, string candidateText)
    {
        Assertion? assertion;
        try
        {
            assertion = JsonSerializer.Deserialize<Assertion>(assertionJson, SerializerOptions);
        }
        catch (JsonException exception)
        {
            // A malformed assertion cannot prove the candidate is good — fail closed rather than wave it through.
            _logger.LogWarning(exception, "Failed to parse golden assertion JSON; scoring the case as a fail.");
            return false;
        }

        if (assertion is null)
        {
            return false;
        }

        // Filter out null/empty entries before the Ordinal checks: an empty phrase is degenerate ("".Contains("") is
        // true), so a stray blank required phrase would always "pass" and a blank forbidden phrase would force-fail any
        // candidate. Ignore them so only meaningful phrases gate the case.
        var requiredPresent = assertion.RequiredPhrases is null
                              || assertion.RequiredPhrases
                                          .Where(static phrase => !string.IsNullOrEmpty(phrase))
                                          .All(phrase => candidateText.Contains(phrase, StringComparison.Ordinal));

        var forbiddenAbsent = assertion.ForbiddenPhrases is null
                              || !assertion.ForbiddenPhrases
                                           .Where(static phrase => !string.IsNullOrEmpty(phrase))
                                           .Any(phrase => candidateText.Contains(phrase, StringComparison.Ordinal));

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

    // Positional records: System.Text.Json binds JSON properties to the constructor parameters by name (Web defaults),
    // and the constructor counts as the assignment so the unassigned-auto-property analyzer stays quiet.
    private sealed record Assertion(List<string>? RequiredPhrases, List<string>? ForbiddenPhrases);

    private sealed record JudgeVerdict(bool Pass, string? Reason);
}
