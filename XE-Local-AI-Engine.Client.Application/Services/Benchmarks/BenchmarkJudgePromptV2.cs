namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

public static class BenchmarkJudgePromptV2
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    /// <summary>The instruction every judging gets.</summary>
    /// <remarks>
    ///     The length-neutrality sentence is fixed and unconditional: rewarding a longer answer for being longer is an
    ///     LLM judge's best-documented bias. THE RULE THIS TEXT LIVES UNDER: it is hashed nowhere, so any wording
    ///     change MUST bump <see cref="BenchmarkJudgePolicyVersions.PromptVersion" /> — otherwise verdicts taken
    ///     either side of the edit share a policy revision and a cohort generation and are dense-ranked against each
    ///     other after having been asked different questions. See docs/wiki/20-benchmarks.md ("The pointwise judge").
    /// </remarks>
    public const string SystemPrompt =
        "Evaluate only the supplied benchmark task and primary output against the supplied rubric. "
        + "Score every rubric criterion from 0 to 10 and give a short rationale for each score. "
        + "Do not reward length or verbosity for its own sake; score only against the rubric. "
        + "Return exactly one JSON object matching the supplied output schema. Return no markdown and no extra properties.";

    /// <summary>The extra instruction a truncated primary output gets.</summary>
    /// <remarks>
    ///     Appended rather than folded into <see cref="SystemPrompt" /> so a judging of a COMPLETE answer stays
    ///     byte-identical to every judging that came before: neither the prompt text nor the payload is part of the
    ///     policy hash or the <c>JudgeExecutionKey</c>, so an unconditional sentence would silently change what past
    ///     and future judgings were asked without any version moving.
    /// </remarks>
    public const string TruncatedPrimaryOutputInstruction =
        " The primary output was cut off by the token budget before the model finished answering. "
        + "Score it as the incomplete answer it is; do not credit work the model did not produce.";

    /// <summary>
    ///     The extra instruction a primary output that answered NOTHING gets — it stopped on an unanswered tool call,
    ///     or emitted only reasoning.
    /// </summary>
    /// <remarks>
    ///     Appended on the same terms as <see cref="TruncatedPrimaryOutputInstruction" /> and for the same reason: a
    ///     run in this state is otherwise graded as if its empty transcript were an answer, and every judging that
    ///     came before must stay byte-identical.
    /// </remarks>
    public const string IncompletePrimaryOutputInstruction =
        " The model produced no answer at all: the turn ended on an unanswered tool call, or emitted only reasoning. "
        + "Score the absence of an answer; do not credit work the model did not produce.";

    /// <summary>
    ///     The system prompt for one judging: the base instruction, plus the notice for a primary output that was cut
    ///     off or never came.
    /// </summary>
    /// <remarks>
    ///     The two are mutually exclusive by construction — a run is recorded as <c>incomplete</c> only when it did
    ///     NOT stop at the token budget — and truncation wins if a caller ever passes both.
    /// </remarks>
    public static string SystemPromptFor(bool primaryOutputTruncated, bool primaryOutputIncomplete = false)
    {
        if (primaryOutputTruncated)
        {
            return SystemPrompt + TruncatedPrimaryOutputInstruction;
        }

        return primaryOutputIncomplete ? SystemPrompt + IncompletePrimaryOutputInstruction : SystemPrompt;
    }

    /// <summary>
    ///     The rubric as the judge model sees it: the four members it has always seen, with the server-side
    ///     <c>kind</c>/<c>config</c> members removed.
    /// </summary>
    /// <remarks>
    ///     The model is never handed a verifiable criterion (an all-verifiable rubric never reaches inference, a mixed
    ///     one is filtered first), so those two members carry nothing it could use — and emitting them would change
    ///     the payload bytes of every judging under a legacy revision stored without those fields, which is exactly
    ///     the drift the prompt-version rule exists to prevent.
    /// </remarks>
    private static JsonNode? SerializeRubricForPrompt(BenchmarkJudgeRubricV1 rubric)
    {
        var node = JsonSerializer.SerializeToNode(rubric, PayloadOptions);
        if (node?["criteria"] is not JsonArray criteria)
        {
            return node;
        }

        foreach (var criterion in criteria.OfType<JsonObject>())
        {
            _ = criterion.Remove("kind");
            _ = criterion.Remove("config");
        }

        return node;
    }

    /// <summary>Embeds the caller's already-shaped pieces verbatim — this builder frames, it never re-serializes.</summary>
    /// <remarks>
    ///     <paramref name="primaryOutputPartsJson" /> must be the GRADED projection of the run's transcript
    ///     (<see cref="BenchmarkOutputParts.ForJudge" />): coalesced, reasoning parts removed, because hidden
    ///     chain-of-thought is not the graded answer and a thinking model's raw transcript does not fit the judge
    ///     window. That narrowing leaves the payload's property names and order unchanged, so neither
    ///     <see cref="BenchmarkJudgePolicyVersions.PromptVersion" /> nor the output-schema version moves.
    /// </remarks>
    /// <param name="primaryOutputTruncated">
    ///     Emits <c>primaryOutputTruncated: true</c> after the output parts, ONLY when true, so a complete run's
    ///     payload stays byte-identical.
    /// </param>
    /// <param name="primaryOutputIncomplete">
    ///     Emits <c>primaryOutputIncomplete: true</c> on the same terms, for a clean run that answered nothing.
    /// </param>
    public static string BuildUserPayloadJson(string taskJson,
        string? referenceAnswer,
        BenchmarkJudgeRubricV1 rubric,
        string primaryOutputPartsJson,
        string outputSchemaJson,
        bool primaryOutputTruncated = false,
        bool primaryOutputIncomplete = false)
    {
        ArgumentNullException.ThrowIfNull(rubric);
        var payload = new JsonObject
        {
            ["task"] = JsonNode.Parse(taskJson)
        };
        if (referenceAnswer is not null)
        {
            payload["referenceAnswer"] = JsonValue.Create(referenceAnswer);
        }

        payload["rubric"] = SerializeRubricForPrompt(rubric);
        payload["primaryOutputParts"] = JsonNode.Parse(primaryOutputPartsJson);
        if (primaryOutputTruncated)
        {
            payload["primaryOutputTruncated"] = JsonValue.Create(value: true);
        }
        else if (primaryOutputIncomplete)
        {
            payload["primaryOutputIncomplete"] = JsonValue.Create(value: true);
        }

        payload["outputSchema"] = JsonNode.Parse(outputSchemaJson);
        return payload.ToJsonString(PayloadOptions);
    }
}
