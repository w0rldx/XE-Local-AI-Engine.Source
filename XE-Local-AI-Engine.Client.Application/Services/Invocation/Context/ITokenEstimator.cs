namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using Microsoft.Extensions.AI;

/// <summary>
///     Estimates the token footprint of chat messages for input-context budgeting. Deliberately an abstraction so the
///     conservative character-heuristic default (<see cref="HeuristicTokenEstimator" />) can be swapped for a
///     provider-accurate estimator (e.g. one backed by the model's <c>/tokenize</c> endpoint) without touching the
///     budgeting policy. Estimates are advisory only — they bound how much history is sent, never what the model bills.
/// </summary>
public interface ITokenEstimator
{
    /// <summary>Estimates the token count of a single message across all of its content parts.</summary>
    int EstimateTokens(ChatMessage message);

    /// <summary>Estimates the total token count of an ordered message list.</summary>
    int EstimateTokens(IReadOnlyList<ChatMessage> messages);
}
