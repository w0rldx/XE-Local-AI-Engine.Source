namespace XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Thrown when a request to <see cref="IScheduledJobManagementService" /> or a handler's parameter check fails
///     validation. The message is safe to surface to callers — it never echoes raw job parameters or secrets.
/// </summary>
public sealed class ScheduledJobValidationException : Exception
{
    public ScheduledJobValidationException(string message) : base(message)
    {
    }

    public ScheduledJobValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
