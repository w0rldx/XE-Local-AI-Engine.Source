namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A partial flush rewrites the WHOLE accumulated message, so a fixed-interval cadence makes per-turn write volume
///     quadratic in output length. <see cref="PartialFlushPolicy" /> replaces the interval with a growth trigger, which
///     bounds the rewrite-to-append ratio independently of message length.
/// </summary>
public sealed class PartialFlushPolicyTests
{
    [Test]
    public void ShouldFlush_FiresAtExactlyTheGrowthFraction()
    {
        var options = new ChatStreamBudgetOptions();
        var pastTheFloor = TimeSpan.FromMilliseconds(options.PartialFlushMinIntervalMs);

        // 20% of 10 000 is 2 000, well clear of the 512-character floor, so the fraction is what decides here.
        AssertEx.True(PartialFlushPolicy.ShouldFlush(persistedChars: 10_000, pendingChars: 2_000, pastTheFloor, options),
            "Exactly the growth fraction must flush.");
        AssertEx.False(PartialFlushPolicy.ShouldFlush(persistedChars: 10_000, pendingChars: 1_999, pastTheFloor, options),
            "One character short of the growth fraction must not flush.");
    }

    [Test]
    public void ShouldFlush_MinGrowthCharsFloorsTheFractionForAShortMessage()
    {
        var options = new ChatStreamBudgetOptions();
        var pastTheFloor = TimeSpan.FromMilliseconds(options.PartialFlushMinIntervalMs);

        // 20% of 100 characters is 20 — without the floor a short message would flush on almost every snapshot, which
        // is the cadence this policy exists to remove.
        AssertEx.False(PartialFlushPolicy.ShouldFlush(persistedChars: 100, pendingChars: 50, pastTheFloor, options),
            "Growth over a short message must be floored by MinGrowthChars.");
        AssertEx.True(PartialFlushPolicy.ShouldFlush(persistedChars: 100, pendingChars: options.PartialFlushMinGrowthChars, pastTheFloor, options),
            "Reaching MinGrowthChars must flush.");
    }

    [Test]
    public void ShouldFlush_MaxIntervalCheckpointsAStreamTooSlowToTripTheGrowthTrigger()
    {
        var options = new ChatStreamBudgetOptions();

        // Ten characters against a 100 KB message will never reach 20%. Without the ceiling this turn would not
        // checkpoint again until it terminalized, so a hard crash would lose everything since the last flush.
        AssertEx.False(PartialFlushPolicy.ShouldFlush(persistedChars: 100_000, pendingChars: 10, TimeSpan.FromMilliseconds(options.PartialFlushMaxIntervalMs - 1), options),
            "Below the ceiling a sub-threshold growth must not flush.");
        AssertEx.True(PartialFlushPolicy.ShouldFlush(persistedChars: 100_000, pendingChars: 10, TimeSpan.FromMilliseconds(options.PartialFlushMaxIntervalMs), options),
            "At the ceiling even a tiny growth must checkpoint.");
    }

    [Test]
    public void ShouldFlush_MinIntervalSuppressesAFastStreamWhateverTheGrowth()
    {
        var options = new ChatStreamBudgetOptions();

        // The floor wins over the growth trigger: a burst that doubles the message still waits out the interval, so a
        // fast local model can never drive more flushes than the cadence this policy replaced.
        AssertEx.False(PartialFlushPolicy.ShouldFlush(persistedChars: 1_000, pendingChars: 100_000, TimeSpan.FromMilliseconds(options.PartialFlushMinIntervalMs - 1), options),
            "Inside the minimum interval nothing may flush.");
    }

    [Test]
    public void ShouldFlush_WhenNothingAdvanced_DoesNotFlush()
    {
        var options = new ChatStreamBudgetOptions();

        AssertEx.False(PartialFlushPolicy.ShouldFlush(persistedChars: 10_000, pendingChars: 0, TimeSpan.FromHours(1), options),
            "A flush with no pending characters would rewrite the row with the text it already holds.");
    }

    [Test]
    public void ShouldFlush_OverATurnGovernedByGrowth_RewritesALinearMultipleOfTheOutput()
    {
        // The asymptotic claim, driven end to end. Let g = GrowthFraction and r = 1 + g. Every flush leaves the message
        // at least r times longer than the previous flush left it, so the flush points P₁ < … < P_k ≤ n satisfy
        // P_{i+1} ≥ r·P_i. Each flush rewrites its own P_i, so summing backwards from P_k gives a geometric series:
        //
        //     Σ P_i ≤ P_k·(1 + 1/r + 1/r² + …) = P_k·r/(r−1) = n·(1+g)/g
        //
        // and the turn's terminal rewrites the full n once more, for a total bound of n·(1+g)/g + n = n·(1+2g)/g.
        // At g = 0.20 that is 7n, not 6n — the 6n figure is the flush series ALONE and silently drops the terminal.
        //
        // MinGrowthChars and MinIntervalMs cannot break this: both only ever DELAY a flush, which makes each delta
        // larger and the ratio bigger than r, never smaller. (Concretely, MinIntervalMs dominates below ~10 000
        // characters here, giving ratios of 2.0 down to 1.25 — all comfortably above 1.2 — and the growth trigger takes
        // over above it.)
        //
        // The drive is deliberately fast enough (20 000 chars/s) that MaxIntervalMs never fires: this bound belongs to
        // the growth branch. The ceiling exists to bound CRASH LOSS on a slow stream, and it knowingly trades this
        // bound back for that — see the design's amplification target for the ceiling-governed case.
        var options = new ChatStreamBudgetOptions();
        var step = TimeSpan.FromMilliseconds(20);
        const int CharsPerStep = 400;
        const long TargetChars = 120_000;

        long persisted = 0;
        long produced = 0;
        long rewritten = 0;
        var elapsed = TimeSpan.Zero;

        while (produced < TargetChars)
        {
            produced += CharsPerStep;
            elapsed += step;

            if (!PartialFlushPolicy.ShouldFlush(persisted, produced - persisted, elapsed, options))
            {
                continue;
            }

            // A flush rewrites everything accumulated so far, not just the delta — that is the whole cost model.
            rewritten += produced;
            persisted = produced;
            elapsed = TimeSpan.Zero;
        }

        // The turn's terminal always writes the full message once more.
        rewritten += produced;

        // n·(1+2g)/g, derived above — expressed from the knob rather than as a constant so retuning GrowthFraction
        // retunes the bound instead of silently invalidating it.
        var growthFraction = options.PartialFlushGrowthFraction;
        var bound = (long)Math.Ceiling(produced * ((1 + (2 * growthFraction)) / growthFraction));

        AssertEx.True(rewritten <= bound,
            $"Rewriting {rewritten} characters for {produced} characters of output exceeds the linear bound of {bound} "
            + $"(n·(1+2g)/g at g={growthFraction}).");
    }
}
