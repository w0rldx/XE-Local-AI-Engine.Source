namespace XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

using System.Collections.Concurrent;

public sealed class TokenEstimatorCalibrationStore : ITokenEstimatorCalibrationStore
{
    public const int DefaultCharsPerToken = 4;
    public const int MinimumCharsPerToken = 1;
    public const int MaximumCharsPerToken = 8;

    /// <summary>
    ///     Fraction of a model's context window the budgeters measure against, absorbing the optimism of a chars/4
    ///     heuristic (<see cref="DefaultCharsPerToken" /> is 4 while a Qwen3-class tokenizer runs nearer 3.4–3.6).
    /// </summary>
    /// <remarks>
    ///     On English markdown and fenced JSON an estimate can sit ~12% BELOW the truth, and measuring against the full window lets an
    ///     over-window round pass as "fitting", so the provider rejects it instead of the budgeters trimming it — measured live at
    ///     72,343 and 71,172 real tokens against a 65,536 window; 85% turns that back into a trim. The finer-grained knob while divisors
    ///     stay integers (4 → 3 over-corrects by 25%). Kept alongside the observed-ratio correction, not replaced by it: a flat,
    ///     always-on floor protects the first round of a never-seen model, the one round no observation can have taught.
    /// </remarks>
    public const double EstimateSafetyFactor = 0.85;

    /// <summary>The correction of a model nothing has been observed for: multiply/divide by one, i.e. do nothing.</summary>
    public const double NeutralObservedCorrection = 1.0;

    /// <summary>Bounds on the observed correction.</summary>
    /// <remarks>
    ///     A ratio outside them is a measurement artefact rather than a tokenizer property (a cached prompt reported
    ///     oddly, a provider counting a whole conversation against one round), and letting one through would move the
    ///     window by a factor no tokenizer difference justifies.
    /// </remarks>
    public const double MinimumObservedCorrection = 0.5;

    /// <inheritdoc cref="MinimumObservedCorrection" />
    public const double MaximumObservedCorrection = 2.0;

    /// <summary>
    ///     EMA weight of the newest sample. Low on purpose: the ratio being tracked is a property of the tokenizer and
    ///     the shape of this workload, not of one round, and a single tool-heavy round should nudge the window rather
    ///     than redefine it.
    /// </summary>
    public const double ObservedCorrectionSmoothingFactor = 0.2;

    /// <summary>Smallest estimated round, in tokens, that may contribute a sample.</summary>
    /// <remarks>
    ///     Below this the per-message framing constants (four tokens a message, plus the provider's own fixed template
    ///     preamble) dominate the ratio, so a handful of short rounds would teach a correction that says nothing about
    ///     long ones — and long ones are the only ones the budgeters ever have to trim.
    /// </remarks>
    public const int MinimumObservedSampleTokens = 500;

    /// <summary>Applies <see cref="EstimateSafetyFactor" /> to a context window, floored at zero.</summary>
    public static int ApplySafetyMargin(int windowTokens)
    {
        return windowTokens <= 0 ? 0 : (int)(windowTokens * EstimateSafetyFactor);
    }

    /// <summary>
    ///     The window a budgeter compares its estimate against: <see cref="ApplySafetyMargin" />, then divided by the
    ///     model's observed correction.
    /// </summary>
    /// <remarks>
    ///     Dividing the WINDOW rather than scaling the ESTIMATE keeps this a one-line change at the two comparison sites — every per-message
    ///     number a budgeter carries, and every test asserting one, stays in estimator units. TIGHTEN-ONLY, deliberately: above 1.0 the
    ///     provider counts more than we predict, so the window shrinks and the round trims earlier; one BELOW 1.0 would widen it — up to 2× at
    ///     the bound — spending the safety factor and more on an estimate already known to be optimistic. A below-neutral correction is
    ///     stored — how a once-tightened model stops being tightened — but never applied.
    /// </remarks>
    public static int ApplyEstimateMargins(int windowTokens, double observedCorrection)
    {
        return ApplyObservedCorrection(ApplySafetyMargin(windowTokens), observedCorrection);
    }

    /// <summary>
    ///     The observed correction alone, applied to a FLAT token budget that is not a context window — the work-session
    ///     step-context budget being the one such caller.
    /// </summary>
    /// <remarks>
    ///     Same tighten-only rule and same bound as <see cref="ApplyEstimateMargins" />, deliberately WITHOUT
    ///     <see cref="EstimateSafetyFactor" />: that factor reserves headroom inside a launched context window against an estimate that may
    ///     overshoot it, and a flat budget chosen as a policy number has no window to overshoot, so applying it there would silently retune
    ///     the policy by 15%. Divides the BUDGET rather than scaling the estimate, for the same reason the window path does: every token
    ///     number the caller carries, and every test asserting one, stays in estimator units.
    /// </remarks>
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
    /// <remarks>
    ///     The FIRST sample folds from neutral rather than being taken raw, so no single round can move the window by more than the
    ///     smoothing factor allows. Raw looks tempting — there is no prior to blend with, and it reaches a genuinely optimistic model's
    ///     true ratio in one round instead of ten — but one anomalous round at the 2.0 bound would pin the correction there outright,
    ///     cutting the effective window to 42.5% of the launched one and taking ~10 rounds to decay back. The flat
    ///     <see cref="EstimateSafetyFactor" /> already covers the rounds before the EMA has converged.
    /// </remarks>
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

        // AddOrUpdate's update delegate re-reads the current value on each CAS retry, so the fold is applied to the value it actually
        // replaces even under concurrent rounds of the same model. The neutral seed is the first-sample smoothing rule (see the docs).
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
