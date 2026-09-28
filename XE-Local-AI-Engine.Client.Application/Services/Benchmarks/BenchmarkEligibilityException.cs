namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class BenchmarkEligibilityException : InvalidOperationException
{
    public BenchmarkEligibilityException(string message)
        : base(message)
    {
    }

    public BenchmarkEligibilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
