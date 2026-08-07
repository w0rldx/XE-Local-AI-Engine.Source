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
    WorkerTokenExpired
}
