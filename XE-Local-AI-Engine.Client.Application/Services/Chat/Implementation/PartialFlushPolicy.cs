namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Decides when the streaming pump persists its accumulated content.
/// </summary>
/// <remarks>
///     A partial flush REWRITES the whole message, so a fixed-interval cadence makes per-turn write volume quadratic
///     in output length. The predicate instead flushes when the unpersisted tail reaches a FRACTION of what is
///     persisted, bounding the rewrite-to-append ratio at <c>1 / GrowthFraction</c> whatever the length. The interval
///     floor stops a fast stream flushing more often than the cadence this replaced, and the ceiling checkpoints a
///     stream too slow to trip the growth trigger. The FIRST partial and any TERMINAL flush unconditionally.
/// </remarks>
internal static class PartialFlushPolicy
{
    /// <summary>
    ///     Whether the pump should flush now.
    /// </summary>
    /// <param name="persistedChars">Characters of content + reasoning already written (the persist cursor).</param>
    /// <param name="pendingChars">Characters of content + reasoning accumulated past that cursor.</param>
    /// <param name="elapsed">Time since the last partial flush.</param>
    /// <param name="options">The cadence knobs.</param>
    public static bool ShouldFlush(long persistedChars, long pendingChars, TimeSpan elapsed, ChatStreamBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Nothing advanced: a flush would rewrite the row with the same text it already holds.
        if (pendingChars <= 0)
        {
            return false;
        }

        // Never faster than the cadence this policy replaced, whatever the growth.
        if (elapsed < TimeSpan.FromMilliseconds(options.PartialFlushMinIntervalMs))
        {
            return false;
        }

        // The growth trigger, floored so a short message does not flush on every few characters.
        var growthThreshold = Math.Max(options.PartialFlushMinGrowthChars, persistedChars * options.PartialFlushGrowthFraction);
        if (pendingChars >= growthThreshold)
        {
            return true;
        }

        // A stream too slow to ever trip the growth trigger still checkpoints, which is what bounds crash loss to one
        // ceiling window of output.
        return elapsed >= TimeSpan.FromMilliseconds(options.PartialFlushMaxIntervalMs);
    }
}
