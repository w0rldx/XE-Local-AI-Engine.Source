namespace XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Thrown when a scheduled-job create/update/trigger request fails validation in
///     <see cref="IScheduledJobManagementService" />. The message is safe to surface to callers — it never echoes raw
///     job parameters or any other secret material.
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
