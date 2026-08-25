namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The observed-ratio channel of the estimator calibration: what real provider rounds teach about a model, and the
///     shape in which the budgeters are allowed to act on it. The flat
///     <see cref="TokenEstimatorCalibrationStore.EstimateSafetyFactor" /> mitigates the char heuristic's optimism for a
///     model nothing is known about; this channel is what makes the correction specific once rounds have run.
/// </summary>
public sealed class TokenEstimatorCalibrationStoreTests
{
    private const string Model = "qwen3.8-27b:Q4_K_M";

    [Test]
    public void ResolveObservedCorrection_ForAnUnknownModel_IsExactlyNeutral()
    {
        var store = new TokenEstimatorCalibrationStore();

        // Byte-identical behaviour for a model nothing has been recorded for is the whole safety argument for this
        // channel: neutral means the two budgeters compute the window they always did.
        AssertEx.Equal(TokenEstimatorCalibrationStore.NeutralObservedCorrection, store.ResolveObservedCorrection(Model));
        AssertEx.Equal(TokenEstimatorCalibrationStore.NeutralObservedCorrection, store.ResolveObservedCorrection(modelName: null));
        AssertEx.Equal(TokenEstimatorCalibrationStore.NeutralObservedCorrection, store.ResolveObservedCorrection("   "));
    }

    [Test]
    public void RecordObservedUsage_FirstSample_FoldsFromNeutralRatherThanBeingTakenRaw()
    {
        var store = new TokenEstimatorCalibrationStore();

        // The live 2026-08-24 shape: the budgeters believed 64,512 where the server counted 72,343 — ~12% optimistic.
        store.RecordObservedUsage(Model, estimatedTokens: 64_512, observedInputTokens: 72_343);

        // Folded from neutral, NOT taken raw. Taking the first sample raw would apply it at an effective alpha of 1.0,
        // which is the one case where a single round redefines the window instead of nudging it — and at the upper
        // bound that means an instant cut to 42.5% of the launched window on one anomalous round.
        const double alpha = TokenEstimatorCalibrationStore.ObservedCorrectionSmoothingFactor;
        var sample = 72_343d / 64_512d;
        AssertClose(((1 - alpha) * TokenEstimatorCalibrationStore.NeutralObservedCorrection) + (alpha * sample),
            store.ResolveObservedCorrection(Model));
    }

    [Test]
    public void RecordObservedUsage_OneAnomalousRound_CannotPinTheCorrectionToTheBound()
    {
        var store = new TokenEstimatorCalibrationStore();

        // The regression this seeding exists to prevent: a single absurd round used to land the correction on 2.0
        // outright and take ~10 rounds to decay out of it, quietly halving every window in between.
        store.RecordObservedUsage(Model, estimatedTokens: 10_000, observedInputTokens: 10_000_000);

        var correction = store.ResolveObservedCorrection(Model);
        AssertEx.True(correction < 1.25,
            $"One anomalous round must nudge rather than redefine; measured {correction}.");
    }

    [Test]
    public void RecordObservedUsage_RepeatedSamples_ConvergeTowardTheObservedRatio()
    {
        var store = new TokenEstimatorCalibrationStore();

        // Smoothing must delay the truth, not refuse it: a model that really does cost 12% more than estimated has to
        // arrive there, or the whole channel is a no-op with extra steps.
        for (var round = 0; round < 40; round++)
        {
            store.RecordObservedUsage(Model, estimatedTokens: 10_000, observedInputTokens: 11_200);
        }

        AssertClose(1.12, store.ResolveObservedCorrection(Model), tolerance: 1e-3);
    }

    [Test]
    public void RecordObservedUsage_SubsequentSamples_BlendWithTheConfiguredSmoothingFactor()
    {
        var store = new TokenEstimatorCalibrationStore();
        // A first sample of exactly 1.0 folds from neutral to neutral, so the prior entering the second fold is 1.0
        // and the expectation below reads as the plain one-step blend it is.
        store.RecordObservedUsage(Model, estimatedTokens: 1_000, observedInputTokens: 1_000);

        store.RecordObservedUsage(Model, estimatedTokens: 1_000, observedInputTokens: 1_200);

        const double alpha = TokenEstimatorCalibrationStore.ObservedCorrectionSmoothingFactor;
        AssertClose(((1 - alpha) * 1.0) + (alpha * 1.2), store.ResolveObservedCorrection(Model));
    }

    [Test]
    public void RecordObservedUsage_WithAnAbsurdRatio_ClampsTheSampleBeforeItIsFolded()
    {
        var store = new TokenEstimatorCalibrationStore();

        // A provider that billed a whole cached conversation against one round, or reported a count for something
        // other than this prompt. The SAMPLE is clamped to the bound before it is folded, which is what this asserts:
        // an unclamped 40.0 folded from neutral would blend to 8.8 and then clamp to 2.0, so landing on the
        // fold-of-the-bound instead is the proof that the clamp happened on the sample.
        store.RecordObservedUsage(Model, estimatedTokens: 1_000, observedInputTokens: 40_000);

        AssertClose(Fold(TokenEstimatorCalibrationStore.MaximumObservedCorrection), store.ResolveObservedCorrection(Model));
    }

