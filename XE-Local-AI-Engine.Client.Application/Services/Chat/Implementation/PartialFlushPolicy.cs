namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Decides when the streaming pump persists its accumulated content. A partial flush REWRITES the whole message —
///     it re-serializes, re-encrypts and re-writes the full accumulated content and metadata — so a fixed-interval
///     cadence makes per-turn write volume quadratic in output length: at a steady token rate, flush <c>k</c> rewrites
///     <c>k</c> units of text, and the total is the sum, not the length.
///     <para>
///         The fix is one predicate: flush when the unpersisted tail has reached a FRACTION of what is already
///         persisted. Bounding the delta at <c>≥ GrowthFraction · persisted</c> bounds the rewrite-to-append ratio at
///         <c>1 / GrowthFraction</c> regardless of message length, so the total rewritten across a turn of <c>n</c>
///         characters is linear in <c>n</c>. The interval floor keeps a fast stream from flushing more often than the
///         cadence this replaced; the interval ceiling keeps a stream too slow to trip the growth trigger checkpointing
///         anyway, which is what bounds crash loss.
///     </para>
///     <para>
///         The caller keeps two unconditional flushes outside this predicate: the FIRST partial (so a turn is durable
///         from its first token) and any TERMINAL (so the final content is never deferred).
///     </para>
/// </summary>
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
