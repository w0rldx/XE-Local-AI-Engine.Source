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
    InstalledModelProviderMapSuperseded
}
