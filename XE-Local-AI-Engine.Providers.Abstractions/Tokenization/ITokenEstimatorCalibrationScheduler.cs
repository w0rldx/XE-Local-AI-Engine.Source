namespace XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Non-blocking notification that a real llama.cpp request has resolved a live model endpoint. Implementations
///     calibrate asynchronously; callers must never wait for calibration on the inference path.
/// </summary>
public interface ITokenEstimatorCalibrationScheduler
{
    void Schedule(string modelName, Uri llamaServerBaseAddress);
}

public sealed class NullTokenEstimatorCalibrationScheduler : ITokenEstimatorCalibrationScheduler
{
    public void Schedule(string modelName, Uri llamaServerBaseAddress)
    {
    }
}
