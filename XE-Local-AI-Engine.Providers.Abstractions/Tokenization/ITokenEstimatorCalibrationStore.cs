namespace XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Process-local, per-model token-estimator calibration. The default remains the conservative chars/4 heuristic;
///     successful llama.cpp <c>/tokenize</c> samples may replace it with a bounded model-specific divisor.
///     <para>
///         Two independent channels, deliberately: the DIVISOR is an integer chars-per-token measured against a fixed
///         sample of prose, and the OBSERVED CORRECTION is a multiplicative factor learned from real rounds — what the
///         provider actually billed for a request divided by what this estimator predicted for that same request. The
///         divisor cannot express the residual (it is an integer, and 4 → 3 over-corrects by 25%); the correction can,
///         and it also absorbs everything the message estimate never sees — chat-template framing, tool-schema
///         serialization differences, special tokens.
///     </para>
/// </summary>
public interface ITokenEstimatorCalibrationStore
{
    int ResolveDivisor(string? modelName);

    void SetDivisor(string modelName, int charsPerToken);

    /// <summary>
    ///     Folds one real observation into <paramref name="modelName" />'s observed correction:
    ///     <paramref name="observedInputTokens" /> is the provider's own reported prompt-token count for a round whose
    ///     estimated input was <paramref name="estimatedTokens" />. Both are TOKEN counts (not characters), because the
    ///     only site that can honestly pair them — the provider-round budget boundary — holds the estimate it just
    ///     computed for exactly the message set it is about to send, and the response's usage describes that same set.
    ///     Samples that are too small to be informative, or that carry no usable observation, are ignored.
    /// </summary>
    void RecordObservedUsage(string modelName, long estimatedTokens, long observedInputTokens);

    /// <summary>
    ///     The multiplicative correction learned for <paramref name="modelName" />: observed ÷ estimated, smoothed and
    ///     bounded. Above 1.0 means the estimator runs OPTIMISTIC for this model (the provider counts more tokens than
    ///     predicted). Exactly <see cref="TokenEstimatorCalibrationStore.NeutralObservedCorrection" /> when nothing has
    ///     been recorded, so an uncalibrated model behaves exactly as it did before this channel existed.
    /// </summary>
    double ResolveObservedCorrection(string? modelName);
}
