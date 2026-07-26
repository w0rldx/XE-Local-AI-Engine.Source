namespace XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Process-local, per-model token-estimator calibration. The default remains the conservative chars/4 heuristic;
///     successful llama.cpp <c>/tokenize</c> samples may replace it with a bounded model-specific divisor.
/// </summary>
public interface ITokenEstimatorCalibrationStore
{
    int ResolveDivisor(string? modelName);

    void SetDivisor(string modelName, int charsPerToken);
}
