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
    public void RecordObservedUsage_FirstSample_SeedsTheRatioWithoutSmoothing()
    {
        var store = new TokenEstimatorCalibrationStore();

        // The live 2026-08-24 shape: the budgeters believed 64,512 where the server counted 72,343 — ~12% optimistic.
        store.RecordObservedUsage(Model, estimatedTokens: 64_512, observedInputTokens: 72_343);

        // Seeded, not blended: there is no prior to blend with, and starting at 0.2 of the truth would leave the first
        // several rounds of a model still measuring against a window it has already been shown does not hold.
        AssertClose(72_343d / 64_512d, store.ResolveObservedCorrection(Model));
    }

    [Test]
    public void RecordObservedUsage_SubsequentSamples_BlendWithTheConfiguredSmoothingFactor()
    {
        var store = new TokenEstimatorCalibrationStore();
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
        // other than this prompt. One such report must not be able to halve the effective window.
        store.RecordObservedUsage(Model, estimatedTokens: 1_000, observedInputTokens: 40_000);

        AssertClose(TokenEstimatorCalibrationStore.MaximumObservedCorrection, store.ResolveObservedCorrection(Model));
    }

    [Test]
    public void RecordObservedUsage_WithARatioBelowTheFloor_ClampsToTheFloor()
    {
        var store = new TokenEstimatorCalibrationStore();

        store.RecordObservedUsage(Model, estimatedTokens: 10_000, observedInputTokens: 100);

        AssertClose(TokenEstimatorCalibrationStore.MinimumObservedCorrection, store.ResolveObservedCorrection(Model));
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

    private static void AssertClose(double expected, double actual)
    {
        AssertEx.True(Math.Abs(expected - actual) < 1e-9, $"Expected a correction of {expected}; measured {actual}.");
    }
}
