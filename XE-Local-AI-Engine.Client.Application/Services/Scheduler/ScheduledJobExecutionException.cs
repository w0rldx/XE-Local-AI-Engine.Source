namespace XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Thrown by a <see cref="IScheduledJobHandler" /> to declare an <b>already-operator-safe</b> failure reason
///     <see cref="SchedulerDispatchExecutor" /> may surface verbatim on the run row, the run event and the UI.
/// </summary>
/// <remarks>
///     It replaces the generic "The scheduled job failed during execution." message for this one path. <b>Security
///     contract, enforced by reviewers:</b> construct ONLY with text already proven operator-safe — no secrets, no raw
///     utility or process output, no exception or stack text, no raw job parameters. Every other exception type the
///     dispatcher catches, bar <see cref="ScheduledJobValidationException" />, keeps the generic message.
///     When in doubt, do NOT use this type.
/// </remarks>
public sealed class ScheduledJobExecutionException : Exception
{
    public ScheduledJobExecutionException(string message) : base(message)
    {
    }

    public ScheduledJobExecutionException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>The operator-safe message; identical to <see cref="Exception.Message" />, named for call-site clarity.</summary>
    public string SanitizedMessage => Message;
}
