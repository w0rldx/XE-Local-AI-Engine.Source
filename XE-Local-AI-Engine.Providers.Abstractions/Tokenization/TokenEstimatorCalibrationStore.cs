namespace XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

using System.Collections.Concurrent;

public sealed class TokenEstimatorCalibrationStore : ITokenEstimatorCalibrationStore
{
    public const int DefaultCharsPerToken = 4;
    public const int MinimumCharsPerToken = 1;
    public const int MaximumCharsPerToken = 8;

    /// <summary>
    ///     Fraction of a model's context window the budgeters measure against, absorbing the char-heuristic's optimism.
    ///     <para>
    ///         <see cref="DefaultCharsPerToken" /> is 4, but a Qwen3-class tokenizer runs nearer 3.4–3.6 chars/token on
    ///         English markdown and fenced JSON, so an estimate can sit ~12% BELOW the truth. Measuring against the full
    ///         window therefore lets an over-window round through as "fitting", and the provider rejects it outright
    ///         instead of the budgeters trimming it — observed live twice on 2026-08-24 (72,343 and 71,172 real tokens
    ///         against a 65,536 window). Budgeting against 85% of the window turns that class of failure back into a
    ///         trim. The divisors here are integers, so lowering the divisor itself (4 → 3) would over-correct by 25%;
    ///         this is the finer-grained knob until a rational divisor exists.
    ///     </para>
    ///     <para>
    ///         It is deliberately kept alongside the observed-ratio correction below rather than replaced by it: the
    ///         factor is a flat, always-on floor that protects the very first round of a never-before-seen model, which
    ///         is precisely the round no observation can have taught anything about yet.
    ///     </para>
    /// </summary>
    public const double EstimateSafetyFactor = 0.85;

    /// <summary>The correction of a model nothing has been observed for: multiply/divide by one, i.e. do nothing.</summary>
    public const double NeutralObservedCorrection = 1.0;

    /// <summary>
    ///     Bounds on the observed correction. A ratio outside them is a measurement artefact rather than a tokenizer
    ///     property (a cached prompt reported oddly, a provider counting a whole conversation against one round), and
    ///     letting one through would move the window by a factor no tokenizer difference justifies.
    /// </summary>
    public const double MinimumObservedCorrection = 0.5;

    /// <inheritdoc cref="MinimumObservedCorrection" />
    public const double MaximumObservedCorrection = 2.0;

    /// <summary>
    ///     EMA weight of the newest sample. Low on purpose: the ratio being tracked is a property of the tokenizer and
    ///     the shape of this workload, not of one round, and a single tool-heavy round should nudge the window rather
    ///     than redefine it.
    /// </summary>
    public const double ObservedCorrectionSmoothingFactor = 0.2;

    /// <summary>
    ///     Smallest estimated round, in tokens, that may contribute a sample. Below this the per-message framing
    ///     constants (four tokens a message, plus the provider's own fixed template preamble) dominate the ratio, so a
    ///     handful of short rounds would teach a correction that says nothing about long ones — and long ones are the
    ///     only ones the budgeters ever have to trim.
    /// </summary>
    public const int MinimumObservedSampleTokens = 500;

    /// <summary>Applies <see cref="EstimateSafetyFactor" /> to a context window, floored at zero.</summary>
    public static int ApplySafetyMargin(int windowTokens)
    {
        return windowTokens <= 0 ? 0 : (int)(windowTokens * EstimateSafetyFactor);
    }

    /// <summary>
    ///     The window a budgeter compares its estimate against: <see cref="ApplySafetyMargin" />, then divided by the
    ///     model's observed correction. Dividing the WINDOW rather than scaling the ESTIMATE is what keeps this a
    ///     one-line change at the two comparison sites — every per-message number a budgeter carries (and every test
    ///     asserting one) stays in estimator units.
    ///     <para>
    ///         TIGHTEN-ONLY, and that asymmetry is deliberate. A correction above 1.0 means the provider counts more
    ///         than we predict, so the window shrinks and the round trims earlier. A correction BELOW 1.0 would widen
    ///         it — up to 2× at the bound — which would spend the safety factor and then some on the strength of an
    ///         estimate we already know to be optimistic in the general case. So a below-neutral correction is stored
    ///         (it is how a model that was once tightened stops being tightened) but never applied.
    ///     </para>
    /// </summary>
    public static int ApplyEstimateMargins(int windowTokens, double observedCorrection)
    {
        return ApplyObservedCorrection(ApplySafetyMargin(windowTokens), observedCorrection);
    }

