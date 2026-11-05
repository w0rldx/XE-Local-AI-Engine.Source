namespace XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

public sealed class NullTokenEstimatorCalibrationScheduler : ITokenEstimatorCalibrationScheduler
{
    public void Schedule(string modelName, Uri llamaServerBaseAddress)
    {
    }

    public void Invalidate(string modelName)
    {
    }
}
