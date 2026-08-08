namespace XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     Exception raised for worker token expired failures.
/// </summary>
public sealed class WorkerTokenExpiredException : InvalidOperationException
{
    public WorkerTokenExpiredException()
        : base("Worker token has expired. Re-pairing is required.")
    {
    }
}
