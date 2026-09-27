namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The <see cref="BenchmarkRunRecord.PrimaryStopReason" /> vocabulary. Values are the provider's own
///     <c>ChatFinishReason</c> tokens, stored verbatim — this class names only the ones the node reasons about, and an
///     unrecognized token is stored and displayed rather than rejected.
/// </summary>
public static class BenchmarkPrimaryStopReasons
{
    /// <summary>Generation ran out of budget: <c>n_predict</c> exhausted, or the context window filled.</summary>
    public const string Length = "length";

    /// <summary>The node cancelled the run at its invocation timeout before the model stopped on its own.</summary>
    public const string Timeout = "timeout";

    /// <summary>
    ///     Generation ran out of budget while still inside its reasoning: <see cref="Length" />, and not one visible
    ///     answer token was emitted.
    /// </summary>
    /// <remarks>
    ///     Truncated for every consumer (<see cref="IsTruncated" /> covers it), but it names the reasoning budget as
    ///     the thing to raise rather than the output budget, which is the whole difference between a run an operator
    ///     can fix and one they cannot explain.
    /// </remarks>
    public const string ReasoningLength = "reasoning-length";

    /// <summary>
    ///     The invocation ended cleanly but produced no answer: the turn stopped on an unanswered tool call, or every
    ///     token it emitted was reasoning.
    /// </summary>
    /// <remarks>
    ///     Node-derived, not a provider token — llama-server reports <c>stop</c> or <c>tool_calls</c> for both shapes,
    ///     which read as a finished answer everywhere downstream and would let a run that answered NOTHING be judged
    ///     and ranked against runs that did.
    /// </remarks>
    public const string Incomplete = "incomplete";

    /// <summary>
    ///     Whether the primary generation stopped because it ran out of budget.
    /// </summary>
    /// <remarks>
    ///     <see cref="Length" /> is the OpenAI-compatible token for BOTH causes llama-server reports it for —
    ///     <c>n_predict</c> exhausted and the context window full (<c>stopped_limit</c>) — and both mean the answer is
    ///     cut off; <see cref="ReasoningLength" /> narrows the same fact and is therefore also truncated. ONE
    ///     implementation on purpose: ranking (<c>BenchmarkRankingPolicy.ApplyRunExclusions</c>) and judging
    ///     (<c>BenchmarkJudgeExecutor</c>) are in different assemblies, and a token added to only one would make ranking exclude a run the judge was never told was cut off.
    /// </remarks>
    public static bool IsTruncated(string? primaryStopReason) =>
        string.Equals(primaryStopReason, Length, StringComparison.OrdinalIgnoreCase)
        || string.Equals(primaryStopReason, ReasoningLength, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Whether the primary generation ended without producing an answer. Same posture as
    ///     <see cref="IsTruncated" />: one implementation, because ranking and judging live in different assemblies and
    ///     must agree on exactly which runs carry no gradable answer.
    /// </summary>
    public static bool IsIncomplete(string? primaryStopReason) =>
        string.Equals(primaryStopReason, Incomplete, StringComparison.OrdinalIgnoreCase);
}
