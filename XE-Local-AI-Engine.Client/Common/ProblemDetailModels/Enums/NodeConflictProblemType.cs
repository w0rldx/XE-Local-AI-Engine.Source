namespace XE_Local_AI_Engine.Client.Common.ProblemDetailModels.Enums;

/// <summary>
///     Enumerates supported node conflict problem type values.
/// </summary>
public enum NodeConflictProblemType
{
    ReadOnlyConversation,

    /// <summary>
    ///     An installed image model could not be deleted because its weight files are still held by the running image
    ///     runtime. Retryable after ejecting the runtime.
    /// </summary>
    ImageModelInUse,

    /// <summary>
    ///     A connect request was made while the worker node is not paired with the Central Platform. The operator must
    ///     complete pairing before the node can connect.
    /// </summary>
    WorkerNotPaired,

    /// <summary>
    ///     A connect request was made while the worker node's pairing token has expired. Re-pairing is required before
    ///     the node can connect.
    /// </summary>
    WorkerTokenExpired,

    /// <summary>
    ///     A workspace could not be revoked because its owner/node execution lease is still held. Retryable once the
    ///     in-flight work finishes.
    /// </summary>
    WorkspaceRevocationBusy,

    /// <summary>
    ///     A preview run could not be started because the concurrent-run cap is already reached. The body carries
    ///     <c>maxConcurrentRuns</c> as a problem-details extension.
    /// </summary>
    PreviewWorkflowCapReached,

    /// <summary>
    ///     A preview run could not be started because the graph needs more distinct node-local model processes than the
    ///     loaded-process cap allows. The body carries <c>distinctModelCount</c> and <c>maxLoadedProcesses</c> as
    ///     problem-details extensions.
    /// </summary>
    PreviewWorkflowModelCapExceeded,

    /// <summary>
    ///     An installed base model could not be deleted because installed LoRA adapters launch against it. Retryable
    ///     once the dependent adapters are removed.
    /// </summary>
    InstalledModelHasDependentAdapters,

    /// <summary>
    ///     An installed model could not be deleted because one of its aliases is mapped to a runtime provider other
    ///     than the llama.cpp runtime that owns the GGUF deletion path.
    /// </summary>
    InstalledModelProviderConflict,

    /// <summary>
    ///     An installed-model deletion lost a race with a concurrent model mutation that moved the provider map past
    ///     the revision it read. Retryable after refreshing the model list.
    /// </summary>
    InstalledModelProviderMapSuperseded,

    /// <summary>
    ///     A model-lifecycle operation was asked of a runtime provider that does not own it — deleting a model served
    ///     by an operator-registered external endpoint, which is removed by unregistering it on its connection
    ///     instead. Not retryable: the request names the wrong lifecycle, not a transient state.
    /// </summary>
    ModelOperationNotSupportedByProvider,

    /// <summary>
    ///     A work-session lifecycle call the session's current status forbids — starting one that is already running,
    ///     deleting one mid-step, or repointing the objective of a live run. The operator cancels or pauses first.
    /// </summary>
    WorkSessionInvalidTransition,

    /// <summary>
    ///     A work-session write lost a race with a concurrent one. Two writers touch a running session by design (the
    ///     supervisor moves the status while the state tools write tasks and findings), so this is ordinary rather than
    ///     exceptional: refresh and retry.
    /// </summary>
    WorkSessionVersionConflict,

    /// <summary>
    ///     A development-workflow command the run's or node-run's current status forbids — resuming one that is not
    ///     paused, or deciding a node-run that is neither waiting for approval nor blocked. Re-read the run.
    /// </summary>
    DevWorkflowInvalidTransition,

    /// <summary>
    ///     A development-workflow write lost a race with a concurrent one. The dispatcher moves statuses while a human
    ///     action writes a decision on the same run, so this is ordinary rather than exceptional: refresh and retry.
    /// </summary>
    DevWorkflowVersionConflict,

    /// <summary>
    ///     A second human act on a node-run that is already answered — a NEW operation id, which is not the idempotent
    ///     replay a repeated one is. The body carries <c>standingDecision</c>, so the UI can say what was decided
    ///     instead of only that the click failed.
    /// </summary>
    DevWorkflowGateAlreadyDecided,

    /// <summary>
    ///     The work item already has a run in flight, and v1 allows one at a time. Starting another, or deleting the
    ///     work item, waits for that run to finish or cancels it first.
    /// </summary>
    DevWorkflowRunInFlight,

    /// <summary>
    ///     Both ways a graph-workflow definition write can lose, under one member because from the client's side they
    ///     are one story — somebody else got there first: a stale <c>version</c> on an update, and a delete refused
    ///     while a live run still pins the definition. Refresh the definition, or cancel the run, and retry.
    ///     <para>
    ///         APPENDED deliberately. This enum crosses the wire as the member's NAME — <c>ConflictExceptionHandler</c>
    ///         writes <c>conflictType.Value.ToString()</c> — so appending leaves every name that already ships
    ///         unchanged, and leaves the ordinals a client may have persisted unchanged with them. Inserting a member
    ///         above this one would shift both.
    ///     </para>
    /// </summary>
    GraphWorkflowDefinitionConflict,

    /// <summary>
    ///     Every way a graph-workflow RUN write can lose, under one member for the same reason the definition member
    ///     above holds two stories: from the client's side they are one — you are acting on a version of this run that
    ///     no longer exists. A stale <c>definitionVersion</c> at start, a cancel of a run that has already finished, a
    ///     request id reused on a different definition, and a status move a concurrent writer got to first. Re-read the
    ///     run and decide again.
    ///     <para>
    ///         APPENDED, like the member above and for the same reason: this enum crosses the wire as the member's NAME
    ///         and clients may have persisted its ordinal, so inserting above either one would shift both.
    ///     </para>
    /// </summary>
    GraphWorkflowRunConflict
}