    [Test]
    public void RecordObservedUsage_WithARatioBelowTheFloor_ClampsToTheFloor()
    {
        var store = new TokenEstimatorCalibrationStore();

        store.RecordObservedUsage(Model, estimatedTokens: 10_000, observedInputTokens: 100);

        AssertClose(Fold(TokenEstimatorCalibrationStore.MinimumObservedCorrection), store.ResolveObservedCorrection(Model));
    }

    [Test]
    public void RecordObservedUsage_BelowTheMinimumSampleSize_IsIgnored()
    {
        var store = new TokenEstimatorCalibrationStore();

        // Short rounds are dominated by per-message framing and the provider's fixed template preamble, so their ratio
        // says nothing about the long rounds the budgeters actually have to trim.
        store.RecordObservedUsage(Model, estimatedTokens: TokenEstimatorCalibrationStore.MinimumObservedSampleTokens - 1, observedInputTokens: 5_000);

        AssertEx.Equal(TokenEstimatorCalibrationStore.NeutralObservedCorrection, store.ResolveObservedCorrection(Model));
    }

    [Test]
    public void RecordObservedUsage_WithNoUsableObservation_IsIgnored()
    {
        var store = new TokenEstimatorCalibrationStore();

        store.RecordObservedUsage(Model, estimatedTokens: 10_000, observedInputTokens: 0);
        store.RecordObservedUsage(Model, estimatedTokens: 10_000, observedInputTokens: -1);

        AssertEx.Equal(TokenEstimatorCalibrationStore.NeutralObservedCorrection, store.ResolveObservedCorrection(Model));
    }

    [Test]
    public void RecordObservedUsage_IsPerModel()
    {
        var store = new TokenEstimatorCalibrationStore();

        store.RecordObservedUsage(Model, estimatedTokens: 10_000, observedInputTokens: 12_000);

        AssertEx.Equal(TokenEstimatorCalibrationStore.NeutralObservedCorrection, store.ResolveObservedCorrection("some-other-model"));
    }

    [Test]
    public async Task RecordObservedUsage_UnderConcurrentRounds_StaysWithinBounds()
    {
        var store = new TokenEstimatorCalibrationStore();

        // Every round of one invocation folds through the same key; the fold must not tear or escape its bounds.
        await Task.WhenAll(Enumerable.Range(0, 8)
                                     .Select(worker => Task.Run(() =>
                                     {
                                         for (var round = 0; round < 500; round++)
                                         {
                                             store.RecordObservedUsage(Model,
                                                 estimatedTokens: 10_000,
                                                 observedInputTokens: worker % 2 == 0 ? 12_000 : 9_000);
                                         }
                                     })))
                  .ConfigureAwait(false);

        var correction = store.ResolveObservedCorrection(Model);
        AssertEx.True(correction is >= TokenEstimatorCalibrationStore.MinimumObservedCorrection and <= TokenEstimatorCalibrationStore.MaximumObservedCorrection,
            $"A concurrently folded correction must stay bounded; measured {correction}.");
    }

    [Test]
    public void ApplyEstimateMargins_AtNeutral_IsTheSafetyMarginAlone()
    {
        // The byte-identical guarantee, asserted rather than assumed: an uncalibrated model gets exactly the window
        // every existing budgeter fixture is built on.
        foreach (var window in new[] { 0, -1, 1, 4_096, 65_536, 1_000_000 })
        {
            AssertEx.Equal(TokenEstimatorCalibrationStore.ApplySafetyMargin(window),
                TokenEstimatorCalibrationStore.ApplyEstimateMargins(window, TokenEstimatorCalibrationStore.NeutralObservedCorrection));
        }
    }

    [Test]
    public void ApplyEstimateMargins_WhenTheEstimatorRunsOptimistic_TightensTheWindow()
    {
        var margined = TokenEstimatorCalibrationStore.ApplySafetyMargin(65_536);

        var tightened = TokenEstimatorCalibrationStore.ApplyEstimateMargins(65_536, observedCorrection: 1.12);

        AssertEx.Equal((int)(margined / 1.12), tightened);
        AssertEx.True(tightened < margined, "A model observed to cost more than estimated must trim earlier, not later.");
    }

