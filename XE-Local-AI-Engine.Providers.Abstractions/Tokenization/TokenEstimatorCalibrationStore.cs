namespace XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

using System.Collections.Concurrent;

public sealed class TokenEstimatorCalibrationStore : ITokenEstimatorCalibrationStore
{
    public const int DefaultCharsPerToken = 4;
    public const int MinimumCharsPerToken = 2;
    public const int MaximumCharsPerToken = 8;

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
