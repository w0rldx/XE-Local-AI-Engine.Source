namespace XE_Local_AI_Engine.Client.Services.Connection;

/// <summary>
///     Thrown when the Central Platform has permanently revoked the worker credentials (the refresh token
///     is rejected or missing). Unlike a transient refresh failure, this signals that automatic reconnection
///     must stop and the worker must be re-paired.
/// </summary>
public sealed class WorkerCredentialsRevokedException : InvalidOperationException
{
    public WorkerCredentialsRevokedException()
        : base("Worker credentials could not be refreshed. Re-pairing is required.")
    {
    }

    public WorkerCredentialsRevokedException(string message)
        : base(message)
    {
    }

    public WorkerCredentialsRevokedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