    /// <summary>
    ///     The observed correction alone, applied to a FLAT token budget that is not a context window — the work-session
    ///     step-context budget being the one such caller. Same tighten-only rule and same bound as
    ///     <see cref="ApplyEstimateMargins" />, deliberately WITHOUT <see cref="EstimateSafetyFactor" />: that factor
    ///     reserves headroom inside a launched context window against an estimate that may overshoot it, and a flat
    ///     budget chosen as a policy number has no window to overshoot. Applying it there would silently retune the
    ///     policy by 15%.
    ///     <para>
    ///         Divides the BUDGET rather than scaling the estimate, for the same reason the window path does: it keeps
    ///         every token number the caller carries, and every test asserting one, in estimator units.
    ///     </para>
    /// </summary>
    public static int ApplyObservedCorrection(int budgetTokens, double observedCorrection)
    {
        if (budgetTokens <= 0)
        {
            return 0;
        }

        if (!double.IsFinite(observedCorrection) || observedCorrection <= NeutralObservedCorrection)
        {
            return budgetTokens;
        }

        var corrected = budgetTokens / Math.Min(observedCorrection, MaximumObservedCorrection);
        return corrected <= 0 ? 0 : (int)corrected;
    }

    private readonly ConcurrentDictionary<string, int> _divisors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _observedCorrections = new(StringComparer.Ordinal);

    public int ResolveDivisor(string? modelName)
    {
        return !string.IsNullOrWhiteSpace(modelName) && _divisors.TryGetValue(modelName, out var divisor)
            ? divisor
            : DefaultCharsPerToken;
    }

    public void SetDivisor(string modelName, int charsPerToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        _divisors[modelName] = Math.Clamp(charsPerToken, MinimumCharsPerToken, MaximumCharsPerToken);
    }

    /// <inheritdoc />
    public void RecordObservedUsage(string modelName, long estimatedTokens, long observedInputTokens)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // Fail quiet, not loud: this runs on the inference path from a provider response we do not control, so an
        // absent, zero or nonsensical usage report must cost nothing and change nothing.
        if (estimatedTokens < MinimumObservedSampleTokens || observedInputTokens <= 0)
        {
            return;
        }

        var sample = Math.Clamp((double)observedInputTokens / estimatedTokens, MinimumObservedCorrection, MaximumObservedCorrection);

        // The FIRST sample folds from neutral rather than being taken raw, so no single round can ever move the window
        // by more than the smoothing factor allows. Taking it raw looks tempting — there is no prior to blend with, and
        // it reaches a genuinely optimistic model's true ratio in one round instead of ten — but it means one anomalous
        // round at the 2.0 bound pins the correction there outright, cutting the effective window to 42.5% of the
        // launched one and taking ~10 rounds to decay back. That is the failure this smoothing exists to prevent, and
        // the flat EstimateSafetyFactor already covers the rounds before the EMA has converged.
        //
        // AddOrUpdate's update delegate re-reads the current value on each CAS retry, so the fold is applied to the
        // value it actually replaces even under concurrent rounds of the same model.
        _ = _observedCorrections.AddOrUpdate(modelName, Fold(NeutralObservedCorrection, sample), (_, prior) => Fold(prior, sample));
    }

    /// <inheritdoc />
    public double ResolveObservedCorrection(string? modelName)
    {
        return !string.IsNullOrWhiteSpace(modelName) && _observedCorrections.TryGetValue(modelName, out var correction)
            ? correction
            : NeutralObservedCorrection;
    }

    private static double Fold(double prior, double sample)
    {
        var blended = ((1.0 - ObservedCorrectionSmoothingFactor) * prior) + (ObservedCorrectionSmoothingFactor * sample);
        return Math.Clamp(blended, MinimumObservedCorrection, MaximumObservedCorrection);
    }
}
