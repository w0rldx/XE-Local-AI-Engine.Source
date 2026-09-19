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
    ///     A workspace could not be revoked because its owner/node execution lease is still held. Retryable once the
    ///     in-flight work finishes.
    /// </summary>
    WorkspaceRevocationBusy,

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
    GraphWorkflowRunConflict,

    /// <summary>
    ///     A second human act on a graph-workflow pause that is already answered — a NEW operation id on a decided
    ///     row, or an id that already decided a different pause of the same run. The body carries
    ///     <c>standingDecision</c>, so the UI can say what was decided instead of only that the click failed.
    ///     <para>
    ///         APPENDED, like the two members above and for the same reason: this enum crosses the wire as the
    ///         member's NAME and clients may have persisted its ordinal.
    ///     </para>
    /// </summary>
    GraphWorkflowGateAlreadyDecided,

    /// <summary>
    ///     A second lifecycle command on an external-app instance whose per-instance gate is already held. Wait, or
    ///     cancel the operation that holds it.
    ///     <para>
    ///         APPENDED, like the three members above and for the same reason: this enum crosses the wire as the
    ///         member's NAME and clients may have persisted its ordinal.
    ///     </para>
    /// </summary>
    ExternalAppOperationInFlight,

    /// <summary>
    ///     A command the instance's current status forbids — starting one already running, reconfiguring one that is
    ///     not stopped, cancelling with nothing in flight. Re-read the instance.
    /// </summary>
    ExternalAppInvalidTransition,

    /// <summary>
    ///     Installing an application that already has an instance. V1 allows one per application; the schema supports
    ///     N, so this is a product rule rather than a limit of the model.
    /// </summary>
    ExternalAppAlreadyInstalled,

    /// <summary>
    ///     An update whose new manifest declares WIDER permissions than the installed snapshot. Nothing was changed:
    ///     the operator re-confirms the added permissions — carried as <c>addedPermissions</c> — and retries.
    /// </summary>
    ExternalAppPermissionChangeRequiresAcknowledgement,

    /// <summary>
    ///     The manifest moved between the preview and the command: the version or the sha256 the request echoed is not
    ///     what the catalog now serves. Nothing was changed — the client re-fetches the preview and re-shows the
    ///     permissions step, because an acceptance only ever covers the manifest that was read.
    /// </summary>
    ExternalAppManifestChanged,

    /// <summary>
    ///     A write lost a race — the reconciler moves status while an operator saves variables, or a command carries a
    ///     stale <c>expectedVersion</c>. Ordinary rather than exceptional: refresh and retry.
    /// </summary>
    ExternalAppVersionConflict
}
