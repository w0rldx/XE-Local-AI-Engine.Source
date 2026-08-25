namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

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

    int ResolveDivisor(string? modelName)
    {
        return TokenEstimatorCalibrationStore.DefaultCharsPerToken;
    }

    /// <summary>
    ///     The multiplicative observed-usage correction learned for a model (see
    ///     <see cref="ITokenEstimatorCalibrationStore.ResolveObservedCorrection" />). Exposed here rather than by
    ///     injecting the store into every budgeter: the correction and the divisor are two halves of the same
    ///     calibration, and both belong behind the estimator abstraction the budgeting policy already depends on. The
    ///     neutral default keeps an estimator that knows nothing about calibration behaving exactly as before.
    /// </summary>
    double ResolveObservedCorrection(string? modelName)
    {
        return TokenEstimatorCalibrationStore.NeutralObservedCorrection;
    }

    int EstimateTokensWithDivisor(ChatMessage message, int charsPerToken)
    {
        return EstimateTokens(message);
    }

    int EstimateTokensWithDivisor(IReadOnlyList<ChatMessage> messages, int charsPerToken)
    {
        return EstimateTokens(messages);
    }

    /// <summary>Estimates one message with the named model's calibration when available.</summary>
    int EstimateTokens(ChatMessage message, string? modelName)
    {
        return EstimateTokens(message);
    }

    /// <summary>Estimates a message list with the named model's calibration when available.</summary>
    int EstimateTokens(IReadOnlyList<ChatMessage> messages, string? modelName)
    {
        return EstimateTokens(messages);
    }
}
