namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     The event-type tags the runtime appends on top of the ones the store writes for its own mutations.
/// </summary>
/// <remarks>
///     The store's own are <c>SessionCreated</c>, <c>SessionStatusChanged</c>, <c>WorkPlanApplied</c>,
///     <c>FindingRecorded</c>, <c>ArtifactSaved</c>, <c>CheckpointRecorded</c>, <c>StepAdvanced</c> and
///     <c>SessionInterrupted</c>.
/// </remarks>
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
    ///     The step's turn ended without a fault, and the row carries what it spent as a
    ///     <c>WorkSessionStepConsumptionDetail</c>.
    /// </summary>
    /// <remarks>
    ///     The outcome tells the cases apart: <c>Completed</c> is an ordinary step, and anything else names what stopped it —
    ///     <c>ProviderCallBudget</c> or <c>ToolGate</c>, of which the gate outcome carries no consumption detail because
    ///     nothing ran. Neither <c>Completed</c> nor a clipped step is a failure: the session stays
    ///     runnable and resumes from the state block, so neither may ever be written as <see cref="StepFailed" />. See
    ///     <c>docs/wiki/04-agent-mode.md</c> §5.7 for what the row records and which steps write none.
    /// </remarks>
    public const string StepEnded = "StepEnded";

    /// <summary>
    ///     <c>complete_work_session</c> fired inside the turn. The supervisor reads this back at step end rather than
    ///     holding a flag in memory, so the request survives the process the same way every other session fact does.
    /// </summary>
    public const string CompletionRequested = "CompletionRequested";

    /// <summary>Written by the store when a step advances; the boundary between one step number and the next.</summary>
    public const string StepAdvanced = "StepAdvanced";

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
    ///     A step the tool gate stopped before it was sent, on its own phase rather than <see cref="Ended" />.
    /// </summary>
    /// <remarks>
    ///     That step is retried after the operator fixes the allow-list, and sharing the phase would let idempotency
    ///     swallow the real row the retried step writes when it actually runs.
    /// </remarks>
    public const string ToolGate = "tool-gate";
}
