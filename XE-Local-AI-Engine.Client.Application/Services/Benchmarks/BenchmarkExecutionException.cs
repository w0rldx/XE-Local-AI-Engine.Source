namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

internal sealed class BenchmarkExecutionException : InvalidOperationException
{
    public BenchmarkExecutionException(string message)
        : base(message)
    {
    }

    public BenchmarkExecutionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    ///     Why generation stopped, when this failure knows — <c>timeout</c> for a run the node cancelled at its
    ///     invocation budget. Null for every failure that cannot explain itself, which then records nothing.
    /// </summary>
    public string? StopReason { get; init; }
}
