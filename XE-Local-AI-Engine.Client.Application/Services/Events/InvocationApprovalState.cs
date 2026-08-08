namespace XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     The live "a tool approval is waiting on the operator" slot on <see cref="InvocationState" />.
///     <para>
///         <see cref="CallId" /> and <see cref="ToolName" /> were added for the reconnect replay: a browser that
///         reloads mid-turn is re-sent the pending approval, and it can only reattach the Approve/Deny controls to the
///         right tool-call card if it knows which call the approval belongs to. Both are optional so a platform-hub
///         approval — which carries only an id and a description — still round-trips unchanged.
///     </para>
/// </summary>
public sealed record InvocationApprovalState(
    string RequestId,
    string Description,
    DateTimeOffset RequestedAt)
{
    /// <summary>The tool-call id the approval belongs to, when known. Null for a platform-hub approval.</summary>
    public string? CallId { get; init; }

    /// <summary>The tool awaiting approval, when known. Null for a platform-hub approval.</summary>
    public string? ToolName { get; init; }
}
