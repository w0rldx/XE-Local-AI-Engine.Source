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
    /// </summary>
    public const double EstimateSafetyFactor = 0.85;

    /// <summary>Applies <see cref="EstimateSafetyFactor" /> to a context window, floored at zero.</summary>
    public static int ApplySafetyMargin(int windowTokens)
    {
        return windowTokens <= 0 ? 0 : (int)(windowTokens * EstimateSafetyFactor);
    }

    private readonly ConcurrentDictionary<string, int> _divisors = new(StringComparer.Ordinal);

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
}
