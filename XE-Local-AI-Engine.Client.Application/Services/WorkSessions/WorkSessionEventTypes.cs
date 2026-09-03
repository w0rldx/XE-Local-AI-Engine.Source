namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     The event-type tags the runtime appends on top of the ones the store writes for its own mutations
///     (<c>SessionCreated</c>, <c>SessionStatusChanged</c>, <c>WorkPlanApplied</c>, <c>FindingRecorded</c>,
///     <c>ArtifactSaved</c>, <c>CheckpointRecorded</c>, <c>StepAdvanced</c>, <c>SessionInterrupted</c>).
/// </summary>
internal static class WorkSessionEventTypes
{
    /// <summary>One step is about to be sent. Written before the send, so a subscriber can attach to the live turn.</summary>
    public const string StepStarted = "StepStarted";

    /// <summary>
    ///     The step ended on a provider or runtime failure. The outcome carries the sanitized reason, and the detail
    ///     carries what the step spent (<c>WorkSessionStepConsumptionDetail</c>) — counts plus the names of the tools it
    ///     called, and nothing else.
    /// </summary>
    public const string StepFailed = "StepFailed";

    /// <summary>
    ///     The step's turn ended without a fault, and the row carries what it spent
    ///     (<c>WorkSessionStepConsumptionDetail</c>: counts plus the names of the tools it called) — which is why it is
    ///     written for EVERY such step and not only for the clipped ones: a record that exists only when a bound trips
    ///     measures the bound rather than the work. It is also the only durable place those tool names survive, because
    ///     the scope they are collected in is disposed when the step ends.
    ///     <para>
    ///         The outcome tells them apart. <c>Completed</c> is an ordinary step. Anything else names what stopped it:
    ///         <c>ProviderCallBudget</c>, the per-step provider-call cap, or <c>ToolGate</c>, the allow-list check that
    ///         refuses a step BEFORE it is sent (the only outcome whose row carries no consumption detail — nothing
    ///         ran). None of them is a failure: the session stays runnable and the step resumes from the state block,
    ///         so none may ever be written as <see cref="StepFailed" />.
    ///     </para>
    ///     <para>
    ///         A step stopped through the cancellation registry — paused, cancelled, an expired park, a blown deadline
    ///         — writes no row here, deliberately: the run may still be unwinding when the supervisor sees its
    ///         terminal, so its counters would be a race rather than a measurement.
    ///     </para>
    /// </summary>
    public const string StepEnded = "StepEnded";

    /// <summary>
    ///     <c>complete_work_session</c> fired inside the turn. The supervisor reads this back at step end rather than
    ///     holding a flag in memory, so the request survives the process the same way every other session fact does.
    /// </summary>
    public const string CompletionRequested = "CompletionRequested";

    /// <summary>A park outlived <c>MaxParkedSeconds</c> and the step was cancelled to free the node's invocation slot.</summary>
    public const string ParkTimedOut = "ParkTimedOut";
}

/// <summary>The phase tag that rides inside a supervisor event's derived operation id, making a replayed step idempotent.</summary>
internal static class WorkSessionStepPhases
{
    public const string Started = "started";
    public const string Failed = "failed";
    public const string Ended = "ended";
    public const string ParkExpired = "park-expired";

    /// <summary>
    ///     A step the tool gate stopped before it was sent. Its own phase, not <see cref="Ended" />: that step is
    ///     retried after the operator fixes the allow-list, and sharing the phase would let idempotency swallow the
    ///     real row the retried step writes when it actually runs.
    /// </summary>
    public const string ToolGate = "tool-gate";
}
