namespace XE_Local_AI_Engine.Client.Common.ProblemDetailModels.Enums;

/// <summary>
///     Enumerates supported node conflict problem type values.
/// </summary>
/// <remarks>
///     This enum crosses the wire as the member's NAME — <c>ConflictExceptionHandler</c> writes
///     <c>conflictType.Value.ToString()</c> — and clients may have persisted its ordinals, so a new member is APPENDED:
///     inserting one above an existing member would shift every name's ordinal after it.
/// </remarks>
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
    ///     A model-lifecycle operation was asked of a runtime provider that does not own it.
    /// </summary>
    /// <remarks>
    ///     Deleting a model served by an operator-registered external endpoint, which is removed by unregistering it on
    ///     its connection instead. Not retryable: the request names the wrong lifecycle, not a transient state.
    /// </remarks>
    ModelOperationNotSupportedByProvider,

    /// <summary>
    ///     A work-session lifecycle call the session's current status forbids — starting one that is already running,
    ///     deleting one mid-step, or repointing the objective of a live run. The operator cancels or pauses first.
    /// </summary>
    WorkSessionInvalidTransition,

    /// <summary>
    ///     A work-session write lost a race with a concurrent one.
    /// </summary>
    /// <remarks>
    ///     Two writers touch a running session by design (the supervisor moves the status while the state tools write
    ///     tasks and findings), so this is ordinary rather than exceptional: refresh and retry.
    /// </remarks>
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
    ///     replay a repeated one is.
    /// </summary>
    /// <remarks>
    ///     The body carries <c>standingDecision</c>, so the UI can say what was decided instead of only that the click
    ///     failed.
    /// </remarks>
    DevWorkflowGateAlreadyDecided,

    /// <summary>
    ///     The work item already has a run in flight, and v1 allows one at a time. Starting another, or deleting the
    ///     work item, waits for that run to finish or cancels it first.
    /// </summary>
    DevWorkflowRunInFlight,

    /// <summary>
    ///     Both ways a graph-workflow definition write can lose, under one member because from the client's side they
    ///     are one story — somebody else got there first.
    /// </summary>
    /// <remarks>
    ///     A stale <c>version</c> on an update, and a delete refused while a live run still pins the definition.
    ///     Refresh the definition, or cancel the run, and retry.
    /// </remarks>
    GraphWorkflowDefinitionConflict,

    /// <summary>
    ///     Every way a graph-workflow RUN write can lose, under one member for the same reason the definition member
    ///     above holds two stories: you are acting on a version of this run that no longer exists.
    /// </summary>
    /// <remarks>
    ///     A stale <c>definitionVersion</c> at start, a cancel of a run that has already finished, a request id reused
    ///     on a different definition, and a status move a concurrent writer got to first. Re-read the run and decide
    ///     again.
    /// </remarks>
    GraphWorkflowRunConflict,

    /// <summary>
    ///     A second human act on a graph-workflow pause that is already answered — a NEW operation id on a decided
    ///     row, or an id that already decided a different pause of the same run.
    /// </summary>
    /// <remarks>
    ///     The body carries <c>standingDecision</c>, so the UI can say what was decided instead of only that the click
    ///     failed.
    /// </remarks>
    GraphWorkflowGateAlreadyDecided,

    /// <summary>
    ///     A second lifecycle command on an external-app instance whose per-instance gate is already held — wait, or
    ///     cancel the operation that holds it.
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
    ///     what the catalog now serves.
    /// </summary>
    /// <remarks>
    ///     Nothing was changed — the client re-fetches the preview and re-shows the permissions step, because an
    ///     acceptance only ever covers the manifest that was read.
    /// </remarks>
    ExternalAppManifestChanged,

    /// <summary>
    ///     A write lost a race — the reconciler moves status while an operator saves variables, or a command carries a
    ///     stale <c>expectedVersion</c>. Ordinary rather than exceptional: refresh and retry.
    /// </summary>
    ExternalAppVersionConflict
}
