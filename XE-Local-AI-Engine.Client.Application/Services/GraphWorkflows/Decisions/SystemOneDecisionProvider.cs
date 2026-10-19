namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows.Decisions;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The <c>systemone</c> decision provider: asks a native decision model (Julia-1, Laya, Kev-4B) through
///     llama-server's <c>/v1/systemone</c>, one question named <c>q</c> per request.
/// </summary>
/// <remarks>
///     It reports what the model said and how a failed call failed; the executor judges the answer against the node.
///     <see cref="DecisionResult.Choice" /> is a routing convenience for <c>noul</c> (<c>yes</c> at 0.5 or above) and
///     <c>score</c> (the nearest level's label); <c>noul</c> and <c>score</c> are the truth.
/// </remarks>
internal sealed class SystemOneDecisionProvider : IGraphWorkflowNativeDecisionProvider
{
    public const string ProviderName = "systemone";

    private readonly ILlamaServerSystemOneClient _client;

    public SystemOneDecisionProvider(ILlamaServerSystemOneClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Name => ProviderName;

    public async Task<DecisionResult> DecideAsync(NativeDecisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var config = request.Config;
        var result = await _client.DecideAsync(new LlamaServerSystemOneRequest
            {
                ModelName = request.ModelName,
                State = request.State,
                QuestionType = config.QuestionType switch
                {
                    GraphWorkflowGraph.DecisionNoul => LlamaServerSystemOneQuestionType.Noul,
                    GraphWorkflowGraph.DecisionScore => LlamaServerSystemOneQuestionType.Score,
                    _ => LlamaServerSystemOneQuestionType.Choice
                },
                Instructions = config.Question,
                Labels = config.Labels,
                Criteria = config.Criteria
            },
            cancellationToken);

        if (result is not { Outcome: LlamaServerSystemOneOutcome.Answered, Answer: { } answer })
        {
            return Failed(result, request.ModelName);
        }

        return new DecisionResult
        {
            Type = answer.Type,
            Choice = answer.Type switch
            {
                GraphWorkflowGraph.DecisionNoul => YesOrNo(answer.Noul),
                GraphWorkflowGraph.DecisionScore => NearestLevel(answer.Score, config.Labels),
                _ => answer.Choice
            },
            Noul = answer.Noul,
            Score = answer.Score,
            Legend = answer.Legend,
            Probabilities = answer.Probabilities,
            Confidence = answer.Confidence,
            InputTokens = result.InputTokens,
            DurationMs = result.DurationMs
        };
    }

    /// <summary><c>yes</c> at 0.5 or above, <c>no</c> below; null for a missing or non-finite likelihood.</summary>
    internal static string? YesOrNo(double? noul)
    {
        if (noul is not { } value || !double.IsFinite(value))
        {
            return null;
        }

        return value >= 0.5 ? "yes" : "no";
    }

    /// <summary>The level a continuous score is closest to; null for a missing or non-finite score.</summary>
    internal static string? NearestLevel(double? score, IReadOnlyList<string> levels)
    {
        if (score is not { } value || !double.IsFinite(value) || levels.Count == 0)
        {
            return null;
        }

        var index = (int)Math.Round(Math.Clamp(value, 0, levels.Count - 1), MidpointRounding.AwayFromZero);
        return levels[index];
    }

    /// <summary>A failed call as the class the run should record; the server's own text stays in the client's log.</summary>
    private static DecisionResult Failed(LlamaServerSystemOneResult result, string modelName)
    {
        return new DecisionResult
        {
            Choice = null,
            Confidence = null,
            Probabilities = null,
            FailureClass = result.Outcome switch
            {
                // A refused request (an input longer than one batch) is refused again on every retry, so it must not be retried.
                LlamaServerSystemOneOutcome.NotDecisionModel or LlamaServerSystemOneOutcome.InvalidRequest => GraphWorkflowFailureClass.ValidationFailed,
                LlamaServerSystemOneOutcome.Timeout => GraphWorkflowFailureClass.Timeout,
                _ => GraphWorkflowFailureClass.NodeFailed
            },
            FailureReason = result.Outcome switch
            {
                LlamaServerSystemOneOutcome.NotDecisionModel => $"The model '{modelName}' is not a decision model: llama-server refused the systemone request (HTTP 501).",
                LlamaServerSystemOneOutcome.Timeout => "The decision model ran out of time before it answered. See the node logs for details.",
                LlamaServerSystemOneOutcome.InvalidRequest =>
                    "The decision model refused the request, for example an input longer than its context window (one batch). See the node logs for details.",
                LlamaServerSystemOneOutcome.Unavailable => $"The decision model '{modelName}' could not be started. See the node logs for details.",
                LlamaServerSystemOneOutcome.Evicting => $"The decision model '{modelName}' is being unloaded by an operator.",
                LlamaServerSystemOneOutcome.Busy => $"The decision model '{modelName}' is busy with a benchmark or profiling run.",
                _ => "The decision model did not answer. See the node logs for details."
            },
            DurationMs = result.DurationMs
        };
    }
}
