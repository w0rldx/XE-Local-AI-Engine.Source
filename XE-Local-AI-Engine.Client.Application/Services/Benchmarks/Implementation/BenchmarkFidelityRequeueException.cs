namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

/// <summary>
///     A measurement that cannot proceed YET, through no fault of its own — today, a base-logit file another process
///     is still writing.
/// </summary>
/// <remarks>
///     Deliberately not a <see cref="BenchmarkExecutionException" />: that path terminalizes the attempt as failed,
///     and a fidelity work item pins <c>attempt = 1</c>, so there is no retry behind it.
/// </remarks>
internal sealed class BenchmarkFidelityRequeueException : InvalidOperationException
{
    public BenchmarkFidelityRequeueException(string message) : base(message)
    {
    }
}
