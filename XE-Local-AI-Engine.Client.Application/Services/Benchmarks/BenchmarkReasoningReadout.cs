namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>The two reasoning facts a run's read derives from its stored counts; nothing here is stored.</summary>
/// <remarks>
///     Both need a single-round turn: the applied budget is the last round's, the reasoning count is the turn's sum.
///     A tool call alone does not prove it, since the runner re-prompts once with no tool call when the final round wrote
///     its call into the reasoning, so the gate also needs exactly one timed provider request (the throughput's
///     <c>SegmentCount</c>). Otherwise, or when a count is unknown, both read null rather than a misleading comparison.
/// </remarks>
public static class BenchmarkReasoningReadout
{
    /// <summary>How far below the applied budget a reasoning count still reads as "at budget".</summary>
    /// <remarks>
    ///     Measured, not guessed: the count tokenizes <c>reasoning_content</c>, which omits the forced closing think tag,
    ///     so a run stopped at its budget counts exactly one short (255 at 256 and 1023 at 1024 in every budgeted probe run).
    /// </remarks>
    public const int AtBudgetSlackTokens = 1;

    /// <summary>
    ///     Whether the reasoning reached the applied budget (<c>reasoning &gt;= applied − slack</c>), or null when either
    ///     count is unknown or the turn was not a single round. Near the threshold this cannot prove a forced end.
    /// </summary>
    public static bool? ReasoningAtBudget(int? reasoningTokens, int? appliedReasoningBudgetTokens, int? toolCallCount, int? segmentCount) =>
        reasoningTokens is { } reasoning && appliedReasoningBudgetTokens is { } applied && IsSingleRound(toolCallCount, segmentCount)
            ? reasoning >= applied - AtBudgetSlackTokens
            : null;

    /// <summary>
    ///     The decoded tokens that were not reasoning, or null under the same single-round rule, and when the reasoning
    ///     exceeds the generation (counts from different rounds, never a negative answer).
    /// </summary>
    public static int? NonReasoningOutputTokens(int? generationTokens, int? reasoningTokens, int? toolCallCount, int? segmentCount) =>
        generationTokens is { } generation && reasoningTokens is { } reasoning && generation >= reasoning && IsSingleRound(toolCallCount, segmentCount)
            ? generation - reasoning
            : null;

    // shortcut: an untimed round (aborted or zero-update stream) beside one timed round still reads 1; upgrade to a runner-reported round count if that shows up.
    private static bool IsSingleRound(int? toolCallCount, int? segmentCount) => toolCallCount is 0 && segmentCount is 1;
}