    [Test]
    public void ApplyEstimateMargins_WhenTheEstimatorRunsPessimistic_NeverWidensTheWindow()
    {
        var margined = TokenEstimatorCalibrationStore.ApplySafetyMargin(65_536);

        // Tighten-only. Widening on a below-neutral correction would spend the safety factor (and, at the 0.5 bound,
        // 1.7x of the launched window) on an estimate known to be optimistic in the general case.
        AssertEx.Equal(margined, TokenEstimatorCalibrationStore.ApplyEstimateMargins(65_536, observedCorrection: 0.5));
        AssertEx.Equal(margined, TokenEstimatorCalibrationStore.ApplyEstimateMargins(65_536, observedCorrection: 0.9));
    }

    [Test]
    public void ApplyEstimateMargins_WithANonsenseCorrection_FallsBackToTheSafetyMarginAlone()
    {
        var margined = TokenEstimatorCalibrationStore.ApplySafetyMargin(65_536);

        AssertEx.Equal(margined, TokenEstimatorCalibrationStore.ApplyEstimateMargins(65_536, double.NaN));
        AssertEx.Equal(margined, TokenEstimatorCalibrationStore.ApplyEstimateMargins(65_536, double.PositiveInfinity));
        AssertEx.Equal(margined, TokenEstimatorCalibrationStore.ApplyEstimateMargins(65_536, observedCorrection: 0));
        AssertEx.Equal(margined, TokenEstimatorCalibrationStore.ApplyEstimateMargins(65_536, observedCorrection: -3));
    }

    [Test]
    public void ApplyEstimateMargins_NeverDividesByMoreThanTheBound()
    {
        // Defence in depth: the store clamps what it stores, so an out-of-bounds correction can only reach here from a
        // future caller. It must not be able to collapse the window to nothing.
        var margined = TokenEstimatorCalibrationStore.ApplySafetyMargin(65_536);

        AssertEx.Equal((int)(margined / TokenEstimatorCalibrationStore.MaximumObservedCorrection),
            TokenEstimatorCalibrationStore.ApplyEstimateMargins(65_536, observedCorrection: 1_000));
    }

    [Test]
    public void ApplyObservedCorrection_AtNeutral_ReturnsTheBudgetUnchanged()
    {
        // The flat-budget path carries NO safety factor: that reserve exists to stop an estimate overshooting a
        // launched context window, and a policy number like StepContextBudgetTokens has no window to overshoot.
        // Applying the factor here would silently retune the policy by 15%.
        foreach (var budget in new[] { 1, 12_000, 65_536 })
        {
            AssertEx.Equal(budget, TokenEstimatorCalibrationStore.ApplyObservedCorrection(budget, TokenEstimatorCalibrationStore.NeutralObservedCorrection));
        }
    }

    [Test]
    public void ApplyObservedCorrection_WhenTheEstimatorRunsOptimistic_TightensTheBudget()
    {
        AssertEx.Equal((int)(12_000 / 1.5), TokenEstimatorCalibrationStore.ApplyObservedCorrection(12_000, observedCorrection: 1.5));
    }

    [Test]
    public void ApplyObservedCorrection_IsTightenOnlyAndBounded()
    {
        AssertEx.Equal(expected: 12_000, TokenEstimatorCalibrationStore.ApplyObservedCorrection(12_000, observedCorrection: 0.6));
        AssertEx.Equal(expected: 12_000, TokenEstimatorCalibrationStore.ApplyObservedCorrection(12_000, double.NaN));
        AssertEx.Equal((int)(12_000 / TokenEstimatorCalibrationStore.MaximumObservedCorrection),
            TokenEstimatorCalibrationStore.ApplyObservedCorrection(12_000, observedCorrection: 1_000));
        AssertEx.Equal(expected: 0, TokenEstimatorCalibrationStore.ApplyObservedCorrection(budgetTokens: 0, observedCorrection: 1.5));
    }

    [Test]
    public void ApplyEstimateMargins_IsTheSafetyMarginComposedWithTheObservedCorrection()
    {
        // The window path is defined as the flat path applied to the margined window; pinning that keeps the two from
        // drifting apart if either is retuned.
        foreach (var correction in new[] { 1.0, 1.12, 1.5, 0.7, 5.0 })
        {
            AssertEx.Equal(TokenEstimatorCalibrationStore.ApplyObservedCorrection(TokenEstimatorCalibrationStore.ApplySafetyMargin(65_536), correction),
                TokenEstimatorCalibrationStore.ApplyEstimateMargins(65_536, correction));
        }
    }

    /// <summary>One smoothing step from neutral — what a single recorded sample is worth.</summary>
    private static double Fold(double sample)
    {
        const double alpha = TokenEstimatorCalibrationStore.ObservedCorrectionSmoothingFactor;
        return ((1 - alpha) * TokenEstimatorCalibrationStore.NeutralObservedCorrection) + (alpha * sample);
    }

    private static void AssertClose(double expected, double actual, double tolerance = 1e-9)
    {
        AssertEx.True(Math.Abs(expected - actual) < tolerance, $"Expected a correction of {expected}; measured {actual}.");
    }
}
