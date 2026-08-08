namespace XE_Local_AI_Engine.Client.Services.Scheduler;

/// <summary>
///     Thrown by a <see cref="IScheduledJobHandler" /> to declare an <b>already-operator-safe</b> failure reason that
///     <see cref="SchedulerDispatchExecutor" /> may surface verbatim on the run row, the SignalR run event, and the UI —
///     replacing the generic "The scheduled job failed during execution." message for this one path.
///     <para>
///         <b>Security contract (enforced by reviewers).</b> Construct ONLY with text already proven operator-safe: no
///         secrets, no raw utility / process output, no exception or stack text, no raw job parameters. Every other
///         exception type the dispatcher catches keeps the generic message, so this is the single widening of the
///         UI-visible error surface. When in doubt, do NOT use this type — let the generic message apply.
///     </para>
/// </summary>
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
