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

    /// <summary>The step ended on a provider or runtime failure. The outcome carries the sanitized reason.</summary>
    public const string StepFailed = "StepFailed";

    /// <summary>
    ///     The step stopped on a bound rather than a fault — today only its provider-call cap. NOT a failure: the
    ///     session stays runnable and the next step resumes from the state block, so this must never be written as
    ///     <see cref="StepFailed" />. The outcome names which bound stopped it.
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
}
