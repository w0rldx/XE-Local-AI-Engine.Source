namespace XE_Local_AI_Engine.Client.Services.WorkSessions;

using System.Text.Json;

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
    ///     <c>ProviderCallBudget</c>, <c>ToolGate</c> or <see cref="WriteGateOutcome" />, of which the two gate outcomes carry no
    ///     consumption detail because nothing ran. Neither <c>Completed</c> nor a clipped step is a failure: the session stays
    ///     runnable and resumes from the state block, so neither may ever be written as <see cref="StepFailed" />. See
    ///     <c>docs/wiki/04-agent-mode.md</c> §5.7 for what the row records and which steps write none.
    /// </remarks>
    public const string StepEnded = "StepEnded";

    /// <summary>
    ///     <c>complete_work_session</c> fired inside the turn. The supervisor reads this back at step end rather than
    ///     holding a flag in memory, so the request survives the process the same way every other session fact does.
    /// </summary>
    public const string CompletionRequested = "CompletionRequested";

    /// <summary>A park outlived <c>MaxParkedSeconds</c> and the step was cancelled to free the node's invocation slot.</summary>
    public const string ParkTimedOut = "ParkTimedOut";

    /// <summary>
    ///     The <see cref="StepEnded" /> outcome for a turn the write-declaration guard refused before it was sent
    ///     (<c>GRAPH-C4-2</c>).
    /// </summary>
    /// <remarks>
    ///     Unlike the other outcomes here this one IS terminal — the session settles Failed — and its row is the durable record of
    ///     WHY, carrying the refusal sentence as its detail. It lives here rather than on the supervisor because the
    ///     development-workflow lane reads it back: the run that owns the session answers with this rule's own failure class, and a
    ///     cause re-derived from the definition's CURRENT state would answer differently the moment an operator put it back.
    /// </remarks>
    public const string WriteGateOutcome = "WriteGate";

    /// <summary>
    ///     Puts a <see cref="WriteGateOutcome" /> row's refusal sentence into the event's detail, and takes it back
    ///     out.
    /// </summary>
    /// <remarks>
    ///     The pair lives here so the supervisor that writes the row and the development-workflow poll that reads it
    ///     cannot disagree about the encoding. The detail column is JSON everywhere else on this log, so the sentence
    ///     travels as a JSON string rather than as raw text.
    /// </remarks>
    public static string WriteGateDetail(string refusal) =>
        JsonSerializer.Serialize(refusal);

    /// <inheritdoc cref="WriteGateDetail" />
    public static string? ReadWriteGateDetail(string? detailJson) =>
        detailJson is null ? null : JsonSerializer.Deserialize<string>(detailJson);
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

    /// <summary>
    ///     A step the write-declaration guard stopped before it was sent (<c>GRAPH-C4-2</c>). Its own phase for the
    ///     same reason as <see cref="ToolGate" />: sharing one would let idempotency swallow the row a step that really
    ///     ran would write.
    /// </summary>
    public const string WriteGate = "write-gate";
}
