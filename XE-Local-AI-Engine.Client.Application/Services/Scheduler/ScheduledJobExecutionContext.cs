namespace XE_Local_AI_Engine.Client.Services.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     All runtime information the dispatcher passes to a <see cref="IScheduledJobHandler" /> for a single
///     scheduled invocation. Created by <c>SchedulerDispatchExecutor</c> and consumed by handlers.
/// </summary>
public sealed class ScheduledJobExecutionContext
{
    /// <summary>Primary key of the <c>scheduled_job_definitions</c> row that triggered this run.</summary>
    public required Guid ScheduledJobId { get; init; }

    /// <summary>Template identifier, copied from the job definition.</summary>
    public required string TemplateId { get; init; }

    /// <summary>Display name of the job definition at the time it fired.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    ///     Decrypted plaintext parameters from the job definition's <c>parameters_enc</c> column, or
    ///     <see langword="null" /> when the definition stores no parameters. Handlers must treat this as
    ///     untrusted input and validate against their declared <see cref="ScheduledJobTemplateDescriptor.ParameterSchema" />.
    /// </summary>
    public required string? Parameters { get; init; }

    /// <summary>Quartz fire-instance identifier, unique per trigger firing. Useful for idempotency checks.</summary>
    public required string FireInstanceId { get; init; }

    /// <summary>
    ///     The time Quartz intended to fire the trigger, or <see langword="null" /> for manual / system triggers
    ///     that have no scheduled time.
    /// </summary>
    public required DateTimeOffset? ScheduledFireTimeUtc { get; init; }

    /// <summary>The wall-clock time at which the dispatcher received the fire signal from Quartz.</summary>
    public required DateTimeOffset ActualFireTimeUtc { get; init; }

    /// <summary>What caused this run to fire: schedule, manual operator action, agent, or system.</summary>
    public required ScheduledRunTrigger TriggeredBy { get; init; }

    /// <summary>
    ///     Optional progress-reporting callback. Handlers may invoke this to emit intermediate progress events
    ///     that are recorded in <c>scheduled_job_run_events</c>.
    ///     <para>
    ///         <b>Progress callback wiring point</b> — this property defaults to a no-op until run-history recording
    ///         injects a real implementation via the dispatcher before passing the
    ///         context to the handler. Handlers should null-check before calling; a <see langword="null" /> value
    ///         means progress events are silently dropped (acceptable for Summary-level templates).
    ///     </para>
    /// </summary>
    public Func<string, int?, CancellationToken, Task>? ReportProgressAsync { get; init; }

    /// <summary>
    ///     Optional operator-facing one-line outcome for this fire, written by the handler and persisted verbatim onto
    ///     the run row's <c>summary</c> by the dispatcher when the handler completes successfully. Left
    ///     <see langword="null" /> (the default) the dispatcher records its generic "Completed." instead, so a handler
    ///     that has nothing distinctive to say need not set it.
    ///     <para>
    ///         Same content rules as <see cref="ReportProgressAsync" />: the column is plaintext-structural, so this
    ///         must carry only sanitized facts about the work (counts, ids, operator-supplied names) — never prompt,
    ///         parameter or model-answer text. Last write wins; a handler that sets it more than once records its final
    ///         value.
    ///     </para>
    /// </summary>
    public string? Summary { get; set; }
}
