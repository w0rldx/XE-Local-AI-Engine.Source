namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

/// <summary>
///     Exception raised for worker tool call failures.
/// </summary>
public sealed class WorkerToolCallException : Exception
{
    public WorkerToolCallException(string toolName, string message, Exception? innerException = null)
        : base($"Tool call '{toolName}' failed: {message}", innerException)
    {
    }
}
