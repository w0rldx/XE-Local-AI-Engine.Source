namespace XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     The <c>status</c> of a node run's output document — the values a condition on an out-edge may compare it
///     against.
///     <para>
///         Deliberately its own closed vocabulary rather than the node-run status enum spelled in lowercase: a
///         definition author writing <c>status eq "succeeded"</c> is reading THIS, and the two would drift the moment
///         either grew a member the other has no use for.
///     </para>
/// </summary>
internal static class GraphWorkflowNodeOutputStatuses
{
    public const string Succeeded = "succeeded";

    public const string Failed = "failed";

    public const string Skipped = "skipped";
}
